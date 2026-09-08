using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using CREC_Web.Desktop.Services;

internal static class DesktopHostTests
{
    public static async Task Run()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "web", "Projects", "host-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var host = new WebServerHost();
        var notifications = new List<DesktopProjectState>();
        host.ProjectChanged += state => { lock (notifications) notifications.Add(state); };
        try
        {
            var webDirectory = Path.GetFullPath(Path.Combine(root, "../.."));
            foreach (var name in new[] { "Desktop A", "Desktop B" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                var labels = new JsonObject();
                foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
                    labels[key] = new JsonObject { ["displayName"] = key };
                File.WriteAllText(Path.Combine(root, name + ".crec"), new JsonObject {
                    ["projectSettings"] = new JsonObject { ["projectName"] = name,
                        ["projectLocation"] = Path.GetRelativePath(webDirectory, Path.Combine(root, name)) },
                    ["labelSettings"] = labels
                }.ToJsonString());
            }
            var port = FreePortPair();
            var invalidProject = Path.Combine(root, "Invalid.crec");
            File.WriteAllText(invalidProject, "{}");
            try
            {
                await host.StartAsync(new(invalidProject, port, false));
                throw new Exception("Invalid startup project was accepted");
            }
            catch (InvalidOperationException) { }
            if (host.IsRunning) throw new Exception("Failed startup left the web server running");
            Console.WriteLine("PASS: invalid startup settings stop the server without a data-directory fallback");
            var session = await host.StartAsync(new(Path.Combine(root, "Desktop A.crec"), port, false));
            using var client = new HttpClient { BaseAddress = session.FrontendUri };
            client.DefaultRequestHeaders.Add("X-CREC-Request", "1");
            var listing = (await client.GetFromJsonAsync<JsonObject>("/api/projects"))!;
            var targetLocation = Path.GetFileName(root) + "/Desktop B.crec";
            var target = listing["projects"]!.AsArray().Single(p => p!["location"]!.GetValue<string>() == targetLocation)!;
            var current = (await client.GetFromJsonAsync<JsonObject>("/api/projects/status"))!;
            var startupSettings = (await client.GetFromJsonAsync<JsonObject>("/api/ProjectSettings"))!;
            if (Path.GetFullPath(startupSettings["projectDataPath"]!.GetValue<string>(), webDirectory) != Path.Combine(root, "Desktop A"))
                throw new Exception("Startup changed the relative data location");
            var switched = await client.PostAsJsonAsync("/api/projects/switch", new {
                id = target["id"]!.GetValue<string>(), revision = current["revision"]!.GetValue<string>()
            });
            switched.EnsureSuccessStatusCode();
            var switchedSettings = (await client.GetFromJsonAsync<JsonObject>("/api/ProjectSettings"))!;
            var switchedDataPath = Path.GetFullPath(switchedSettings["projectDataPath"]!.GetValue<string>(), webDirectory);
            if (switchedDataPath != Path.Combine(root, "Desktop B")) throw new Exception("Switch changed the relative data location");
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (host.CurrentProject?.Name != "Desktop B" && DateTime.UtcNow < timeout) await Task.Delay(50);
            if (host.CurrentProject?.FilePath != Path.Combine(root, "Desktop B.crec")) throw new Exception("Desktop IPC did not synchronize project path");
            Console.WriteLine("PASS: desktop host receives new project path and title through private IPC");
            await host.StopAsync();
            var finalProject = host.CurrentProject!;
            var restarted = await host.StartAsync(new(finalProject.FilePath, port, true));
            using var restartedClient = new HttpClient { BaseAddress = restarted.FrontendUri };
            var stateAfterRestart = (await restartedClient.GetFromJsonAsync<JsonObject>("/api/projects/status"))!;
            if (stateAfterRestart["name"]!.GetValue<string>() != "Desktop B" || restarted.Port != port)
                throw new Exception("Desktop restart lost project or port");
            var restartedSettings = (await restartedClient.GetFromJsonAsync<JsonObject>("/api/ProjectSettings"))!;
            if (Path.GetFullPath(restartedSettings["projectDataPath"]!.GetValue<string>(), webDirectory) != switchedDataPath)
                throw new Exception("Restart changed the relative data location");
            Console.WriteLine("PASS: startup, switching and restart resolve relative data paths from the same working directory");
            Console.WriteLine("PASS: public restart preserves switched project and HTTP/HTTPS port pair");
            await host.StopAsync();
            if (host.CurrentProject?.Name != "Desktop B") throw new Exception("Missing final shutdown state");
            Console.WriteLine("PASS: final desktop state retained after server shutdown");
            using (var occupied = new TcpListener(IPAddress.Any, port))
            {
                occupied.Start();
                try
                {
                    await host.StartAsync(new(finalProject.FilePath, port, false));
                    throw new Exception("Expected occupied-port startup failure");
                }
                catch (InvalidOperationException) { }
            }
            var recovered = await host.StartAsync(new(finalProject.FilePath, port, false));
            using var recoveredClient = new HttpClient { BaseAddress = recovered.FrontendUri };
            (await recoveredClient.GetAsync("/api/projects")).EnsureSuccessStatusCode();
            await host.StopAsync();
            Console.WriteLine("PASS: desktop host recovers after failed server startup");
        }
        finally
        {
            await host.StopAsync();
            Directory.Delete(root, true);
        }
    }

    private static int FreePortPair()
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var port = Random.Shared.Next(20000, 40000);
            try
            {
                using var first = new TcpListener(IPAddress.Any, port);
                using var second = new TcpListener(IPAddress.Any, port + 1);
                first.Start(); second.Start(); return port;
            }
            catch (SocketException) { }
        }
        throw new Exception("No available port pair for desktop host tests");
    }
}
