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
            foreach (var name in new[] { "Desktop A", "Desktop B" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                var labels = new JsonObject();
                foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
                    labels[key] = new JsonObject { ["displayName"] = key };
                File.WriteAllText(Path.Combine(root, name + ".crec"), new JsonObject {
                    ["projectSettings"] = new JsonObject { ["projectName"] = name, ["projectLocation"] = name },
                    ["labelSettings"] = labels
                }.ToJsonString());
            }
            var port = FreePortPair();
            var session = await host.StartAsync(new(Path.Combine(root, "Desktop A.crec"), port, false));
            var cookie = await host.CreateAdministratorSessionAsync(session.FrontendUri);
            using var client = new HttpClient { BaseAddress = session.FrontendUri };
            client.DefaultRequestHeaders.Add("Cookie", cookie.Name + "=" + cookie.Value);
            client.DefaultRequestHeaders.Add("X-CREC-Request", "1");
            var listing = (await client.GetFromJsonAsync<JsonObject>("/api/projects"))!;
            var targetLocation = Path.GetFileName(root) + "/Desktop B.crec";
            var target = listing["projects"]!.AsArray().Single(p => p!["location"]!.GetValue<string>() == targetLocation)!;
            var current = (await client.GetFromJsonAsync<JsonObject>("/api/projects/status"))!;
            var switched = await client.PostAsJsonAsync("/api/projects/switch", new {
                id = target["id"]!.GetValue<string>(), revision = current["revision"]!.GetValue<string>()
            });
            switched.EnsureSuccessStatusCode();
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
            await host.CreateAdministratorSessionAsync(recovered.FrontendUri);
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
