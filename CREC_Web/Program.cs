/*
CREC Web - Main Program
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Services;
using Microsoft.Extensions.FileProviders;

Console.WriteLine("Starting CREC Web Server...");

// Webアプリケーションビルダーの作成
var builder = WebApplication.CreateBuilder(args);
var projectSettingsService = new ProjectSettingsService(builder.Configuration);

// CRECのプロジェクトファイルのパスを取得
var crecFilePath = string.Empty;
ProjectSettings? projectSettings = null;
if (args.Length > 0 && args[0].EndsWith(".crec", StringComparison.OrdinalIgnoreCase))
{
    // コマンドライン引数からプロジェクトファイルのパスを取得
    crecFilePath = args[0];
}
else
{
    // CRECファイルがコマンドライン引数に指定されていない場合、手動でのパス入力を待機
    Console.WriteLine("No .crec file specified. Please enter the project data folder path:");
    var inputPath = Console.ReadLine()?.Trim();
    crecFilePath = inputPath ?? string.Empty;
}

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

// Add HttpClient for server-side Ollama calls
builder.Services.AddHttpClient();

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
bool isPortAvailable = false;
int port = 5000;
while (!isPortAvailable)
{
    // port番号をコマンドラインに入力
    Console.Write("Please enter the project port number (1-65534): ");
    var inputPort = Console.ReadLine();
    if (!string.IsNullOrWhiteSpace(inputPort))
    {
        inputPort = inputPort.Trim();
        if (int.TryParse(inputPort, out int parsedPort))
        {
            port = parsedPort;
        }
        else
        {
            Console.WriteLine($"Invalid port input. Use default port.");
            port = 5000; // デフォルトポートを指定
        }
    }
    else
    {
        Console.WriteLine($"No port input. Use default port.");
        port = 5000; // デフォルトポートを指定
    }

    // ポート番号の規則をコンソール表示（HTTPSは入力値、HTTPは入力値+1）
    Console.WriteLine($"Using ports: HTTP={port}, HTTPS={port + 1}");
    // ポートが利用可能か確認
    if (IsPortAvailable(port) && IsPortAvailable(port + 1))
    {
        isPortAvailable = true;
    }
}
builder.WebHost.UseUrls($"http://0.0.0.0:{port}", $"https://0.0.0.0:{port + 1}");

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
logger.LogInformation("Web interface will be available at:");
logger.LogInformation("  - http://localhost:{Port} (HTTP)", port);
logger.LogInformation("  - https://localhost:{Port} (HTTPS)", port + 1);
logger.LogInformation("  - https://[your-ip]:{Port}", port + 1);
logger.LogInformation("API documentation available at: https://localhost:{Port}/swagger", port + 1);
logger.LogInformation("Press Ctrl+Q to initiate server shutdown.");

// Ctrl+Q シャットダウンハンドラの設定
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var isShuttingDown = 0; // シャットダウン処理の重複実行を防ぐフラグ (0=実行中でない, 1=実行中)

// Ctrl+Qの入力を監視するバックグラウンドタスク
var monitorTask = Task.Run(() =>
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
                        var response = Console.ReadLine()?.Trim().ToUpper();

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

// ポートが利用可能か確認する関数
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
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
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

app.Run();
