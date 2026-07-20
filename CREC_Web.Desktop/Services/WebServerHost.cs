using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;

namespace CREC_Web.Desktop.Services;

internal sealed class WebServerHost : IAsyncDisposable
{
    private Process? _process;// Web サーバー子プロセスの参照
    public bool IsRunning => _process is { HasExited: false };// サーバの起動状態を確認するためのプロパティ

    /// <summary>
    /// デスクトップアプリ用に Web サーバー子プロセスを起動し、接続可能になるまで待機する。
    /// </summary>
    /// <param name="settings">起動設定値</param>
    /// <param name="cancellationToken">キャンセルトークン</param>
    /// <returns>Web サーバーセ</returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="FileNotFoundException"></exception>
    public async Task<WebServerSession> StartAsync(DesktopLaunchSettings settings, CancellationToken cancellationToken = default)
    {
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
        var process = new Process
        {
            StartInfo = CreateStartInfo(webAppDirectory, projectFilePath, port, settings.PublishToNetwork),
            EnableRaisingEvents = true
        };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start the CREC Web server process.");
        }

        _process = process;

        try
        {
            await WaitForServerAsync(process, port, cancellationToken);
            return new WebServerSession(port, new Uri($"http://localhost:{port}", UriKind.Absolute));
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    // まず標準入力の shutdown による正常終了を試し、応答しない場合だけ強制終了へ切り替える。
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;

        if (process is null)
        {
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

                if (!await WaitForExitAsync(process, TimeSpan.FromSeconds(5)))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(StopAsync());
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
    private static async Task WaitForServerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(30);// 30 秒以内に接続可能にならなければタイムアウトとする

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)// プロセスが終了している場合は接続確認を行わずに例外をスローする
            {
                throw new InvalidOperationException("The CREC Web server exited before startup completed.");
            }

            if (await IsPortOpenAsync(port, cancellationToken))// 指定したポートで接続可能になった場合は待機を終了する
            {
                return;
            }

            await Task.Delay(250, cancellationToken);// 250 ミリ秒ごとに接続確認を行う
        }

        throw new TimeoutException("Timed out while waiting for the CREC Web server to start.");// タイムアウトとして例外をスローする
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        var exitTask = process.WaitForExitAsync();
        var completedTask = await Task.WhenAny(exitTask, Task.Delay(timeout));
        return completedTask == exitTask;
    }

    private static async Task<bool> IsPortOpenAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // 配布物に同梱した web 出力だけを起動対象にし、desktop 単体で完結できるようにする。
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

internal sealed record DesktopLaunchSettings(string ProjectFilePath, int Port, bool PublishToNetwork);

internal sealed record WebServerSession(int Port, Uri FrontendUri);
