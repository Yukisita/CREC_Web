namespace CREC_Web.Services.Chat;

public sealed class ChatOptions
{
    public const string SectionName = "AiChat";
    // Include /v1 in the API base URL, including any provider-specific path prefix.
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234/v1/";
    public string Model { get; set; } = "google/gemma-4-e2b";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 120;
    public int MaxContextCharacters { get; set; } = 3000;
    public int MaxHistoryTurns { get; set; } = 10;
}
