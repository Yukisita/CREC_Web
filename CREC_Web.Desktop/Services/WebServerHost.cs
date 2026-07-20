using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;

namespace CREC_Web.Desktop.Services;

internal sealed class WebServerHost : IAsyncDisposable
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    // デスクトップアプリ用に Web サーバー子プロセスを起動し、接続可能になるまで待機する。
    public async Task<WebServerSession> StartAsync(DesktopLaunchSettings settings, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("The web server is already running.");
        }

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

    // Web 単体でも解釈できる CLI 引数へ正規化し、desktop 側の起動要求を子プロセスへ橋渡しする。
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

    // 起動直後は HTTP 応答確認より軽い TCP 接続確認で、待受開始だけを素早く検出する。
    private static async Task WaitForServerAsync(Process process, int port, CancellationToken cancellationToken)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(30);

        while (DateTime.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                throw new InvalidOperationException("The CREC Web server exited before startup completed.");
            }

            if (await IsPortOpenAsync(port, cancellationToken))
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out while waiting for the CREC Web server to start.");
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
