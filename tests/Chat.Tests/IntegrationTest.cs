using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CREC_Web.Controllers;
using CREC_Web.Middleware;
using CREC_Web.Models;
using CREC_Web.Services;
using CREC_Web.Services.Chat;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class IntegrationTest
{
    // The Python harness starts the real MCP server with a deterministic LLM endpoint.
    public static async Task<int> RunAsync(string mcpUrl)
    {
        var directory = Directory.CreateTempSubdirectory("crec-chat-projects-");
        try
        {
            foreach (var name in new[] { "Project A", "Project B" })
            {
                var data = Directory.CreateDirectory(Path.Combine(directory.FullName, name)).FullName;
                var labels = new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" }
                    .ToDictionary(key => key, _ => new { displayName = "" });
                await File.WriteAllTextAsync(Path.Combine(directory.FullName, name + ".crec"),
                    JsonSerializer.Serialize(new { projectSettings = new { projectName = name, projectLocation = data }, labelSettings = labels }));
            }
            return await RunHostAsync(mcpUrl, directory.FullName);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    private static async Task<int> RunHostAsync(string mcpUrl, string projectsRoot)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["CrecFilePath"] = null;
        builder.Configuration["ProjectDataPath"] = null;
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
        builder.Services.Configure<McpClientOptions>(options => { options.Url = mcpUrl; options.TimeoutSeconds = 10; });
        builder.Services.AddHttpClient(McpChatClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<IMcpChatClient, McpChatClient>();
        builder.Services.AddSingleton<ProjectSettingsService>();
        builder.Services.AddSingleton<CrecDataService>();
        builder.Services.AddSingleton(new ProjectCatalogService(projectsRoot));
        builder.Services.AddSingleton<ProjectRuntime>();
        await using var app = builder.Build();
        app.UseRouting();
        app.UseMiddleware<ProjectRequestMiddleware>();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var runtime = app.Services.GetRequiredService<ProjectRuntime>();
            var initialRevision = runtime.Current.Revision;
            client.DefaultRequestHeaders.Add("X-CREC-Request", "1");
            client.DefaultRequestHeaders.Add("X-CREC-Project", initialRevision);
            await AssertRejectedAsync(client, "projects-not-selected");
            var listing = await client.GetFromJsonAsync<ProjectListing>("/api/projects")
                ?? throw new Exception("Project listing was empty.");
            if (listing.ErrorCode != null || listing.Projects.Count != 2)
                throw new Exception("Test projects were not listed.");
            await SwitchAsync(client, listing.Projects.Single(project => project.Name == "Project A").Id, initialRevision);
            var revisionA = runtime.Current.Revision;
            client.DefaultRequestHeaders.Remove("X-CREC-Project");
            await AssertRejectedAsync(client, "projects-stale");
            client.DefaultRequestHeaders.Add("X-CREC-Project", revisionA);
            foreach (var (message, actionCount, warning) in new[]
            {
                ("explain", 0, (string?)null), ("plan", 2, null),
                ("invalid-plan", 0, "invalid_actions"), ("delete", 0, "deletion_blocked")
            })
            {
                using var response = await client.PostAsJsonAsync("/api/Chat", new ChatRequest { Message = message });
                response.EnsureSuccessStatusCode();
                var reply = await response.Content.ReadFromJsonAsync<ChatResponse>();
                if (reply == null || reply.Actions.Length != actionCount || reply.Warning != warning || reply.Text.Contains("<action>"))
                    throw new Exception($"Invalid integration response for {message}.");
                if (message == "plan" && reply.Actions[0].GetProperty("value").GetString() != "カメラ")
                    throw new Exception("Unicode action value was lost in transit.");
                Console.WriteLine($"PASS Web API -> MCP -> LLM -> structured response: {message}");
            }
            using var invalid = await client.PostAsJsonAsync("/api/Chat", new ChatRequest());
            if (invalid.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Empty message was accepted.");
            await SwitchAsync(client, listing.Projects.Single(project => project.Name == "Project B").Id, revisionA);
            await AssertRejectedAsync(client, "projects-stale");
            client.DefaultRequestHeaders.Remove("X-CREC-Project");
            client.DefaultRequestHeaders.Add("X-CREC-Project", runtime.Current.Revision);
            using var current = await client.PostAsJsonAsync("/api/Chat", new ChatRequest { Message = "explain" });
            current.EnsureSuccessStatusCode();
            Console.WriteLine("PASS project selection, switch and stale chat request rejection");
            return 0;
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task AssertRejectedAsync(HttpClient client, string code)
    {
        using var response = await client.PostAsJsonAsync("/api/Chat", new ChatRequest { Message = "explain" });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (response.StatusCode != HttpStatusCode.Conflict || problem.GetProperty("code").GetString() != code)
            throw new Exception($"Expected chat rejection: {code}.");
    }

    private static async Task SwitchAsync(HttpClient client, string id, string revision)
    {
        using var response = await client.PostAsJsonAsync("/api/projects/switch", new { id, revision });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (result.GetProperty("code").GetString() != "projects-switched") throw new Exception("Project did not switch.");
    }
}
