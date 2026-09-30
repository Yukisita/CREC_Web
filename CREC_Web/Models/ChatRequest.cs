namespace CREC_Web.Models;

public sealed class ChatRequest
{
    public string Message { get; set; } = string.Empty;
    public List<ChatHistoryMessage>? History { get; set; }
    public string? PageContext { get; set; }
    public string? PageTitle { get; set; }
    public string? ProjectName { get; set; }
}

public sealed class ChatHistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
