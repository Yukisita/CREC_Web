/*
CREC Web - Main Program
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Net;
using CREC_Web.Services;
using Microsoft.Extensions.FileProviders;

Console.WriteLine("Starting CREC Web Server...");

// コマンドライン引数
string? startupProjectPath = null;// CRECのプロジェクトファイルのパス
int? startupPort = null;// 起動ポート番号
string? startupBindHost = null;// バインドホスト (例: "127.0.0.1" または "0.0.0.0")
var nonInteractive = false;// 非対話モード (標準入力からのシャットダウンコマンドを受け付ける)

// コマンドライン引数の解析
for (var i = 0; i < args.Length; i++)
{
    var argument = args[i];

    switch (argument)
    {
        case "--project":// CRECのプロジェクトファイルのパスを指定
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for --project.");
            }
            startupProjectPath = args[++i];
            break;
        case "--port":// 起動ポート番号を指定
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for --port.");
            }
            if (int.TryParse(args[++i], out var parsedPort))
            {
                startupPort = parsedPort;
            }
            else
            {
                throw new ArgumentException($"Invalid value for --port: {args[i]}");
            }
            break;
        case "--bind-host":// バインドホストを指定
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException("Missing value for --bind-host.");
            }
            startupBindHost = args[++i];
            break;
        case "--non-interactive":// 非対話モードを有効化
            nonInteractive = true;
            break;
        case "--local-only":// ローカルホストのみで起動
            startupBindHost = "127.0.0.1";
            break;
        case "--public":// 公開用に起動
            startupBindHost = "0.0.0.0";
            break;
    }
}

// Webアプリケーションビルダーの作成
var builder = WebApplication.CreateBuilder(args);
var projectSettingsService = new ProjectSettingsService(builder.Configuration);

// CRECのプロジェクトファイルのパスを取得
var crecFilePath = startupProjectPath?.Trim();
if (string.IsNullOrWhiteSpace(crecFilePath))// コマンドライン引数で指定されていない場合は、標準入力から取得
{
    if (Console.IsInputRedirected)// 標準入力がリダイレクトされている場合は、ユーザーに入力を促すことができないため、例外をスロー
    {
        throw new InvalidOperationException("No .crec file specified. Please set the project path before startup.");
    }

    Console.WriteLine("No .crec file specified. Please enter the .crec file path:");
    crecFilePath = Console.ReadLine()?.Trim() ?? string.Empty;
}

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

// HttpClient for MCP server communication (used by ChatController)
builder.Services.AddHttpClient("MCP");

// Add CREC data service
builder.Services.AddSingleton<CrecDataService>();
builder.Services.AddSingleton<DataFileManagerService>();

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
int port;
if (startupPort is int configuredPort)// コマンドライン引数で指定されたポート番号がある場合はパターンマッチング後にそれを使用
{
    if (configuredPort < 1 || configuredPort > 65534)
    {
        throw new ArgumentOutOfRangeException(nameof(configuredPort), configuredPort, "Port must be between 1 and 65534 (HTTPS uses port+1).");
    }

    if (ArePortsAvailable(configuredPort))
    {
        port = configuredPort;
    }
    else
    {
        throw new InvalidOperationException($"Port {configuredPort} is already in use. Please specify a different port.");
    }
}
else if (Console.IsInputRedirected)// 標準入力がリダイレクトされている場合は、ユーザーに入力を促すことができないため、例外をスロー
{
    throw new InvalidOperationException("No port specified. Please set the startup port before launch.");
}
else// ユーザーにポート番号の入力を促す
{
    while (true)
    {
        Console.Write("Please enter the project port number (1-65534): ");
        var inputPort = Console.ReadLine();
        port = 5000;

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
            break;
        }
    }
}

// バインドホストの設定
string bindHost;
if (!string.IsNullOrWhiteSpace(startupBindHost))// コマンドライン引数で指定されたバインドホストがある場合はそれを使用
{
    bindHost = startupBindHost.Trim();
}
else if (Console.IsInputRedirected)// 標準入力がリダイレクトされている場合は、ユーザーに入力を促すことができないため、例外をスロー
{
    throw new InvalidOperationException("No bind host specified. Please set --local-only, --public, or --bind-host before launch.");
}
else// ユーザーにバインドホストの入力を促す
{
    while (true)
    {
        Console.WriteLine("Please choose the startup scope:");
        Console.WriteLine("  1: Local only (127.0.0.1)");
        Console.WriteLine("  2: Web server as public (0.0.0.0)");
        Console.Write("Enter 1 or 2: ");
        var input = Console.ReadLine()?.Trim();

        if (input == "1")
        {
            bindHost = "127.0.0.1";
            break;
        }

        if (input == "2")
        {
            bindHost = "0.0.0.0";
            break;
        }

        Console.WriteLine("Invalid input. Please enter 1 or 2.");
    }
}

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
var isPublicBind = string.Equals(bindHost, "0.0.0.0", StringComparison.Ordinal);
if (isPublicBind)// 公開の場合
{
    logger.LogInformation("  - http://localhost:{Port} (HTTP, local access)", port);
    logger.LogInformation("  - https://localhost:{Port} (HTTPS, local access)", port + 1);
    logger.LogInformation("  - http://[your-ip]:{Port} (HTTP, network access)", port);
    logger.LogInformation("  - https://[your-ip]:{Port}", port + 1);
}
else// ローカルのみの場合
{
    logger.LogInformation("  - http://{BindHost}:{Port} (HTTP)", bindHost, port);
    logger.LogInformation("  - https://{BindHost}:{Port} (HTTPS)", bindHost, port + 1);
}
var documentationHost = isPublicBind ? "localhost" : bindHost;
logger.LogInformation("API documentation available at: https://{DocumentationHost}:{Port}/swagger", documentationHost, port + 1);

// シャットダウンハンドラの設定
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var isShuttingDown = 0; // シャットダウン処理の重複実行を防ぐフラグ (0=実行中でない, 1=実行中)

if (nonInteractive && Console.IsInputRedirected)
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
