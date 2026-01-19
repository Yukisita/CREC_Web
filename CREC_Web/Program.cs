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
projectSettings = ParseCrecFile(crecFilePath);

// プロジェクト設定を適用
if (projectSettings != null)
{
    builder.Configuration["ProjectDataPath"] = projectSettings.ProjectDataPath;
    builder.Configuration["ProjectName"] = projectSettings.ProjectName;
    builder.Configuration["CollectionNameLabel"] = projectSettings.CollectionNameLabel;
    builder.Configuration["UUIDLabel"] = projectSettings.UUIDLabel;
    builder.Configuration["ManagementCodeLabel"] = projectSettings.ManagementCodeLabel;
    builder.Configuration["CategoryLabel"] = projectSettings.CategoryLabel;
    builder.Configuration["FirstTagLabel"] = projectSettings.FirstTagLabel;
    builder.Configuration["SecondTagLabel"] = projectSettings.SecondTagLabel;
    builder.Configuration["ThirdTagLabel"] = projectSettings.ThirdTagLabel;
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

// Ctrl+C (SIGINT) ハンドラの設定
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var isShuttingDown = false; // シャットダウン処理の重複実行を防ぐフラグ
Console.CancelKeyPress += (sender, eventArgs) =>
{
    if (!isShuttingDown)
    {
        isShuttingDown = true;
        Console.WriteLine("\nCtrl+C detected. Shutting down the server gracefully...");
        eventArgs.Cancel = true; // デフォルトの終了処理をキャンセルして、アプリケーションの適切なシャットダウンを実行
        lifetime.StopApplication(); // アプリケーションの適切なシャットダウンを要求
    }
};

// Helper method to parse .crec file and extract project settings
static ProjectSettings? ParseCrecFile(string crecFilePath)
{
    try
    {
        if (!File.Exists(crecFilePath))
        {
            Console.WriteLine($"Error: .crec file not found: {crecFilePath}");
            return null;
        }

        var settings = new ProjectSettings();
        var lines = File.ReadAllLines(crecFilePath, System.Text.Encoding.GetEncoding("UTF-8"));

        Console.WriteLine($"Parsing .crec file with {lines.Length} lines...");

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var cols = line.Split(',');
            if (cols.Length < 2) continue;

            // Extract the value (second column, index 1)
            var key = cols[0].Trim();
            var value = cols[1].Trim();

            switch (key)
            {
                case "projectname":
                    settings.ProjectName = value;
                    Console.WriteLine($"  - Found projectname: {value}");
                    break;
                case "projectlocation":
                    settings.ProjectDataPath = value;
                    Console.WriteLine($"  - Found projectlocation: {value}");
                    break;
                case "ShowObjectNameLabel":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.CollectionNameLabel = value;
                        Console.WriteLine($"  - Found ShowObjectNameLabel: {value}");
                    }
                    break;
                case "ShowIDLabel":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.UUIDLabel = value;
                        Console.WriteLine($"  - Found ShowIDLabel: {value}");
                    }
                    break;
                case "ShowMCLabel":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.ManagementCodeLabel = value;
                        Console.WriteLine($"  - Found ShowMCLabel: {value}");
                    }
                    break;
                case "ShowCategoryLabel":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.CategoryLabel = value;
                        Console.WriteLine($"  - Found ShowCategoryLabel: {value}");
                    }
                    break;
                case "Tag1Name":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.FirstTagLabel = value;
                        Console.WriteLine($"  - Found Tag1Name: {value}");
                    }
                    break;
                case "Tag2Name":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.SecondTagLabel = value;
                        Console.WriteLine($"  - Found Tag2Name: {value}");
                    }
                    break;
                case "Tag3Name":
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        settings.ThirdTagLabel = value;
                        Console.WriteLine($"  - Found Tag3Name: {value}");
                    }
                    break;
            }
        }

        Console.WriteLine($"Finished parsing .crec file");

        // Validate that we at least have a data path
        if (string.IsNullOrEmpty(settings.ProjectDataPath))
        {
            Console.WriteLine("Error: 'projectlocation' not found in .crec file");
            return null;
        }

        if (!Directory.Exists(settings.ProjectDataPath))
        {
            Console.WriteLine($"Warning: Project data folder does not exist: {settings.ProjectDataPath}");
            // Return anyway so user can see the configured path
        }

        return settings;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error parsing .crec file: {ex.Message}");
        return null;
    }
}

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

// Helper class to hold project settings
public class ProjectSettings
{
    public string ProjectName { get; set; } = "CREC Project";
    public string ProjectDataPath { get; set; } = "";
    public string CollectionNameLabel { get; set; } = "Name";
    public string UUIDLabel { get; set; } = "UUID";
    public string ManagementCodeLabel { get; set; } = "MC";
    public string CategoryLabel { get; set; } = "Category";
    public string FirstTagLabel { get; set; } = "Tag 1";
    public string SecondTagLabel { get; set; } = "Tag 2";
    public string ThirdTagLabel { get; set; } = "Tag 3";
}
