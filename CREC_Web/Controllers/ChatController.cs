/*
CREC Web - Chat Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Proxies chat requests to a local Ollama instance running on the server.
Ollama URL and model are configured in appsettings.json under "Ollama:Url"
and "Ollama:Model". Defaults: http://localhost:11434 / llama3.2.
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
    /// Ollamaへのチャットリクエストをサーバ側でプロキシする
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Message))
        {
            return BadRequest(new { error = "Message is required." });
        }

        var ollamaUrl = (_configuration["Ollama:Url"] ?? "http://localhost:11434").TrimEnd('/');
        var model = (_configuration["Ollama:Model"] ?? "llama3.2").Trim();

        var systemPrompt = BuildSystemPrompt(
            request.Lang ?? "ja",
            request.PageTitle ?? "CREC Web",
            request.PageContext ?? string.Empty,
            request.ProjectName ?? "CREC Web");

        // 会話履歴を構築
        var messages = new List<OllamaMessage>
        {
            new() { Role = "system", Content = systemPrompt }
        };

        if (request.History != null)
        {
            foreach (var msg in request.History)
            {
                if (!string.IsNullOrWhiteSpace(msg.Role) && !string.IsNullOrWhiteSpace(msg.Content))
                {
                    messages.Add(new OllamaMessage { Role = msg.Role, Content = msg.Content });
                }
            }
        }

        messages.Add(new OllamaMessage { Role = "user", Content = request.Message });

        var ollamaRequest = new OllamaChatRequest
        {
            Model = model,
            Messages = messages,
            Stream = false
        };

        try
        {
            var client = _httpClientFactory.CreateClient();
            var json = JsonSerializer.Serialize(ollamaRequest, _jsonOptions);
            using var content = new StringContent(json, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            _logger.LogInformation("Forwarding chat request to Ollama: {Url}, model={Model}", ollamaUrl, model);
            var response = await client.PostAsync($"{ollamaUrl}/v1/chat/completions", content);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning("Ollama returned HTTP {Status}: {Body}", (int)response.StatusCode, errorBody);
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

            return Ok(new { text });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to Ollama at {Url}", ollamaUrl);
            return StatusCode(503, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error in ChatController");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string BuildSystemPrompt(string lang, string pageTitle, string pageContext, string projectName)
    {
        const string actionDocsJa = @"## 実行可能な操作
ユーザーが操作を要求した場合は、返答の中に以下のJSON形式でアクションを含めることができます（複数可）:
<action>{""type"":""search"",""text"":""検索テキスト""}</action>  — ホームページで指定テキストを検索する
<action>{""type"":""openCollection"",""id"":""コレクションID""}</action>  — 指定IDのコレクションを新しいタブで開く
<action>{""type"":""showAdminPanel""}</action>  — 管理パネルを表示する
<action>{""type"":""navigate"",""path"":""/""}</action>  — 指定パスのページへ移動する（例: ""/""はホームページ）

操作を含める場合は、必ずその内容を日本語で説明してください。";

        const string actionDocsEn = @"## Available Operations
When the user requests an action, you may include one or more actions anywhere in your response:
<action>{""type"":""search"",""text"":""search text""}</action>  — Search on the home page
<action>{""type"":""openCollection"",""id"":""collection ID""}</action>  — Open collection in a new tab
<action>{""type"":""showAdminPanel""}</action>  — Open the admin panel
<action>{""type"":""navigate"",""path"":""/""}</action>  — Navigate to a page (e.g. ""/"" for home)

When including an action, describe what you are doing in English.";

        const string actionDocsDe = @"## Verfügbare Operationen
Wenn der Benutzer eine Aktion anfordert, können Sie eine oder mehrere Aktionen in Ihre Antwort einfügen:
<action>{""type"":""search"",""text"":""Suchtext""}</action>  — Auf der Startseite suchen
<action>{""type"":""openCollection"",""id"":""Sammlungs-ID""}</action>  — Sammlung in neuem Tab öffnen
<action>{""type"":""showAdminPanel""}</action>  — Admin-Panel öffnen
<action>{""type"":""navigate"",""path"":""/""}</action>  — Zu einer Seite navigieren (z. B. ""/"" für Startseite)

Wenn Sie eine Aktion einfügen, beschreiben Sie bitte auf Deutsch, was Sie tun.";

        var noContent = lang switch
        {
            "de" => "(kein Inhalt)",
            "en" => "(no content)",
            _ => "（コンテンツなし）"
        };
        var context = string.IsNullOrWhiteSpace(pageContext) ? noContent : pageContext;

        return lang switch
        {
            "en" => $@"You are a support assistant for {projectName} (CREC Web), a web-based collection and inventory management system.

## Key Features
- Search, list, and view collections (managed items)
- Record name, management code, category, tags, location, and inventory for each collection
- Manage attached images, videos, 3D data (STL), and data files
- Record inventory operations (entry, exit, stocktaking) with history
- Search collections by QR code
- Add/delete collections and edit project settings via the admin panel

## Current Page: {pageTitle}
### Page Content (excerpt):
{context}

{actionDocsEn}",

            "de" => $@"Sie sind ein Support-Assistent für {projectName} (CREC Web), ein webbasiertes Sammlungs- und Inventarverwaltungssystem.

## Hauptfunktionen
- Sammlungen (verwaltete Artikel) suchen, auflisten und anzeigen
- Name, Verwaltungscode, Kategorie, Tags, Standort und Bestand für jede Sammlung erfassen
- Angehängte Bilder, Videos, 3D-Daten (STL) und Datendateien verwalten
- Bestandsoperationen (Eingang, Ausgang, Inventur) mit Verlauf aufzeichnen
- Sammlungen per QR-Code suchen
- Sammlungen über das Admin-Panel hinzufügen/löschen und Projekteinstellungen bearbeiten

## Aktuelle Seite: {pageTitle}
### Seiteninhalt (Auszug):
{context}

{actionDocsDe}",

            _ => $@"あなたは{projectName}（CREC Web）のサポートアシスタントです。CREC WebはWebベースのコレクション・在庫管理システムです。

## システムの主な機能
- コレクション（管理対象物品）の検索・一覧表示・詳細表示
- 各コレクションに名称・管理コード・カテゴリ・タグ・場所・在庫数などを記録
- 画像・動画・3Dデータ（STL）・データファイルの添付と管理
- 在庫操作（入庫・出庫・棚卸し）の記録と履歴管理
- QRコードによるコレクション検索
- 管理パネルからコレクションの追加・削除・プロジェクト設定の編集

## 現在のページ: {pageTitle}
### ページ内容（抜粋）:
{context}

{actionDocsJa}"
        };
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

// ===== Ollama request models =====

internal class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OllamaMessage> Messages { get; set; } = [];

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }
}

internal class OllamaMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}
