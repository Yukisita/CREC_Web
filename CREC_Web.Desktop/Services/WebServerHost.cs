using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace CREC_Web.Desktop.Services;

/// <summary>Web 子プロセスの起動・停止と、現在のプロジェクトの受信を管理する。</summary>
internal sealed class WebServerHost
{
    private Process? _process;// Web サーバー子プロセスの参照
    private NamedPipeServerStream? _statePipe;// 同じ OS ユーザー専用の通知経路。
    private CancellationTokenSource? _stateCancellation;// 受信待ちの中止用。
    private Task? _stateReader;// 再起動前に最終通知を読み切るための待機対象。
    private DesktopProjectState? _currentState;// 停止後も保持し、再起動先に使う。

    /// <summary>子プロセスから最後に受信したプロジェクト状態。初回受信前は null。</summary>
    public DesktopProjectState? CurrentProject => Volatile.Read(ref _currentState);

    /// <summary>状態の受信通知。UI 更新は購読側で UI スレッドへ渡す。</summary>
    public event Action<DesktopProjectState>? ProjectChanged;

    /// <summary>子プロセスが稼働中か。</summary>
    public bool IsRunning => _process is { HasExited: false };

    /// <summary>子サーバーを起動し、接続可能になるまで待つ。</summary>
    /// <param name="settings">起動設定値</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>Web サーバーセッション</returns>
    /// <exception cref="InvalidOperationException">既に起動中、または起動完了前に子プロセスが終了した場合。</exception>
    /// <exception cref="FileNotFoundException">指定したプロジェクトファイルが存在しない場合。</exception>
    public async Task<WebServerSession> StartAsync(DesktopLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        if (_process is { HasExited: true } exitedProcess)
        {
            exitedProcess.Dispose();
            _process = null;
            await StopStateChannelAsync();
        }

        if (IsRunning)
            throw new InvalidOperationException("The web server is already running.");
        var projectFilePath = Path.GetFullPath(settings.ProjectFilePath);
        if (!File.Exists(projectFilePath))
            throw new FileNotFoundException("The selected .crec project file was not found.", projectFilePath);

        var webAppDirectory = ResolveWebAppDirectory();
        var port = settings.Port;
        // 起動ごとに専用パイプを作り、HTTP の応答元との照合に使う。
        var pipeName = "crec-desktop-" + Guid.NewGuid().ToString("N");
        _statePipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _stateCancellation = new CancellationTokenSource();
        _stateReader = ReadProjectStatesAsync(_statePipe, _stateCancellation.Token);
        Volatile.Write(ref _currentState, null);
        var process = new Process
        {
            StartInfo = CreateStartInfo(webAppDirectory, projectFilePath, port, settings.PublishToNetwork),
            EnableRaisingEvents = true
        };
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
            // 起動失敗時もプロセスとパイプを解放する。
            await StopAsync();
            process.Dispose();
            throw;
        }
    }

    /// <summary>子サーバーを停止する。35秒を超えたら強制終了する。</summary>
    /// <returns>停止と最終通知の受信完了。</returns>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;

        try
        {
            if (process is null || process.HasExited) return;

            try
            {
                // 保存中の要求を完了させるため、まず通常終了を依頼する。
                await process.StandardInput.WriteLineAsync("shutdown");
                await process.StandardInput.FlushAsync();
            }
            catch
            {
                // 入力が閉じられていても終了を待つ。
            }

            var exitTask = process.WaitForExitAsync();
            if (await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(35))) == exitTask)
                return;

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        finally
        {
            await StopStateChannelAsync();
            process?.Dispose();
        }
    }

    /// <summary>最終通知を読み切り、通知パイプを閉じる。</summary>
    /// <returns>受信終了と解放の完了。最大5秒待機。</returns>
    private async Task StopStateChannelAsync()
    {
        // 接続済みなら最終通知と EOF を待つ。未接続なら中止する。
        if (_statePipe?.IsConnected != true)
            _stateCancellation?.Cancel();
        if (_stateReader is not null)
        {
            try
            {
                await _stateReader.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                _stateCancellation?.Cancel();
            }
        }
        _stateCancellation?.Cancel();
        _statePipe?.Dispose();
        _stateCancellation?.Dispose();
        _statePipe = null;
        _stateCancellation = null;
        _stateReader = null;
    }

    /// <summary>子プロセスの最新状態を受信し、購読先へ通知する。</summary>
    /// <param name="pipe">状態の受信パイプ。</param>
    /// <param name="cancellationToken">受信の中止通知。</param>
    /// <returns>EOF または中止までの受信タスク。</returns>
    private async Task ReadProjectStatesAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken);
            using var reader = new StreamReader(pipe);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                var state = JsonSerializer.Deserialize<DesktopProjectState>(line);
                if (state is null || !Path.IsPathFullyQualified(state.FilePath))
                    continue;
                Volatile.Write(ref _currentState, state);
                ProjectChanged?.Invoke(state);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // 停止・起動中止によるパイプの切断は正常終了とする。
        }
    }

    /// <summary>Web サーバー子プロセスの起動情報を作成する</summary>
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

    /// <summary>HTTP と通知の世代が一致するまで起動を待つ。</summary>
    /// <param name="process">Web サーバー子プロセス</param>
    /// <param name="port">接続確認を行うポート番号</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>起動確認の完了を待つタスク。</returns>
    /// <exception cref="InvalidOperationException">起動確認中に子プロセスが終了した場合。</exception>
    /// <exception cref="TimeoutException">30秒以内に起動を確認できなかった場合。</exception>
    private async Task WaitForServerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(30); // 起動待ちの期限。
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
                throw new InvalidOperationException("The CREC Web server exited before startup completed.");

            if (await IsServerReadyAsync(client, port, cancellationToken))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out while waiting for the CREC Web server to start.");
    }

    /// <summary>HTTP の接続先が今回の子プロセスか確認する。</summary>
    /// <param name="client">起動確認用の HTTP クライアント。</param>
    /// <param name="port">子プロセスへ指定した HTTP ポート。</param>
    /// <param name="cancellationToken">起動確認の中止を通知するトークン。</param>
    /// <returns>パイプと HTTP の世代が一致すれば true。</returns>
    private async Task<bool> IsServerReadyAsync(HttpClient client, int port, CancellationToken cancellationToken)
    {
        var expectedState = CurrentProject;
        if (expectedState is null)
            return false;

        try
        {
            // 別プロセスの応答を起動完了と誤認しない。
            var status = await client.GetFromJsonAsync<DesktopServerStatus>(
                $"http://127.0.0.1:{port}/api/projects/status", cancellationToken);
            return status?.Revision == expectedState.Revision;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // 起動途中の接続失敗は待機ループで再確認する。
            return false;
        }
    }

    /// <summary>Web アプリケーションのディレクトリを解決する</summary>
    /// <returns>デスクトップ実行ファイルの隣にある web フォルダの絶対パス。</returns>
    /// <exception cref="DirectoryNotFoundException">Web アプリケーションの DLL が配置されていない場合。</exception>
    private static string ResolveWebAppDirectory()
    {
        var webAppDirectory = Path.Combine(AppContext.BaseDirectory, "web");
        var webAppAssemblyPath = Path.Combine(webAppDirectory, "CREC_Web.dll");

        if (!File.Exists(webAppAssemblyPath))
            throw new DirectoryNotFoundException("The packaged CREC Web files were not found. Build the desktop project after the web project so the web output is copied to the desktop app.");

        return webAppDirectory;
    }
}

/// <summary>子サーバーの起動設定。</summary>
/// <param name="ProjectFilePath">プロジェクトファイルのパス</param>
/// <param name="Port">使用するポート番号</param>
/// <param name="PublishToNetwork">ネットワークに公開するかどうか</param>
internal sealed record DesktopLaunchSettings(string ProjectFilePath, int Port, bool PublishToNetwork);

/// <summary>起動した子サーバーの接続先。</summary>
/// <param name="Port">使用するポート番号</param>
/// <param name="FrontendUri">フロントエンドの URI</param>
internal sealed record WebServerSession(int Port, Uri FrontendUri);

/// <summary>専用パイプから受信する、同じ時点のプロジェクト状態。</summary>
/// <param name="Revision">Web サーバーが発行する世代識別子。</param>
/// <param name="FilePath">次回起動で再利用する .crec ファイルの絶対パス。</param>
/// <param name="Name">ウィンドウタイトルに表示する名前。</param>
internal sealed record DesktopProjectState(string Revision, string FilePath, string Name);

/// <summary>HTTP の起動確認で使うプロジェクト状態。</summary>
/// <param name="Revision">専用パイプの通知と照合する世代識別子。</param>
internal sealed record DesktopServerStatus(string Revision);
