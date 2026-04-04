/*
CREC Web - Chat Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Proxies chat requests to an OpenAI-compatible LLM backend running on the server.
The backend URL and model are configured in appsettings.json under "LlmBackend:Url"
and "LlmBackend:Model". Defaults: http://localhost:11434 / llama3.2.

System prompt templates are loaded from Prompts/system_prompt.{lang}.txt at runtime,
allowing prompt editing without recompilation.

Allowed button/input IDs for AI actions are read from "Chat:SafeButtonIds" and
"Chat:SafeInputIds" in appsettings.json. The LLM response is sanitized on the server
before being returned to the client, so the whitelist never reaches the browser.
*/

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatController> _logger;
    private readonly IWebHostEnvironment _env;

    // キャッシュされたプロンプトテンプレート（言語コード → テンプレート文字列）
    private static readonly ConcurrentDictionary<string, string> _promptCache = new();

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // <action>…</action> タグを検出する正規表現
    private static readonly Regex _actionRegex =
        new(@"<action>([\s\S]*?)<\/action>", RegexOptions.Compiled);

    // ログ出力値のサニタイズに使用する正規表現（制御文字を除去）
    private static readonly Regex _sanitizeLogRegex =
        new(@"[\r\n\t\x00-\x1F\x7F]", RegexOptions.Compiled);

    public ChatController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChatController> logger,
        IWebHostEnvironment env)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
        _env = env;
    }

    /// <summary>
    /// OpenAI互換LLMバックエンドへのチャットリクエストをサーバ側でプロキシする
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required." });
        }

        var llmUrl = (_configuration["LlmBackend:Url"] ?? "http://localhost:11434").TrimEnd('/');
        var model = (_configuration["LlmBackend:Model"] ?? "llama3.2").Trim();

        var systemPrompt = BuildSystemPrompt(
            request.Lang ?? "ja",
            request.PageTitle ?? "CREC Web",
            request.PageContext ?? string.Empty,
            request.ProjectName ?? "CREC Web");

        // 会話履歴を構築
        var messages = new List<LlmMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        if (request.History != null)
        {
            foreach (var msg in request.History)
            {
                if (!string.IsNullOrWhiteSpace(msg.Role) && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    messages.Add(new LlmMessage { Role = msg.Role, Content = msg.Content });
                }
            }
        }

        messages.Add(new LlmMessage { Role = "user", Content = request.Message });

        var llmRequest = new LlmChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = false
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            var json = JsonSerializer.Serialize(llmRequest, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _logger.LogInformation("Forwarding chat request to LLM backend: {Url}, model={Model}", llmUrl, model);
            var response = await client.PostAsync($"{llmUrl}/v1/chat/completions", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("LLM backend returned HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
                string errorMessage;
                try
                {
                    var errData = JsonDocument.Parse(errorBody);
                    errorMessage = errData.RootElement
                        .GetProperty("error")
                        .GetProperty("message")
                        .GetString() ?? $"HTTP {(int)response.StatusCode}";
                }
                catch
                {
                    errorMessage = $"HTTP {(int)response.StatusCode}";
                }
                return StatusCode((int)response.StatusCode, new { error = errorMessage });
            }

            var responseBody = await response.Content.ReadAsStringAsync();
            var data = JsonDocument.Parse(responseBody);
            var text = data.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrEmpty(text))
            {
                return Ok(new { error = "empty_response" });
            }

            // LLMレスポンス内のアクションをサーバ側でホワイトリスト検証し、
            // 許可されていないIDを含むアクションを除去してからクライアントへ返す
            var sanitizedText = SanitizeLlmResponse(text);

            return Ok(new { text = sanitizedText });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to LLM backend at {Url}", llmUrl);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ChatController");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// LLMレスポンス内の <action> タグをサーバ側のホワイトリストで検証し、
    /// 許可されていないIDを含む clickButton / fillInput アクションを除去する。
    /// </summary>
    private string SanitizeLlmResponse(string text)
    {
        var safeButtonIds = _configuration
            .GetSection("Chat:SafeButtonIds")
            .Get<string[]>() ?? [];

        var safeInputIds = _configuration
            .GetSection("Chat:SafeInputIds")
            .Get<string[]>() ?? [];

        var buttonSet = new HashSet<string>(safeButtonIds, StringComparer.Ordinal);
        var inputSet  = new HashSet<string>(safeInputIds,  StringComparer.Ordinal);

        return _actionRegex.Replace(text, match =>
        {
            try
            {
                var cmd = JsonDocument.Parse(match.Groups[1].Value);
                if (!cmd.RootElement.TryGetProperty("type", out var typeProp))
                    return match.Value;

                var type = typeProp.GetString();

                if (type == "clickButton")
                {
                    var id = cmd.RootElement.TryGetProperty("id", out var idProp)
                        ? idProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!buttonSet.Contains(id))
                    {
                        _logger.LogWarning("Blocked unsafe clickButton action with id: {Id}", SanitizeForLog(id));
                        return string.Empty;
                    }
                }
                else if (type == "fillInput")
                {
                    var id = cmd.RootElement.TryGetProperty("id", out var idProp)
                        ? idProp.GetString() ?? string.Empty
                        : string.Empty;

                    if (!inputSet.Contains(id))
                    {
                        _logger.LogWarning("Blocked unsafe fillInput action with id: {Id}", SanitizeForLog(id));
                        return string.Empty;
                    }
                }

                return match.Value;
            }
            catch (JsonException)
            {
                // 解析できないアクションはそのまま通す（クライアント側でも無視される）
                return match.Value;
            }
        });
    }

    /// <summary>
    /// 指定言語のシステムプロンプトを Prompts/ ディレクトリのテンプレートファイルから読み込む。
    /// ファイルが存在しない場合は空文字列を返す。読み込んだ結果はキャッシュする。
    /// </summary>
    private string LoadPromptTemplate(string lang)
    {
        return _promptCache.GetOrAdd(lang, l =>
        {
            var fileName = $"system_prompt.{l}.txt";
            var filePath = Path.Combine(_env.ContentRootPath, "Prompts", fileName);

            if (!System.IO.File.Exists(filePath))
            {
                _logger.LogWarning("Prompt template not found: {Path}", SanitizeForLog(filePath));
                return string.Empty;
            }

            _logger.LogInformation("Loading prompt template: {Path}", SanitizeForLog(filePath));
            return System.IO.File.ReadAllText(filePath, Encoding.UTF8);
        });
    }

    /// <summary>
    /// 指定言語のシステムプロンプトを構築する。
    /// テンプレートファイルの {{projectName}}・{{pageTitle}}・{{context}} を置換して返す。
    /// </summary>
    private string BuildSystemPrompt(string lang, string pageTitle, string pageContext, string projectName)
    {
        // 対応言語以外は日本語にフォールバック
        var resolvedLang = lang is "en" or "de" ? lang : "ja";

        var template = LoadPromptTemplate(resolvedLang);

        if (string.IsNullOrWhiteSpace(template))
        {
            _logger.LogWarning("System prompt template is empty for lang={Lang}", SanitizeForLog(resolvedLang));
            return string.Empty;
        }

        var noContent = resolvedLang switch
        {
            "de" => "(kein Inhalt)",
            "en" => "(no content)",
            _    => "（コンテンツなし）"
        };

        var context = string.IsNullOrWhiteSpace(pageContext) ? noContent : pageContext;

        return template
            .Replace("{{projectName}}", projectName)
            .Replace("{{pageTitle}}", pageTitle)
            .Replace("{{context}}", context);
    }

    /// <summary>
    /// ログ出力前に値をサニタイズする（ログフォージング防止）。
    /// 制御文字（改行・タブ等）をアンダースコアに置換し、200文字に切り詰める。
    /// </summary>
    private static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var truncated = value.Length > 200 ? value[..200] : value;
        return _sanitizeLogRegex.Replace(truncated, "_");
    }
}

// ===== Request / Response models =====

public class ChatRequest
{
    /// <summary>ユーザーのメッセージ</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>会話履歴 (role: "user"|"assistant")</summary>
    public List<ChatHistoryMessage>? History { get; set; }

    /// <summary>ページコンテキスト（RAG用）</summary>
    public string? PageContext { get; set; }

    /// <summary>現在のページタイトル</summary>
    public string? PageTitle { get; set; }

    /// <summary>現在の言語 ("ja" | "en" | "de")</summary>
    public string? Lang { get; set; }

    /// <summary>プロジェクト名</summary>
    public string? ProjectName { get; set; }
}

public class ChatHistoryMessage
{
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

// ===== LLM backend request models =====

internal class LlmChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<LlmMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

internal class LlmMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

