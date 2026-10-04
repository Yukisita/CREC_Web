using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CREC_Web.Controllers;
using CREC_Web.Middleware;
using CREC_Web.Services;
using CREC_Web.Services.AiTools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

internal static class AiToolsIntegrationTest
{
    public static async Task<int> RunAsync()
    {
        var directory = Directory.CreateTempSubdirectory("crec-ai-tools-");
        try
        {
            foreach (var name in new[] { "Project A", "Project B" })
            {
                var dataPath = Path.Combine(directory.FullName, name);
                var systemPath = Directory.CreateDirectory(Path.Combine(dataPath, "camera-id", "SystemData"));
                var labels = new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" }
                    .ToDictionary(key => key, _ => new { displayName = "" });
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name + ".crec"),
                    JsonSerializer.Serialize(new { projectSettings = new { projectName = name, projectLocation = dataPath }, labelSettings = labels }));
                await File.WriteAllTextAsync(Path.Combine(systemPath.FullName, "index.json"),
                    JsonSerializer.Serialize(new
                    {
                        systemData = new { id = "camera-id", systemCreateDate = "2026-01-01T00:00:00Z" },
                        values = new { name = "カメラ " + name, managementCode = "CAM-01", category = "写真", firstTag = "精密機器", location = "棚A" }
                    }));
                await File.WriteAllTextAsync(Path.Combine(systemPath.FullName, "inventory.json"), JsonSerializer.Serialize(new
                {
                    metaData = new { collectionId = "camera-id" },
                    setting = new { safetyStock = 5, reorderPoint = 10, maximumLevel = 9007199254740997L },
                    operations = new[]
                    {
                        new { dateTime = "2026-01-01T00:00:00Z", operationType = 0, quantity = 9007199254740997L, note = "初期在庫" },
                        new { dateTime = "2026-01-02T00:00:00Z", operationType = 1, quantity = -2L, note = "持ち出し" }
                    }
                }));
                var collectionRoot = Path.GetDirectoryName(systemPath.FullName)!;
                Directory.CreateDirectory(Path.Combine(dataPath, "empty-id"));
                Directory.CreateDirectory(Path.Combine(collectionRoot, "data", "説明"));
                await File.WriteAllTextAsync(Path.Combine(collectionRoot, "data", "説明", "readme.txt"), "日本語📷\n" + name, new UTF8Encoding(false));
                await File.WriteAllBytesAsync(Path.Combine(collectionRoot, "data", "raw.bin"), [0, 255, 12, 128, 10]);
                foreach (var area in new[] { "pictures", "videos", "3DData" })
                {
                    Directory.CreateDirectory(Path.Combine(collectionRoot, area));
                    await File.WriteAllBytesAsync(Path.Combine(collectionRoot, area, "sample.bin"), [1, 2, 3, 4]);
                }
                await File.WriteAllBytesAsync(Path.Combine(systemPath.FullName, "Thumbnail.jpg"), [255, 216, 255, 217]);
            }
            await VerifyAsync(directory.FullName);
            Console.WriteLine("PASS All nine read tools, MCP/WebMCP parity, advanced search, inventory, file paging/content/isolation, read-only behavior and access boundaries.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL AI tools: {ex}");
            return 1;
        }
        finally { Directory.Delete(directory.FullName, recursive: true); }
    }

    private static async Task VerifyAsync(string projectsRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration["CrecFilePath"] = null;
        builder.Configuration["ProjectDataPath"] = null;
        builder.Services.AddControllers().AddApplicationPart(typeof(AiToolsController).Assembly);
        builder.Services.AddSingleton<ProjectSettingsService>();
        builder.Services.AddSingleton<CrecDataService>();
        builder.Services.AddSingleton<DataFileManagerService>();
        builder.Services.AddSingleton(new ProjectCatalogService(projectsRoot));
        builder.Services.AddSingleton<ProjectRuntime>();
        builder.Services.AddCrecMcp();
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
        await using var app = builder.Build();
        app.UseRouting();
        app.MapCrecMcp();
        app.UseMiddleware<ProjectRequestMiddleware>();
        app.UseCors();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var token = timeout.Token;
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
            {
                Endpoint = new Uri(http.BaseAddress, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp
            }), cancellationToken: token);
            var tools = await mcp.ListToolsAsync(cancellationToken: token);
            Equal("get_collection,get_current_project,get_inventory,get_project_settings,get_search_options,list_collection_files,list_projects,read_collection_file,search_collections",
                string.Join(",", tools.Select(tool => tool.Name).Order()));
            foreach (var tool in tools)
            {
                Check(tool.ProtocolTool.Annotations?.ReadOnlyHint == true, "Tool must be read-only.");
                Check(tool.ProtocolTool.OutputSchema is not null, "Tool must advertise structured output.");
            }
            var runtime = app.Services.GetRequiredService<ProjectRuntime>();
            var initial = await CallAsync(mcp, "get_current_project", new(), token);
            Check(!initial.GetProperty("hasProject").GetBoolean(), "Discovery must work without a selected project.");
            await RejectAsync(mcp, "search_collections", new() { ["projectRevision"] = runtime.Current.Revision }, "projects-not-selected", token);
            Equal(2, (await CallAsync(mcp, "list_projects", new() { ["projectRevision"] = runtime.Current.Revision }, token))
                .GetProperty("projects").GetProperty("totalCount").GetInt32());
            using (var definitions = await http.GetAsync($"/api/ai-tools?projectRevision={runtime.Current.Revision}", token))
                Equal(HttpStatusCode.OK, definitions.StatusCode);

            var catalog = app.Services.GetRequiredService<ProjectCatalogService>();
            var projects = catalog.List(null).Projects;
            Equal("projects-switched", (await runtime.SwitchAsync(projects.Single(p => p.Name == "Project A").Id, runtime.Current.Revision, token)).Code);
            var revision = runtime.Current.Revision;
            http.DefaultRequestHeaders.Add("X-CREC-Project", revision);
            http.DefaultRequestHeaders.Add("X-CREC-Request", "1");
            var definitionsJson = await http.GetFromJsonAsync<JsonElement>("/api/ai-tools", token);
            Equal(9, definitionsJson.GetArrayLength());
            foreach (var tool in tools)
                SameJson(tool.ProtocolTool.InputSchema, definitionsJson.EnumerateArray()
                    .Single(d => d.GetProperty("name").GetString() == tool.Name).GetProperty("inputSchema"));
            var project = await CallAsync(mcp, "get_current_project", new(), token);
            Equal("Project A", project.GetProperty("name").GetString());
            Equal(revision, project.GetProperty("revision").GetString());
            var arguments = new Dictionary<string, object?> { ["projectRevision"] = revision, ["query"] = "カメラ", ["pageSize"] = 1 };
            var search = await CallAsync(mcp, "search_collections", arguments, token);
            Equal(1, search.GetProperty("totalCount").GetInt32());
            Equal("カメラ Project A", search.GetProperty("collections")[0].GetProperty("name").GetString());
            Check(!search.ToString().Contains(projectsRoot, StringComparison.OrdinalIgnoreCase), "Tool results must not contain filesystem paths.");
            var webSearch = await WebCallAsync(http, "search_collections", arguments, token);
            SameJson(search, webSearch);
            var detailArgs = new Dictionary<string, object?> { ["projectRevision"] = revision, ["collectionId"] = "camera-id" };
            var detail = await CallAsync(mcp, "get_collection", detailArgs, token);
            var webDetail = await WebCallAsync(http, "get_collection", detailArgs, token);
            SameJson(detail, webDetail);
            Equal("CAM-01", detail.GetProperty("managementCode").GetString());
            arguments["query"] = "精密機器";
            Equal(1, (await CallAsync(mcp, "search_collections", arguments, token)).GetProperty("totalCount").GetInt32());
            arguments["page"] = 2;
            Equal(0, (await CallAsync(mcp, "search_collections", arguments, token)).GetProperty("collections").GetArrayLength());
            arguments["pageSize"] = 101;
            await RejectAsync(mcp, "search_collections", arguments, "invalid-page", token);
            using (var invalid = await http.PostAsJsonAsync("/api/ai-tools/search_collections", arguments, token))
                Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            detailArgs["collectionId"] = "../outside";
            await RejectAsync(mcp, "get_collection", detailArgs, "collection-not-found", token);
            await RejectAsync(mcp, "search_collections", new() { ["projectRevision"] = "" }, "projects-stale", token);
            await VerifyReadsAsync(mcp, http, revision, projectsRoot, token);

            using (var spoofed = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
            {
                spoofed.Headers.Host = "evil.example";
                using var response = await http.SendAsync(spoofed, token);
                Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { }) })
            {
                crossOrigin.Headers.Add("Origin", "https://evil.example");
                using var response = await http.SendAsync(crossOrigin, token);
                Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Check(!response.Headers.Contains("Access-Control-Allow-Origin"), "MCP must not inherit permissive CORS.");
            }
            using (var crossOrigin = new HttpRequestMessage(HttpMethod.Post, "/api/ai-tools/get_current_project") { Content = JsonContent.Create(new { }) })
            {
                crossOrigin.Headers.Add("Origin", "https://evil.example");
                using var response = await http.SendAsync(crossOrigin, token);
                Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
            using (var removed = await http.GetAsync($"/api/collection-queries?projectRevision={revision}", token))
                Equal(HttpStatusCode.NotFound, removed.StatusCode);

            Equal("projects-switched", (await runtime.SwitchAsync(projects.Single(p => p.Name == "Project B").Id, revision, token)).Code);
            arguments["pageSize"] = 20;
            await RejectAsync(mcp, "search_collections", arguments, "projects-stale", token);
            using (var stale = await http.PostAsJsonAsync("/api/ai-tools/get_collection", new { collectionId = "camera-id", projectRevision = runtime.Current.Revision }, token))
                Equal(HttpStatusCode.Conflict, stale.StatusCode);
            detailArgs["collectionId"] = "camera-id";
            await RejectAsync(mcp, "get_collection", detailArgs, "projects-stale", token);
            detailArgs["projectRevision"] = runtime.Current.Revision;
            Equal("カメラ Project B", (await CallAsync(mcp, "get_collection", detailArgs, token)).GetProperty("name").GetString());
            foreach (var tool in tools.Where(t => t.Name != "get_current_project"))
            {
                var staleArgs = new Dictionary<string, object?> { ["projectRevision"] = revision };
                var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
                if (properties.TryGetProperty("collectionId", out _)) staleArgs["collectionId"] = "camera-id";
                if (properties.TryGetProperty("path", out _)) staleArgs["path"] = "raw.bin";
                await RejectAsync(mcp, tool.Name, staleArgs, "projects-stale", token);
            }
        }
        finally { await app.StopAsync(); }
    }

    private static async Task VerifyReadsAsync(McpClient mcp, HttpClient http, string revision, string root, CancellationToken token)
    {
        var before = Snapshot(root);
        Dictionary<string, object?> Args() => new() { ["projectRevision"] = revision };
        async Task<JsonElement> Read(string name, Dictionary<string, object?> args)
        {
            var result = await CallAsync(mcp, name, args, token);
            SameJson(result, await WebCallAsync(http, name, args, token));
            Check(!result.ToString().Contains(root, StringComparison.OrdinalIgnoreCase), "Absolute paths must not escape.");
            return result;
        }
        var settings = await Read("get_project_settings", Args());
        Equal("Name", settings.GetProperty("labels").GetProperty("name").GetString());
        var projects = Args(); projects["pageSize"] = 1;
        Equal(2, (await Read("list_projects", projects)).GetProperty("projects").GetProperty("totalCount").GetInt32());
        projects["page"] = 2;
        Equal(1, (await Read("list_projects", projects)).GetProperty("projects").GetProperty("items").GetArrayLength());
        var options = await Read("get_search_options", Args());
        Equal("写真", options.GetProperty("categories").GetProperty("items")[0].GetString());
        Equal("精密機器", options.GetProperty("tags").GetProperty("items")[0].GetString());
        foreach (var (field, method, query) in new[] { ("Name", "Prefix", "カメラ"), ("Location", "Suffix", "A"),
            ("ManagementCode", "Exact", "cam-01"), ("Tag1", "Partial", "精密") })
        {
            var args = Args(); args["field"] = field; args["method"] = method; args["query"] = query;
            Equal(1, (await Read("search_collections", args)).GetProperty("totalCount").GetInt32());
        }
        var filtered = Args(); filtered["inventoryStatus"] = "Appropriate";
        Equal(1, (await Read("search_collections", filtered)).GetProperty("totalCount").GetInt32());
        filtered["inventoryStatus"] = "StockOut";
        Equal(0, (await Read("search_collections", filtered)).GetProperty("totalCount").GetInt32());
        var inventoryArgs = Args(); inventoryArgs["collectionId"] = "camera-id"; inventoryArgs["pageSize"] = 1;
        var inventory = await Read("get_inventory", inventoryArgs);
        Equal("9007199254740995", inventory.GetProperty("currentInventory").GetString());
        Equal("9007199254740997", inventory.GetProperty("settings").GetProperty("maximumLevel").GetString());
        Equal("初期在庫", inventory.GetProperty("history").GetProperty("items")[0].GetProperty("note").GetString());
        inventoryArgs["page"] = 2;
        Equal("-2", (await Read("get_inventory", inventoryArgs)).GetProperty("history").GetProperty("items")[0].GetProperty("quantity").GetString());
        foreach (var area in new[] { "Data", "Pictures", "Videos", "ThreeD", "Thumbnail" })
        {
            var args = Args(); args["collectionId"] = "camera-id"; args["area"] = area;
            var listing = await Read("list_collection_files", args);
            Check(listing.GetProperty("entries").GetProperty("totalCount").GetInt32() > 0, "File area must be populated.");
            args["path"] = area == "Data" ? "raw.bin" : area == "Thumbnail" ? "Thumbnail.jpg" : "sample.bin";
            var content = await Read("read_collection_file", args);
            Equal("base64", content.GetProperty("encoding").GetString());
            Check(Convert.FromBase64String(content.GetProperty("content").GetString()!).Length > 0, "Binary content missing.");
        }
        var textArgs = Args(); textArgs["collectionId"] = "camera-id"; textArgs["path"] = "説明/readme.txt"; textArgs["maxBytes"] = 4;
        var text = new StringBuilder();
        JsonElement chunk;
        do
        {
            chunk = await Read("read_collection_file", textArgs);
            text.Append(chunk.GetProperty("content").GetString());
            textArgs["version"] = chunk.GetProperty("version").GetString();
            if (chunk.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            textArgs["offset"] = chunk.GetProperty("nextOffset").GetInt64();
        } while (true);
        Equal("日本語📷\nProject A", text.ToString());
        var empty = Args(); empty["collectionId"] = "empty-id";
        Equal(0, (await Read("list_collection_files", empty)).GetProperty("entries").GetProperty("totalCount").GetInt32());
        var invalid = Args(); invalid["collectionId"] = "camera-id";
        foreach (var path in new[] { "../SystemData/index.json", "/outside", "C:/outside", "説明/../../outside", "raw.bin:stream" })
        {
            invalid["path"] = path;
            await RejectAsync(mcp, "read_collection_file", invalid, "invalid-file-path", token);
        }
        invalid["area"] = "Thumbnail"; invalid["path"] = "index.json";
        await RejectAsync(mcp, "read_collection_file", invalid, "invalid-file-path", token);
        invalid["area"] = "Data"; invalid["path"] = "missing.txt";
        await RejectAsync(mcp, "read_collection_file", invalid, "file-not-found", token);
        invalid["path"] = "raw.bin"; invalid["encoding"] = "Utf8";
        await RejectAsync(mcp, "read_collection_file", invalid, "file-not-utf8-use-base64", token);
        invalid["encoding"] = "Base64"; invalid["offset"] = 1;
        await RejectAsync(mcp, "read_collection_file", invalid, "file-version-required", token);
        invalid["version"] = "old";
        await RejectAsync(mcp, "read_collection_file", invalid, "file-changed", token);
        SameJson(await CallAsync(mcp, "get_current_project", new(), token), await WebCallAsync(http, "get_current_project", new(), token));
        var overridden = new Dictionary<string, object?> { ["projectRevision"] = "other-project", ["collectionId"] = "camera-id" };
        Equal("カメラ Project A", (await WebCallAsync(http, "get_collection", overridden, token)).GetProperty("name").GetString());
        using (var unknown = await http.PostAsJsonAsync("/api/ai-tools/delete_collection", new { collectionId = "camera-id" }, token))
            Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using (var unknownArg = await http.PostAsJsonAsync("/api/ai-tools/get_current_project", new { filePath = root }, token))
            Equal(HttpStatusCode.BadRequest, unknownArg.StatusCode);
        using (var badEnum = await http.PostAsJsonAsync("/api/ai-tools/search_collections", new { field = "Unknown" }, token))
            Equal(HttpStatusCode.BadRequest, badEnum.StatusCode);
        Equal(before, Snapshot(root));
        // 実際に更新されたファイルを、古い分割位置のまま読み進めない。
        await File.AppendAllTextAsync(Path.Combine(root, "Project A", "camera-id", "data", "説明", "readme.txt"), "変更", token);
        await RejectAsync(mcp, "read_collection_file", textArgs, "file-changed", token);

        var link = Path.Combine(root, "Project A", "camera-id", "data", "outside");
        var target = Directory.CreateDirectory(Path.Combine(root, "outside-files")).FullName;
        await File.WriteAllTextAsync(Path.Combine(target, "private.txt"), "must not be returned", token);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync(token);
            Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, target);
        try
        {
            var linked = Args(); linked["collectionId"] = "camera-id"; linked["path"] = "outside/private.txt";
            await RejectAsync(mcp, "read_collection_file", linked, "file-link-denied", token);
            linked["path"] = "outside";
            await RejectAsync(mcp, "list_collection_files", linked, "file-link-denied", token);
        }
        finally { Directory.Delete(link); }
    }

    private static string Snapshot(string root) => string.Join("\n", Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order()
        .Concat(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order().Select(path => path + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))));

    private static async Task<JsonElement> WebCallAsync(HttpClient http, string name, Dictionary<string, object?> args, CancellationToken token)
    {
        using var response = await http.PostAsJsonAsync("/api/ai-tools/" + name, args, token);
        var text = await response.Content.ReadAsStringAsync(token);
        Check(response.IsSuccessStatusCode, $"Web tool {name} failed: {response.StatusCode} {text}");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private static void SameJson(JsonElement expected, JsonElement actual) =>
        Check(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), JsonNode.Parse(actual.GetRawText())), $"Different results: {expected} / {actual}");

    private static async Task<JsonElement> CallAsync(McpClient client, string name,
        Dictionary<string, object?> arguments, CancellationToken token)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: token);
        Check(result.IsError != true, $"Tool {name} failed: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text))}");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static async Task RejectAsync(McpClient client, string name,
        Dictionary<string, object?> arguments, string code, CancellationToken token)
    {
        var result = await client.CallToolAsync(name, arguments, cancellationToken: token);
        Check(result.IsError == true, $"Expected {name} to fail.");
        Check(result.Content.OfType<TextContentBlock>().Any(content => content.Text.Contains(code)), $"Expected {code}.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) => Check(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}, got {actual}.");
}
