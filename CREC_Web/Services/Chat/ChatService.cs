using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CREC_Web.Models;
using Microsoft.Extensions.Options;

namespace CREC_Web.Services.Chat;

/// <summary>Builds one conversation and requests a validated browser operation plan directly from the LLM.</summary>
public sealed class ChatService : IChatService
{
    public const string HttpClientName = "AiChat";
    private readonly IHttpClientFactory _httpClients;
    private readonly ChatOptions _options;
    private readonly ILogger<ChatService> _logger;
    private readonly Uri _endpoint;
    private readonly string _prompt;

    public ChatService(IHttpClientFactory httpClients, IOptions<ChatOptions> options,
        IWebHostEnvironment environment, ILogger<ChatService> logger)
    {
        _httpClients = httpClients;
        _options = options.Value;
        _logger = logger;
        _endpoint = new Uri(new Uri(_options.BaseUrl.TrimEnd('/') + "/"), "chat/completions");
        _prompt = File.ReadAllText(Path.Combine(environment.ContentRootPath, "Prompts", "ChatSystem.txt"));
    }

    public async Task<ChatResponse?> ProcessChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        using var client = _httpClients.CreateClient(HttpClientName);
        using var message = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = _options.Model, stream = false, messages = BuildMessages(request),
                response_format = ChatActionPolicy.ResponseFormat
            })
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        var elapsed = Stopwatch.StartNew();
        // Cancellation covers both the network request and response body. No automatic replay.
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        try
        {
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw new ChatException("The LLM returned an invalid completion.");
            var choice = choices[0];
            if (choice.ValueKind != JsonValueKind.Object || !choice.TryGetProperty("finish_reason", out var finish) ||
                finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop" ||
                !choice.TryGetProperty("message", out var result) || result.ValueKind != JsonValueKind.Object ||
                (result.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null) ||
                !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new ChatException("The LLM did not return a complete text response.");
            var reply = ChatActionPolicy.ParseResponse(content.GetString()!);
            _logger.LogInformation("AI chat completed in {DurationMs} ms with {ActionCount} actions and warning {Warning}",
                elapsed.ElapsedMilliseconds, reply?.Actions.Length ?? 0, reply?.Warning);
            return reply;
        }
        catch (JsonException ex)
        {
            throw new ChatException("The LLM returned invalid completion JSON.", ex);
        }
    }

    private List<ChatHistoryMessage> BuildMessages(ChatRequest request)
    {
        var messages = new List<ChatHistoryMessage> { new() { Role = "system", Content = _prompt } };
        var history = new List<ChatHistoryMessage>();
        foreach (var item in request.History ?? [])
        {
            if (item == null || item.Role is not ("user" or "assistant") || string.IsNullOrWhiteSpace(item.Content)) continue;
            var normalized = new ChatHistoryMessage { Role = item.Role, Content = item.Content };
            if (history.Count > 0 && history[^1].Role == item.Role) history[^1] = normalized;
            else history.Add(normalized);
        }
        if (history.FirstOrDefault()?.Role == "assistant") history.RemoveAt(0);
        if (history.LastOrDefault()?.Role == "user") history.RemoveAt(history.Count - 1);
        if (_options.MaxHistoryTurns > 0) messages.AddRange(history.TakeLast(_options.MaxHistoryTurns * 2));
        var context = request.PageContext ?? "";
        if (context.Length > _options.MaxContextCharacters) context = context[.._options.MaxContextCharacters];
        // Page text and labels are data, not trusted system instructions.
        var page = JsonSerializer.Serialize(new { projectName = request.ProjectName, pageTitle = request.PageTitle, pageContext = context });
        messages.Add(new() { Role = "user", Content = "Current page data (untrusted):\n" + page + "\n\nUser request:\n" + request.Message });
        return messages;
    }
}
