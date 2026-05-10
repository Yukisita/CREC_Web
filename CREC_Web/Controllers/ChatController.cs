/*
CREC Web - Chat Controller (MCP Client)
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

This controller acts as an MCP client that connects to the CREC MCPServer
(a Python MCP server) to process AI chat messages.  Browser requests are
received here, forwarded to the MCP server's process_chat tool, and the
validated AI response is returned to the browser.

Architecture:
  Browser → POST /api/Chat → ChatController (MCP client)
          → MCP tools/call  → Python MCPServer
          → LLM backend (Ollama / LM Studio etc.)
*/

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatController> _logger;

    // Cached MCP session ID (one session shared across requests for this server process)
    private static string? _mcpSessionId;
    private static readonly SemaphoreSlim _mcpInitLock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ChatController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChatController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Receive a chat message from the browser, forward it to the MCP server's
    /// process_chat tool, and return the AI response.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required." });
        }

        var mcpUrl = (_configuration["McpServer:Url"] ?? "http://127.0.0.1:8765").TrimEnd('/');

        try
        {
            var client = _httpClientFactory.CreateClient("MCP");

            // Ensure the MCP session is initialized
            await EnsureMcpInitializedAsync(client, mcpUrl);

            // Call the process_chat tool on the MCP server
            var arguments = new Dictionary<string, object?>
            {
                ["message"]      = request.Message,
                ["history"]      = request.History ?? [],
                ["page_context"] = request.PageContext ?? string.Empty,
                ["page_title"]   = request.PageTitle ?? "CREC Web",
                ["lang"]         = request.Lang ?? "ja",
                ["project_name"] = request.ProjectName ?? "CREC Web"
            };

            var text = await CallMcpToolAsync(client, mcpUrl, "process_chat", arguments);

            if (text == null)
            {
                return Ok(new { error = "empty_response" });
            }

            return Ok(new { text });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to MCP server at {Url}", mcpUrl);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ChatController");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // =========================================================================
    // MCP Protocol Helpers
    // =========================================================================

    /// <summary>
    /// Ensure the MCP session is initialized (idempotent; only runs once per
    /// server process).
    /// </summary>
    private async Task EnsureMcpInitializedAsync(HttpClient client, string mcpUrl)
    {
        if (_mcpSessionId != null) return;

        await _mcpInitLock.WaitAsync();
        try
        {
            if (_mcpSessionId != null) return;

            _logger.LogInformation("Initializing MCP session with server at {Url}", mcpUrl);

            // Send initialize request
            var initRequest = new McpJsonRpcRequest
            {
                Method = "initialize",
                Params = new Dictionary<string, object?>
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"]    = new Dictionary<string, object?>(),
                    ["clientInfo"]      = new Dictionary<string, string>
                    {
                        ["name"]    = "CREC-Web",
                        ["version"] = "1.0"
                    }
                },
                Id = 1
            };

            var (initResult, sessionId) = await SendMcpRequestAsync(client, mcpUrl, initRequest, sessionId: null);

            if (initResult == null)
            {
                _logger.LogWarning("MCP initialize returned no result");
                return;
            }

            _mcpSessionId = sessionId ?? string.Empty;
            _logger.LogInformation("MCP session initialized. SessionId={SessionId}", _mcpSessionId);

            // Send initialized notification (no response expected)
            var notif = new McpJsonRpcRequest
            {
                Method = "notifications/initialized",
                Params = new Dictionary<string, object?>()
                // Notifications have no id
            };
            await SendMcpNotificationAsync(client, mcpUrl, notif);
        }
        finally
        {
            _mcpInitLock.Release();
        }
    }

    /// <summary>
    /// Call a tool on the MCP server and return the text content of the result.
    /// Returns null if the result is empty.
    /// </summary>
    private async Task<string?> CallMcpToolAsync(
        HttpClient client,
        string mcpUrl,
        string toolName,
        Dictionary<string, object?> arguments)
    {
        var request = new McpJsonRpcRequest
        {
            Method = "tools/call",
            Params = new Dictionary<string, object?>
            {
                ["name"]      = toolName,
                ["arguments"] = arguments
            },
            Id = 2
        };

        var (result, newSessionId) = await SendMcpRequestAsync(client, mcpUrl, request, _mcpSessionId);

        // Update session ID if the server issued a new one
        if (!string.IsNullOrEmpty(newSessionId) && newSessionId != _mcpSessionId)
        {
            _mcpSessionId = newSessionId;
        }

        if (result == null) return null;

        // MCP tools/call result: { "content": [{"type":"text","text":"…"}], "isError": false }
        if (result.Value.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var typeProp) &&
                    typeProp.GetString() == "text" &&
                    item.TryGetProperty("text", out var textProp))
                {
                    sb.Append(textProp.GetString());
                }
            }
            var text = sb.ToString();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        return null;
    }

    /// <summary>
    /// Send a JSON-RPC request to the MCP server and return the parsed result
    /// along with the session ID from the response header.
    /// Handles both plain JSON and SSE (text/event-stream) responses.
    /// </summary>
    private async Task<(JsonElement? result, string? sessionId)> SendMcpRequestAsync(
        HttpClient client,
        string mcpUrl,
        McpJsonRpcRequest request,
        string? sessionId)
    {
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{mcpUrl}/mcp");
        httpRequest.Content = content;
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrEmpty(sessionId))
        {
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        using var response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("MCP server returned HTTP {Status}: {Body}", (int)response.StatusCode, errBody);
            response.EnsureSuccessStatusCode(); // throws HttpRequestException
        }

        // Capture new session ID from response header
        response.Headers.TryGetValues("Mcp-Session-Id", out var sessionVals);
        var newSessionId = sessionVals?.FirstOrDefault();

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

        string? resultJson;
        if (mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            resultJson = await ReadSseResultAsync(response);
        }
        else
        {
            resultJson = await response.Content.ReadAsStringAsync();
        }

        if (string.IsNullOrWhiteSpace(resultJson)) return (null, newSessionId);

        try
        {
            var doc = JsonDocument.Parse(resultJson);
            if (doc.RootElement.TryGetProperty("result", out var resultElement))
            {
                return (resultElement, newSessionId);
            }

            if (doc.RootElement.TryGetProperty("error", out var errorElement))
            {
                var msg = errorElement.TryGetProperty("message", out var msgProp)
                    ? msgProp.GetString()
                    : resultJson;
                throw new InvalidOperationException($"MCP error: {msg}");
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse MCP response JSON");
        }

        return (null, newSessionId);
    }

    /// <summary>
    /// Send a JSON-RPC notification (no response expected).
    /// </summary>
    private async Task SendMcpNotificationAsync(HttpClient client, string mcpUrl, McpJsonRpcRequest notification)
    {
        var json = JsonSerializer.Serialize(notification, _jsonOptions);
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{mcpUrl}/mcp");
        httpRequest.Content = content;

        if (!string.IsNullOrEmpty(_mcpSessionId))
        {
            httpRequest.Headers.TryAddWithoutValidation("Mcp-Session-Id", _mcpSessionId);
        }

        try
        {
            using var response = await client.SendAsync(httpRequest);
            // Notifications may return 202 Accepted or 200; we don't process the body.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send MCP notification");
        }
    }

    /// <summary>
    /// Read an SSE (text/event-stream) response and return the JSON from the
    /// last "message" event's data field that contains a JSON-RPC response.
    /// </summary>
    private async Task<string?> ReadSseResultAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? lastResultJson = null;

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (line == null) break;

            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                var data = line["data: ".Length..];
                if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") continue;

                try
                {
                    var doc = JsonDocument.Parse(data);
                    // We're looking for a JSON-RPC response that has "result" or "error"
                    if (doc.RootElement.TryGetProperty("result", out _) ||
                        doc.RootElement.TryGetProperty("error", out _))
                    {
                        lastResultJson = data;
                    }
                }
                catch (JsonException)
                {
                    // Not valid JSON – skip
                }
            }
        }

        return lastResultJson;
    }
}

// =============================================================================
// Request / Response models
// =============================================================================

public class ChatRequest
{
    /// <summary>User's message text</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Conversation history (role: "user" | "assistant")</summary>
    public List<ChatHistoryMessage>? History { get; set; }

    /// <summary>Page context excerpt for RAG</summary>
    public string? PageContext { get; set; }

    /// <summary>Current page title</summary>
    public string? PageTitle { get; set; }

    /// <summary>UI language code ("ja" | "en" | "de")</summary>
    public string? Lang { get; set; }

    /// <summary>CREC Web project name</summary>
    public string? ProjectName { get; set; }
}

public class ChatHistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

// =============================================================================
// MCP protocol models
// =============================================================================

internal class McpJsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public object? Params { get; set; }

    /// <summary>
    /// Request ID. Notifications omit this property.
    /// </summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }
}
