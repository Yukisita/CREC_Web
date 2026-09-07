using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;

namespace CREC_Web.Desktop.Services;

internal sealed class WebServerHost
{
    private Process? _process;// Web サーバー子プロセスの参照
    private NamedPipeServerStream? _statePipe;
    private CancellationTokenSource? _stateCancellation;
    private Task? _stateReader;
    private string? _adminToken;
    private DesktopProjectState? _currentState;
    public DesktopProjectState? CurrentProject => Volatile.Read(ref _currentState);
    public event Action<DesktopProjectState>? ProjectChanged;
    public bool IsRunning => _process is { HasExited: false };// サーバの起動状態を確認するためのプロパティ

    /// <summary>
    /// デスクトップアプリ用に Web サーバー子プロセスを起動し、接続可能になるまで待機する。
    /// </summary>
    /// <param name="settings">起動設定値</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>Web サーバーセッション</returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    public async Task<WebServerSession> StartAsync(DesktopLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: true } exitedProcess)
        {
            exitedProcess.Dispose();
            _process = null;
            await StopStateChannelAsync();
        }

        // すでに起動中の場合は例外をスローする
        if (IsRunning)
        {
            throw new InvalidOperationException("The web server is already running.");
        }
        // 起動設定で指定しているプロジェクトファイルの存在を確認する
        var projectFilePath = Path.GetFullPath(settings.ProjectFilePath);
        if (!File.Exists(projectFilePath))
        {
            throw new FileNotFoundException("The selected .crec project file was not found.", projectFilePath);
        }

