using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
                        systemData = new { id = "camera-id" },
                        values = new { name = "カメラ " + name, managementCode = "CAM-01", category = "写真", firstTag = "精密機器", location = "棚A" }
                    }));
            }
            await VerifyAsync(directory.FullName);
            Console.WriteLine("PASS MCP discovery, structured reads, shared Web API results, project isolation and local access boundaries.");
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
        builder.Services.AddControllers().AddApplicationPart(typeof(CollectionQueriesController).Assembly);
        builder.Services.AddSingleton<ProjectSettingsService>();
        builder.Services.AddSingleton<CrecDataService>();
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
            Equal("get_collection,get_current_project,search_collections", string.Join(",", tools.Select(tool => tool.Name).Order()));
            foreach (var tool in tools)
            {
                Check(tool.ProtocolTool.Annotations?.ReadOnlyHint == true, "Tool must be read-only.");
                Check(tool.ProtocolTool.OutputSchema is not null, "Tool must advertise structured output.");
            }
            var runtime = app.Services.GetRequiredService<ProjectRuntime>();
            var initial = await CallAsync(mcp, "get_current_project", new(), token);
            Check(!initial.GetProperty("hasProject").GetBoolean(), "Discovery must work without a selected project.");
            await RejectAsync(mcp, "search_collections", new() { ["projectRevision"] = runtime.Current.Revision }, "projects-not-selected", token);

            var catalog = app.Services.GetRequiredService<ProjectCatalogService>();
            var projects = catalog.List(null).Projects;
            Equal("projects-switched", (await runtime.SwitchAsync(projects.Single(p => p.Name == "Project A").Id, runtime.Current.Revision, token)).Code);
            var revision = runtime.Current.Revision;
            var project = await CallAsync(mcp, "get_current_project", new(), token);
            Equal("Project A", project.GetProperty("name").GetString());
            Equal(revision, project.GetProperty("revision").GetString());
            var arguments = new Dictionary<string, object?> { ["projectRevision"] = revision, ["query"] = "カメラ", ["pageSize"] = 1 };
            var search = await CallAsync(mcp, "search_collections", arguments, token);
            Equal(1, search.GetProperty("totalCount").GetInt32());
            Equal("カメラ Project A", search.GetProperty("collections")[0].GetProperty("name").GetString());
            Check(!search.ToString().Contains(projectsRoot, StringComparison.OrdinalIgnoreCase), "Tool results must not contain filesystem paths.");
            var webSearch = await http.GetFromJsonAsync<JsonElement>($"/api/collection-queries?projectRevision={revision}&query=カメラ&pageSize=1", token);
            Equal(JsonSerializer.Serialize(search.Deserialize<CollectionQueryResult>(JsonSerializerOptions.Web)),
                JsonSerializer.Serialize(webSearch.Deserialize<CollectionQueryResult>(JsonSerializerOptions.Web)));
            var detailArgs = new Dictionary<string, object?> { ["projectRevision"] = revision, ["collectionId"] = "camera-id" };
            var detail = await CallAsync(mcp, "get_collection", detailArgs, token);
            var webDetail = await http.GetFromJsonAsync<JsonElement>($"/api/collection-queries/camera-id?projectRevision={revision}", token);
            Equal(JsonSerializer.Serialize(detail.Deserialize<CollectionView>(JsonSerializerOptions.Web)),
                JsonSerializer.Serialize(webDetail.Deserialize<CollectionView>(JsonSerializerOptions.Web)));
            Equal("CAM-01", detail.GetProperty("managementCode").GetString());
            arguments["query"] = "精密機器";
            Equal(1, (await CallAsync(mcp, "search_collections", arguments, token)).GetProperty("totalCount").GetInt32());
            arguments["page"] = 2;
            Equal(0, (await CallAsync(mcp, "search_collections", arguments, token)).GetProperty("collections").GetArrayLength());
            arguments["pageSize"] = 51;
            await RejectAsync(mcp, "search_collections", arguments, "invalid-query", token);
            using (var invalid = await http.GetAsync($"/api/collection-queries?projectRevision={revision}&pageSize=51", token))
                Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            detailArgs["collectionId"] = "../outside";
            await RejectAsync(mcp, "get_collection", detailArgs, "collection-not-found", token);
            await RejectAsync(mcp, "search_collections", new() { ["projectRevision"] = "" }, "projects-stale", token);

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

            Equal("projects-switched", (await runtime.SwitchAsync(projects.Single(p => p.Name == "Project B").Id, revision, token)).Code);
            arguments["pageSize"] = 20;
            await RejectAsync(mcp, "search_collections", arguments, "projects-stale", token);
            using (var stale = await http.GetAsync($"/api/collection-queries?projectRevision={revision}", token))
                Equal(HttpStatusCode.Conflict, stale.StatusCode);
            detailArgs["collectionId"] = "camera-id";
            await RejectAsync(mcp, "get_collection", detailArgs, "projects-stale", token);
            detailArgs["projectRevision"] = runtime.Current.Revision;
            Equal("カメラ Project B", (await CallAsync(mcp, "get_collection", detailArgs, token)).GetProperty("name").GetString());
        }
        finally { await app.StopAsync(); }
    }

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
