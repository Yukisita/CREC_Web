using System.Net;
using System.Net.Http.Json;
using CREC_Web.Controllers;
using CREC_Web.Models;
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
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddControllers().AddApplicationPart(typeof(ChatController).Assembly);
        builder.Services.Configure<McpClientOptions>(options => { options.Url = mcpUrl; options.TimeoutSeconds = 10; });
        builder.Services.AddHttpClient(McpChatClient.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan);
        builder.Services.AddSingleton<IMcpChatClient, McpChatClient>();
        await using var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
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
            return 0;
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
