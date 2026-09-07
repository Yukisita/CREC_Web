using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CREC_Web.Controllers;
using CREC_Web.Middleware;
using CREC_Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;

internal static class HttpTests
{
    public static async Task Run(string fixture, bool serve)
    {
        var root = Path.Combine(fixture, "HttpProjects");
        Directory.CreateDirectory(root);
        const string collectionId = "a5a48e6d-a11b-4c01-b970-b371883018d1";
        foreach (var name in new[] { "Alpha", "Beta", "Empty" })
        {
            var dataPath = Path.Combine(root, name);
            Directory.CreateDirectory(dataPath);
            if (name != "Empty")
            {
                var collection = Path.Combine(dataPath, collectionId);
                Directory.CreateDirectory(Path.Combine(collection, "SystemData"));
                Directory.CreateDirectory(Path.Combine(collection, "data"));
                File.WriteAllText(Path.Combine(collection, "data", "example.txt"), name);
                File.WriteAllText(Path.Combine(collection, "SystemData", "index.json"), JsonSerializer.Serialize(new {
                    systemData = new { id = collectionId, systemCreateDate = "2026-09-08T00:00:00Z" },
                    values = new { name = name + " collection", registrationDate = "2026-09-08T00:00:00Z" }
                }));
            }
            var labels = new JsonObject();
            foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
                labels[key] = new JsonObject { ["displayName"] = name + " " + key };
            File.WriteAllText(Path.Combine(root, name + ".crec"), new JsonObject {
                ["projectSettings"] = new JsonObject { ["projectName"] = name, ["projectLocation"] = name },
                ["labelSettings"] = labels
            }.ToJsonString());
        }
        File.WriteAllText(Path.Combine(root, "Invalid.crec"), "{}");
        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions {
            ApplicationName = typeof(ProjectsController).Assembly.GetName().Name,
            ContentRootPath = Path.Combine(repository, "CREC_Web")
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddControllersWithViews();
        var settings = new ProjectSettingsService(builder.Configuration);
        var alpha = Path.Combine(root, "Alpha.crec");
        settings.ApplyProjectSettings(ProjectSettingsService.ReadValidatedSettings(alpha), alpha);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<CrecDataService>();
        builder.Services.AddSingleton<DataFileManagerService>();
        builder.Services.AddSingleton(new ProjectCatalogService(root));
        builder.Services.AddSingleton<ProjectRuntime>();
        const string token = "test-only-administrator-token-00000000";
        builder.Services.AddSingleton(new ProjectAdminService(token));
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));
        await using var app = builder.Build();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseMiddleware<ProjectRequestMiddleware>();
        app.UseCors();
        app.MapControllers();
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapPost("/test/save", async context => {
            saveStarted.SetResult();
            await finishSave.Task;
            await File.WriteAllTextAsync(Path.Combine(builder.Configuration["ProjectDataPath"]!, "saved.txt"), "saved to original project");
            await context.Response.WriteAsync("saved");
        });
        await app.StartAsync();
        var address = app.Urls.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        void Check(bool condition, string description)
        {
            if (!condition) throw new Exception("HTTP FAIL: " + description);
            Console.WriteLine("PASS: HTTP " + description);
        }
        async Task<JsonObject> GetObject(string path) => (await client.GetFromJsonAsync<JsonObject>(path))!;
        var initial = await GetObject("/api/projects/status");
        var revision = initial["revision"]!.GetValue<string>();
        var html = await client.GetStringAsync("/");
        Check(html.Contains(revision) && html.Contains("openProjectBtn"), "layout binds revision and exposes project picker");
        Check((await client.GetAsync("/api/projects")).StatusCode == HttpStatusCode.Unauthorized, "anonymous listing denied");
        client.DefaultRequestHeaders.Add("X-CREC-Request", "1");
        var login = await client.PostAsJsonAsync("/api/projects/login", new { token });
        Check(login.IsSuccessStatusCode, "administrator session login");
        var list = await GetObject("/api/projects");
        string Id(string name) => list["projects"]!.AsArray().Single(p => p!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
        client.DefaultRequestHeaders.Add("X-CREC-Project", revision);
        var collections = await client.GetStringAsync("/api/collections");
        Check(collections.Contains("Alpha collection"), "Alpha collection loaded");
        var download = $"/api/collections/{collectionId}/data/files?path=example.txt";
        Check(await client.GetStringAsync(download) == "Alpha", "Alpha attachment loaded");
        var save = client.PostAsync("/test/save", null);
        await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var switching = client.PostAsJsonAsync("/api/projects/switch", new { id = Id("Beta"), revision });
        await Task.Delay(150);
        Check(!switching.IsCompleted, "switch waits for in-flight save");
        Check((await client.PostAsync("/api/collections", null)).StatusCode == HttpStatusCode.ServiceUnavailable, "new updates rejected while draining");
        finishSave.SetResult();
        Check((await save).IsSuccessStatusCode, "in-flight save completed");
        var switched = await switching;
        Check(switched.IsSuccessStatusCode, "switch API completed");
        Check(File.Exists(Path.Combine(root, "Alpha", "saved.txt")) && !File.Exists(Path.Combine(root, "Beta", "saved.txt")), "in-flight save stayed in Alpha");
        Check((await client.GetAsync(download)).StatusCode == HttpStatusCode.Conflict, "old attachment URL/header refused");
        Check((await client.PostAsync("/api/collections", null)).StatusCode == HttpStatusCode.Conflict, "old collection update refused");
        var betaRevision = (await GetObject("/api/projects/status"))["revision"]!.GetValue<string>();
        client.DefaultRequestHeaders.Remove("X-CREC-Project");
        client.DefaultRequestHeaders.Add("X-CREC-Project", betaRevision);
        Check((await client.GetStringAsync("/api/collections")).Contains("Beta collection"), "same collection ID resolves to Beta");
        Check(await client.GetStringAsync(download) == "Beta", "same attachment name resolves to Beta");
        Check((await GetObject("/api/ProjectSettings"))["objectNameLabel"]!.GetValue<string>() == "Beta objectName", "Beta labels reflected");
        Check((await client.GetAsync(download)).Headers.CacheControl?.NoStore == true, "project responses are not cached");
        using (var hostile = new HttpRequestMessage(HttpMethod.Post, "/api/projects/switch") {
            Content = JsonContent.Create(new { id = Id("Alpha"), revision = betaRevision })
        })
        {
            hostile.Headers.Add("Origin", "https://evil.example");
            Check((await client.SendAsync(hostile)).StatusCode == HttpStatusCode.Forbidden, "cross-origin switch rejected before CORS");
        }
        Check((await client.PostAsJsonAsync("/api/projects/switch", new { id = Id("Empty"), revision = betaRevision })).IsSuccessStatusCode, "switch to empty project");
        client.DefaultRequestHeaders.Remove("X-CREC-Project");
        var emptyRevision = (await GetObject("/api/projects/status"))["revision"]!.GetValue<string>();
        Check((await client.GetFromJsonAsync<JsonArray>("/api/collections"))!.Count == 0, "empty project has no previous collections");
        Check((await client.PostAsJsonAsync("/api/projects/switch", new { id = Id("Alpha"), revision = emptyRevision })).IsSuccessStatusCode, "return to Alpha");
        Check((await client.GetStringAsync("/api/collections")).Contains("Alpha collection"), "return reloads Alpha data");
        if (serve)
        {
            Console.WriteLine("UI test server: " + address);
            await app.WaitForShutdownAsync();
        }
        else await app.StopAsync();
    }
}
