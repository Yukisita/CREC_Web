namespace CREC_Web.Services.Chat;

public sealed class McpClientOptions
{
    public const string SectionName = "McpServer";
    public string Url { get; set; } = "http://127.0.0.1:8765";
    // Includes initialization and the entire response body, including SSE reads.
    public int TimeoutSeconds { get; set; } = 150;
}