        var webAppDirectory = ResolveWebAppDirectory();
        var port = settings.Port;
        var pipeName = "crec-desktop-" + Guid.NewGuid().ToString("N");
        _statePipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _stateCancellation = new CancellationTokenSource();
        _stateReader = ReadProjectStatesAsync(_statePipe, _stateCancellation.Token);
        _adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Volatile.Write(ref _currentState, null);
        var process = new Process
        {
            StartInfo = CreateStartInfo(webAppDirectory, projectFilePath, port, settings.PublishToNetwork),
            EnableRaisingEvents = true
        };
        process.StartInfo.Environment["CREC_ADMIN_TOKEN"] = _adminToken;
        process.StartInfo.Environment["CREC_DESKTOP_PIPE"] = pipeName;

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start the CREC Web server process.");
            _process = process;
            await WaitForServerAsync(process, port, cancellationToken);
            return new WebServerSession(port, new Uri($"http://localhost:{port}", UriKind.Absolute));
        }
        catch
        {
            await StopAsync();
            process.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Web サーバー子プロセスを停止する
    /// </summary>
    /// <returns></returns>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;

        if (process is null)
        {
            await StopStateChannelAsync();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("shutdown");
                    await process.StandardInput.FlushAsync();
                }
                catch
                {
                    // 標準入力に送れない場合は強制終了のフォールバックを行う
                }

                if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(35)))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            await StopStateChannelAsync();
            process.Dispose();
        }
    }

    private async Task StopStateChannelAsync()
    {
        // EOF follows the final state after graceful shutdown. Read it before restarting.
        if (_statePipe?.IsConnected != true) _stateCancellation?.Cancel();
        if (_stateReader is not null)
        {
            try { await _stateReader.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _stateCancellation?.Cancel(); }
        }
        _stateCancellation?.Cancel();
        _statePipe?.Dispose();
        _stateCancellation?.Dispose();
        _statePipe = null;
        _stateCancellation = null;
        _stateReader = null;
    }

    private async Task ReadProjectStatesAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(pipe);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var state = JsonSerializer.Deserialize<DesktopProjectState>(line);
                if (state is null || !Path.IsPathFullyQualified(state.FilePath)) continue;
                Volatile.Write(ref _currentState, state);
                ProjectChanged?.Invoke(state);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Server shutdown or canceled startup closes the private channel.
        }
    }

    public async Task<Cookie> CreateAdministratorSessionAsync(Uri frontendUri)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies };
        using var client = new HttpClient(handler) { BaseAddress = frontendUri };
        client.DefaultRequestHeaders.Add("X-CREC-Request", "1");
        using var response = await client.PostAsJsonAsync("/api/projects/login", new { token = _adminToken });
        response.EnsureSuccessStatusCode();
        return cookies.GetCookies(new Uri(frontendUri, "/api/projects")).Cast<Cookie>().Single();
    }

    /// <summary>
    /// Web サーバー子プロセスの起動情報を作成する
    /// </summary>
    /// <param name="webAppDirectory">Web アプリケーションのディレクトリ</param>
    /// <param name="projectFilePath">プロジェクトファイルのパス</param>
    /// <param name="port">使用するポート番号</param>
    /// <param name="publishToNetwork">ネットワーク公開フラグ</param>
    /// <returns>プロセスの起動情報</returns>
    private static ProcessStartInfo CreateStartInfo(string webAppDirectory, string projectFilePath, int port, bool publishToNetwork)
    {
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = webAppDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true
        };

        var executablePath = Path.Combine(webAppDirectory, "CREC_Web.exe");
        if (File.Exists(executablePath))
        {
            startInfo.FileName = executablePath;
        }
        else
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add(Path.Combine(webAppDirectory, "CREC_Web.dll"));
        }

        startInfo.ArgumentList.Add("--non-interactive");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectFilePath);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(publishToNetwork ? "--public" : "--local-only");

        return startInfo;
    }

    /// <summary>
    /// Web サーバー子プロセスが指定したポートで接続可能になるまで待機する
    /// </summary>
    /// <param name="process">Web サーバー子プロセス</param>
    /// <param name="port">接続確認を行うポート番号</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="TimeoutException"></exception>
    private async Task WaitForServerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(30);// 30 秒以内に接続可能にならなければタイムアウトとする
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)// プロセスが終了している場合は接続確認を行わずに例外をスローする
            {
                throw new InvalidOperationException("The CREC Web server exited before startup completed.");
            }

            // Verify the revision received over private IPC before sending any administrator
            // credential. An unrelated process listening on the requested port is not readiness.
            if (CurrentProject is { } expected)
            {
                try
                {
                    var status = await client.GetFromJsonAsync<DesktopServerStatus>(
                        $"http://127.0.0.1:{port}/api/projects/status", cancellationToken);
                    if (status?.Revision == expected.Revision) return;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
            }

            await Task.Delay(250, cancellationToken);// 250 ミリ秒ごとに接続確認を行う
        }

        throw new TimeoutException("Timed out while waiting for the CREC Web server to start.");// タイムアウトとして例外をスローする
    }

    /// <summary>
    /// Web サーバー子プロセスが終了するまで待機する
    /// </summary>
    /// <param name="process">Web サーバー子プロセス</param>
    /// <param name="timeout">待機するタイムアウト時間(ミリ秒)</param>
    /// <returns>プロセスが指定したタイムアウト内に終了した場合は true、それ以外の場合は false</returns>
    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(exitTask, Task.Delay(timeout));
        return completedTask == exitTask;
    }

    /// <summary>
    /// Web アプリケーションのディレクトリを解決する
    /// </summary>
    /// <returns></returns>
    /// <exception cref="DirectoryNotFoundException"></exception>
    private static string ResolveWebAppDirectory()
    {
        var webAppDirectory = Path.Combine(AppContext.BaseDirectory, "web");
        var webAppAssemblyPath = Path.Combine(webAppDirectory, "CREC_Web.dll");

        if (!File.Exists(webAppAssemblyPath))
        {
            throw new DirectoryNotFoundException("The packaged CREC Web files were not found. Build the desktop project after the web project so the web output is copied to the desktop app.");
        }

        return webAppDirectory;
    }
}

/// <summary>
/// デスクトップアプリから Web サーバーを起動する際の設定値を保持するレコード
/// </summary>
/// <param name="ProjectFilePath">プロジェクトファイルのパス</param>
/// <param name="Port">使用するポート番号</param>
/// <param name="PublishToNetwork">ネットワークに公開するかどうか</param>
internal sealed record DesktopLaunchSettings(string ProjectFilePath, int Port, bool PublishToNetwork);

/// <summary>
/// デスクトップアプリから Web サーバーを起動した際のセッション情報を保持するレコード
/// </summary>
/// <param name="Port">使用するポート番号</param>
/// <param name="FrontendUri">フロントエンドの URI</param>
internal sealed record WebServerSession(int Port, Uri FrontendUri);

internal sealed record DesktopProjectState(string Revision, string FilePath, string Name);
internal sealed record DesktopServerStatus(string Revision);
