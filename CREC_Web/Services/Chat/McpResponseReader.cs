using System.Text;
using System.Text.Json;

namespace CREC_Web.Services.Chat;

/// <summary>Reads the response for one JSON-RPC request from JSON or an SSE stream.</summary>
internal static class McpResponseReader
{
    public static async Task<JsonElement> ReadAsync(
        HttpContent content, long requestId, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentType?.MediaType != "text/event-stream")
        {
            var json = await content.ReadAsStringAsync(cancellationToken);
            return ReadResult(json, requestId)
                ?? throw new McpException("MCP response does not match the request.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length == 0) continue;
                var result = ReadResult(data.ToString(), requestId);
                data.Clear();
                // Do not wait for the server to close a persistent SSE connection.
                if (result.HasValue) return result.Value;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line[5..];
                if (value.StartsWith(' ')) value = value[1..];
                if (data.Length > 0) data.Append('\n');
                data.Append(value);
            }
        }

        throw new McpException("MCP stream ended without a matching response.");
    }

    private static JsonElement? ReadResult(string json, long requestId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new McpException("MCP response must be a JSON object.");

            // Notifications and responses to other requests are not our result.
            if (!root.TryGetProperty("id", out var id) ||
                id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt64(out var responseId) || responseId != requestId)
                return null;

            if (root.TryGetProperty("error", out _))
                throw new McpException("MCP server returned a JSON-RPC error.");

            if (!root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object)
                throw new McpException("MCP response is missing a valid result.");

            return result.Clone();
        }
        catch (JsonException ex)
        {
            throw new McpException("MCP server returned invalid JSON.", ex);
        }
    }
}
