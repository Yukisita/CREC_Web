using System.Text.Json;

namespace CREC_Web.Models;

/// <summary>Display text and the independently validated browser action plan.</summary>
public sealed class ChatResponse
{
    public required string Text { get; init; }
    public required JsonElement[] Actions { get; init; }
    public string? Warning { get; init; }
}
