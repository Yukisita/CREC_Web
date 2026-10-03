using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CREC_Web.Models;
using Microsoft.Extensions.Options;

namespace CREC_Web.Services.Chat;

/// <summary>Owns one MCP session for this application's configured server.</summary>
public sealed class McpChatClient : IMcpChatClient, IDisposable
{
    public const string HttpClientName = "MCP";
    private const string ProtocolVersion = "2025-03-26";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Uri _endpoint;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private Session? _session;
    private long _requestId;

    public McpChatClient(IHttpClientFactory httpClientFactory, IOptions<McpClientOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _endpoint = new Uri(options.Value.Url.TrimEnd('/') + "/mcp");
        _timeout = TimeSpan.FromSeconds(options.Value.TimeoutSeconds);
    }

    public async Task<ChatResponse?> ProcessChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        using var client = _httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 0; ; attempt++)
        {
            var session = await GetSessionAsync(client, deadline.Token);
            try
            {
                var id = Interlocked.Increment(ref _requestId);
                using var message = CreateRequest("tools/call", new
                {
                    name = "process_chat",
                    arguments = new
                    {
                        message = request.Message,
                        history = request.History ?? [],
                        page_context = request.PageContext ?? string.Empty,
                        page_title = request.PageTitle ?? "CREC Web",
                        project_name = request.ProjectName ?? "CREC Web"
                    }
                }, id, session);
                using var response = await SendAsync(client, message, deadline.Token);
                var result = await McpResponseReader.ReadAsync(response.Content, id, deadline.Token);
                return ReadToolResponse(result);
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == HttpStatusCode.NotFound && session.Id != null)
            {
                // A late failure from the old session must not discard a newer one.
                Interlocked.CompareExchange(ref _session, null, session);
                if (attempt > 0) throw;
                // Only a rejected, expired session is retried. Never replay a tool
                // after a timeout or an ambiguous connection failure.
            }
        }
    }

    private async Task<Session> GetSessionAsync(HttpClient client, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _session) is { } existing) return existing;

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_session is { } initialized) return initialized;
            var id = Interlocked.Increment(ref _requestId);
            using var message = CreateRequest("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { },
                clientInfo = new { name = "CREC-Web", version = "1.0" }
            }, id);
            using var response = await SendAsync(client, message, cancellationToken);
            var result = await McpResponseReader.ReadAsync(response.Content, id, cancellationToken);
            if (!result.TryGetProperty("protocolVersion", out var version) ||
                version.ValueKind != JsonValueKind.String || version.GetString() != ProtocolVersion)
                throw new McpException("MCP server selected an unsupported protocol version.");

            response.Headers.TryGetValues("Mcp-Session-Id", out var ids);
            var session = new Session(ids?.FirstOrDefault());
            using var notification = CreateRequest("notifications/initialized", new { }, session: session);
            using var acknowledgement = await SendAsync(client, notification, cancellationToken);

            // Publish only after initialization and acknowledgement both succeed.
            Volatile.Write(ref _session, session);
            return session;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private HttpRequestMessage CreateRequest(string method, object parameters, long? id = null, Session? session = null)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = parameters, id }, options: JsonOptions)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (session?.Id is { } sessionId)
            message.Headers.Add("Mcp-Session-Id", sessionId);
        return message;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static ChatResponse? ReadToolResponse(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind != JsonValueKind.False)
            throw new McpException("MCP chat tool failed.");

        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new McpException("MCP chat tool returned invalid content.");

        var text = new StringBuilder();
        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new McpException("MCP chat tool returned an invalid content block.");
            if (type.GetString() != "text") continue;
            if (!item.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                throw new McpException("MCP chat tool returned invalid text.");
            text.Append(value.GetString());
        }
        if (text.Length == 0) return null;
        try
        {
            var reply = JsonSerializer.Deserialize<ChatResponse>(text.ToString(), JsonOptions);
            if (reply?.Text == null || reply.Actions == null || reply.Actions.Length > 32 ||
                reply.Actions.Any(action => action.ValueKind != JsonValueKind.Object ||
                    !action.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) ||
                (reply.Warning != null && (reply.Actions.Length > 0 ||
                    reply.Warning is not ("invalid_actions" or "deletion_blocked"))))
                throw new McpException("MCP chat tool returned an invalid action plan.");
            return reply.Text.Length == 0 && reply.Actions.Length == 0 && reply.Warning == null ? null : reply;
        }
        catch (JsonException ex)
        {
            throw new McpException("MCP chat tool returned invalid response JSON.", ex);
        }
    }

    public void Dispose() => _initializationLock.Dispose();

    private sealed record Session(string? Id);
}
