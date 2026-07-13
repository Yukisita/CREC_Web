/*
CREC Web - Main Program
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Net;
using CREC_Web.Services;
using Microsoft.Extensions.FileProviders;

Console.WriteLine("Starting CREC Web Server...");

var startupOptions = StartupOptions.Parse(args);

// Webアプリケーションビルダーの作成
var builder = WebApplication.CreateBuilder(args);
var projectSettingsService = new ProjectSettingsService(builder.Configuration);

// CRECのプロジェクトファイルのパスを取得
var crecFilePath = ResolveProjectFilePath(startupOptions);
ProjectSettings? projectSettings = null;

// CRECのプロジェクトファイルを読み込み、プロジェクト設定を取得
Console.WriteLine($"Loading project settings from: {crecFilePath}");
projectSettings = projectSettingsService.LoadProjectSettings(crecFilePath);

// プロジェクト設定を適用
if (projectSettings != null)
{
    projectSettingsService.ApplyProjectSettings(projectSettings, crecFilePath);
}
else
{
    Console.WriteLine("Warning: Failed to parse .crec file or extract project settings");
}

// wwwrootフォルダのパスを設定
var executablePath = AppContext.BaseDirectory;
var webRootPath = Path.Combine(executablePath, "wwwroot");
builder.Environment.WebRootPath = webRootPath;

// Add services to the container
builder.Services.AddControllersWithViews();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton(projectSettingsService);

// Add CREC data service
builder.Services.AddSingleton<CrecDataService>();

// Add CORS for browser access
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// URL設定 (HTTPSはカメラアクセスに必要)
var port = ResolvePort(startupOptions);
var bindHost = ResolveBindHost(startupOptions);

Console.WriteLine($"Using ports: HTTP={port}, HTTPS={port + 1}");
builder.WebHost.UseUrls($"http://{bindHost}:{port}", $"https://{bindHost}:{port + 1}");

var app = builder.Build();

app.UseCors();

// Configure static files middleware
if (Directory.Exists(webRootPath))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(webRootPath)
    });
}

app.UseRouting();

app.MapControllers();
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// 起動情報を表示
var logger = app.Services.GetRequiredService<ILogger<Program>>();
if (projectSettings != null)
{
    logger.LogInformation("Project: {ProjectName}", projectSettings.ProjectName);
    logger.LogInformation("Data folder (from .crec file): {ProjectDataPath}", projectSettings.ProjectDataPath);
}
else
{
    logger.LogInformation("Data folder (current directory): {CurrentDirectory}", Environment.CurrentDirectory);
}
logger.LogInformation("Executable directory: {ExecutablePath}", executablePath);
logger.LogInformation("Web root path: {WebRootPath}", webRootPath);
logger.LogInformation("wwwroot exists: {WebRootExists}", Directory.Exists(webRootPath));
logger.LogInformation("Bind host: {BindHost}", bindHost);
logger.LogInformation("Web interface will be available at:");
logger.LogInformation("  - http://localhost:{Port} (HTTP)", port);
logger.LogInformation("  - https://localhost:{Port} (HTTPS)", port + 1);
if (!IsLoopbackHost(bindHost))
{
    logger.LogInformation("  - https://[your-ip]:{Port}", port + 1);
}
logger.LogInformation("API documentation available at: https://localhost:{Port}/swagger", port + 1);

// シャットダウンハンドラの設定
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var isShuttingDown = 0; // シャットダウン処理の重複実行を防ぐフラグ (0=実行中でない, 1=実行中)

if (startupOptions.NonInteractive && Console.IsInputRedirected)
{
    logger.LogInformation("Waiting for shutdown command on standard input.");
    _ = Task.Run(() => MonitorShutdownCommands(lifetime));
}
else
{
    logger.LogInformation("Press Ctrl+Q to initiate server shutdown.");

    // Ctrl+Qの入力を監視するバックグラウンドタスク
    _ = Task.Run(() =>
    {
        try
        {
            while (!lifetime.ApplicationStopping.IsCancellationRequested)
            {
                // Console.KeyAvailable を使用してブロッキングを回避
                if (Console.KeyAvailable)
                {
                    var keyInfo = Console.ReadKey(intercept: true);

                    // Ctrl+Q (Q key with Control modifier)
                    if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        // Interlocked.CompareExchange でスレッドセーフな比較と交換
                        if (Interlocked.CompareExchange(ref isShuttingDown, 1, 0) == 0)
                        {
                            Console.WriteLine("\nCtrl+Q detected. Do you want to shut down the server? (Y/N): ");
                            var response = Console.ReadLine()?.Trim().ToUpperInvariant();

                            if (response == "Y")
                            {
                                Console.WriteLine("Shutting down the server gracefully...");
                                lifetime.StopApplication(); // アプリケーションの適切なシャットダウンを要求
                            }
                            else
                            {
                                Console.WriteLine("Shutdown canceled. Server continues running.");
                                Interlocked.Exchange(ref isShuttingDown, 0); // フラグをリセット
                            }
                        }
                    }
                }
                else
                {
                    // キー入力がない場合は少し待機してCPU使用率を抑える
                    Thread.Sleep(100);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in shutdown monitor: {ex.Message}");
        }
    });
}

app.Run();

// .crec は desktop / server の両方で同じ必須入力とし、未指定時だけ対話入力で補完する。
static string ResolveProjectFilePath(StartupOptions startupOptions)
{
    if (!string.IsNullOrWhiteSpace(startupOptions.ProjectPath))
    {
        return startupOptions.ProjectPath;
    }

    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException("No .crec file specified. Please set the project path before startup.");
    }

    // CRECファイルがコマンドライン引数に指定されていない場合、手動でのパス入力を待機
    Console.WriteLine("No .crec file specified. Please enter the .crec file path:");
    var inputPath = Console.ReadLine()?.Trim();
    return inputPath ?? string.Empty;
}

// 起動ポートは desktop / server の両方で同じ扱いにし、未指定時だけ対話入力で決定する。
static int ResolvePort(StartupOptions startupOptions)
{
    if (startupOptions.Port is int configuredPort)
    {
        if (ArePortsAvailable(configuredPort))
        {
            return configuredPort;
        }

        throw new InvalidOperationException($"Port {configuredPort} is already in use. Please specify a different port.");
    }

    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException("No port specified. Please set the startup port before launch.");
    }

    while (true)
    {
        Console.Write("Please enter the project port number (1-65534): ");
        var inputPort = Console.ReadLine();
        var port = 5000;

        if (!string.IsNullOrWhiteSpace(inputPort))
        {
            inputPort = inputPort.Trim();
            if (int.TryParse(inputPort, out var parsedPort))
            {
                port = parsedPort;
            }
            else
            {
                Console.WriteLine("Invalid port input. Use default port.");
            }
        }
        else
        {
            Console.WriteLine("No port input. Use default port.");
        }

        if (ArePortsAvailable(port))
        {
            return port;
        }
    }
}

// 公開範囲も desktop / server で同じ明示設定にそろえ、未指定時だけ対話入力で選択する。
static string ResolveBindHost(StartupOptions startupOptions)
{
    if (!string.IsNullOrWhiteSpace(startupOptions.BindHost))
    {
        return startupOptions.BindHost;
    }

    if (Console.IsInputRedirected)
    {
        throw new InvalidOperationException("No bind host specified. Please set --local-only, --public, or --bind-host before launch.");
    }

    while (true)
    {
        Console.WriteLine("Please choose the startup scope:");
        Console.WriteLine("  1: Local only (127.0.0.1)");
        Console.WriteLine("  2: Web server as public (0.0.0.0)");
        Console.Write("Enter 1 or 2: ");
        var input = Console.ReadLine()?.Trim();

        if (input == "1")
        {
            return "127.0.0.1";
        }

        if (input == "2")
        {
            return "0.0.0.0";
        }

        Console.WriteLine("Invalid input. Please enter 1 or 2.");
    }
}

static bool ArePortsAvailable(int port)
{
    return IsPortAvailable(port) && IsPortAvailable(port + 1);
}

static bool IsPortAvailable(int port)
{
    // ポートが設定可能範囲内の数値か確認
    if (port < 1 || port > 65535)
    {
        Console.WriteLine($"Port {port} is out of valid range (1-65535). Please enter a port between 1 and 65534 (to allow for HTTP port + 1).");
        return false;
    }

    // ポートが使用中か確認
    try
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Any, port);
        listener.Start();
        listener.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException) // ポートが使用中の場合
    {
        Console.WriteLine($"Port {port} is already in use. Please try a different port or press Enter to use the default port.");
        return false;
    }
    catch (Exception ex) // その他の例外処理
    {
        Console.WriteLine($"Unexpected error when checking port {port}: {ex.Message}");
        return false;
    }
}

static bool IsLoopbackHost(string host)
{
    if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
}

// デスクトップホストから標準入力経由で "shutdown" が送られたときだけ停止を受け付ける。
static void MonitorShutdownCommands(IHostApplicationLifetime lifetime)
{
    try
    {
        while (!lifetime.ApplicationStopping.IsCancellationRequested)
        {
            var command = Console.ReadLine();
            if (command is null)
            {
                break;
            }

            if (string.Equals(command.Trim(), "shutdown", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Shutdown command received.");
                lifetime.StopApplication();
                break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error in shutdown command monitor: {ex.Message}");
    }
}

file sealed class StartupOptions
{
    public string? ProjectPath { get; init; }
    public int? Port { get; init; }
    public string? BindHost { get; init; }
    public bool NonInteractive { get; init; }

    // Web 単体起動とデスクトップホスト起動の両方で同じ起動オプションを解釈する。
    public static StartupOptions Parse(string[] args)
    {
        string? projectPath = null;
        int? port = null;
        string? bindHost = null;
        var nonInteractive = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            switch (argument)
            {
                case "--project":
                    projectPath = GetValue(args, ref i, argument);
                    break;
                case "--port":
                    port = ParsePort(GetValue(args, ref i, argument), argument);
                    break;
                case "--bind-host":
                    bindHost = GetValue(args, ref i, argument);
                    break;
                case "--non-interactive":
                    nonInteractive = true;
                    break;
                case "--local-only":
                    bindHost = "127.0.0.1";
                    break;
                case "--public":
                    bindHost = "0.0.0.0";
                    break;
                default:
                    if (argument.StartsWith("--project=", StringComparison.OrdinalIgnoreCase))
                    {
                        projectPath = argument["--project=".Length..];
                    }
                    else if (argument.StartsWith("--port=", StringComparison.OrdinalIgnoreCase))
                    {
                        port = ParsePort(argument["--port=".Length..], "--port");
                    }
                    else if (argument.StartsWith("--bind-host=", StringComparison.OrdinalIgnoreCase))
                    {
                        bindHost = argument["--bind-host=".Length..];
                    }
                    else if (projectPath is null && argument.EndsWith(".crec", StringComparison.OrdinalIgnoreCase))
                    {
                        projectPath = argument;
                    }
                    break;
            }
        }

        return new StartupOptions
        {
            ProjectPath = projectPath,
            Port = port,
            BindHost = string.IsNullOrWhiteSpace(bindHost) ? null : bindHost.Trim(),
            NonInteractive = nonInteractive
        };
    }

    private static string GetValue(string[] args, ref int index, string argumentName)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {argumentName}.");
        }

        index++;
        return args[index];
    }

    private static int? ParsePort(string? value, string source)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var parsedPort))
        {
            return parsedPort;
        }

        Console.WriteLine($"Invalid {source} value: {value}");
        return null;
    }
}
