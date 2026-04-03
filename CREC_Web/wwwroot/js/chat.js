/*
CREC Web - AI Chat Support
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Uses Google Gemini API
(Google LLC, Mountain View, California, USA)
API key is stored only in the user's browser localStorage and is
sent exclusively to Google's Gemini API over HTTPS.
*/

// チャット設定定数
const CHAT_API_KEY_STORAGE = 'crec_chat_gemini_api_key';
const CHAT_MODEL = 'gemini-2.0-flash';
const CHAT_MAX_OUTPUT_TOKENS = 1024;
const CHAT_HISTORY_MAX = 20; // コンテキストに保持する最大メッセージ数
const CHAT_PAGE_CONTEXT_MAX = 2000; // RAGに含めるページコンテンツの最大文字数

// チャット状態
let chatMessages = []; // { role: 'user'|'model', text: string }
let chatIsOpen = false;
let chatIsSending = false;

/**
 * 現在のページのコンテキストを取得する（RAG用）
 * テーブル本体など大量のデータは除外して要約する
 * @returns {string}
 */
function getChatPageContext() {
    const main = document.querySelector('main');
    if (!main) return '';

    const clone = main.cloneNode(true);

    // テーブル行など大量データは省略
    clone.querySelectorAll('tbody').forEach(tbody => {
        tbody.innerHTML = '';
    });

    // チャットパネル自体は除外
    const chatPanel = clone.querySelector('#chatPanel');
    if (chatPanel) chatPanel.remove();

    const text = stripHtmlToText(clone.innerHTML);
    return text.substring(0, CHAT_PAGE_CONTEXT_MAX);
}

/**
 * システムプロンプトを現在の言語に合わせて構築する
 * @returns {string}
 */
function buildChatSystemPrompt() {
    const lang = currentLanguage || 'ja';
    const pageTitle = document.title || 'CREC Web';
    const pageContext = getChatPageContext();
    const projectName = (typeof projectSettings !== 'undefined' && projectSettings.projectName)
        ? projectSettings.projectName
        : 'CREC Web';

    const actionDocs = {
        ja: `## 実行可能な操作
ユーザーが操作を要求した場合は、返答の中に以下のJSON形式でアクションを含めることができます（複数可）:
<action>{"type":"search","text":"検索テキスト"}</action>  — ホームページで指定テキストを検索する
<action>{"type":"openCollection","id":"コレクションID"}</action>  — 指定IDのコレクションを新しいタブで開く
<action>{"type":"showAdminPanel"}</action>  — 管理パネルを表示する
<action>{"type":"navigate","path":"/"}</action>  — 指定パスのページへ移動する（例: "/"はホームページ）

操作を含める場合は、必ずその内容を日本語で説明してください。`,

        en: `## Available Operations
When the user requests an action, you may include one or more actions anywhere in your response:
<action>{"type":"search","text":"search text"}</action>  — Search on the home page
<action>{"type":"openCollection","id":"collection ID"}</action>  — Open collection in a new tab
<action>{"type":"showAdminPanel"}</action>  — Open the admin panel
<action>{"type":"navigate","path":"/"}</action>  — Navigate to a page (e.g. "/" for home)

When including an action, describe what you are doing in English.`,

        de: `## Verfügbare Operationen
Wenn der Benutzer eine Aktion anfordert, können Sie eine oder mehrere Aktionen in Ihre Antwort einfügen:
<action>{"type":"search","text":"Suchtext"}</action>  — Auf der Startseite suchen
<action>{"type":"openCollection","id":"Sammlungs-ID"}</action>  — Sammlung in neuem Tab öffnen
<action>{"type":"showAdminPanel"}</action>  — Admin-Panel öffnen
<action>{"type":"navigate","path":"/"}</action>  — Zu einer Seite navigieren (z. B. "/" für Startseite)

Wenn Sie eine Aktion einfügen, beschreiben Sie bitte auf Deutsch, was Sie tun.`
    };

    const systemDesc = {
        ja: `あなたは${projectName}（CREC Web）のサポートアシスタントです。CREC WebはWebベースのコレクション・在庫管理システムです。

## システムの主な機能
- コレクション（管理対象物品）の検索・一覧表示・詳細表示
- 各コレクションに名称・管理コード・カテゴリ・タグ・場所・在庫数などを記録
- 画像・動画・3Dデータ（STL）・データファイルの添付と管理
- 在庫操作（入庫・出庫・棚卸し）の記録と履歴管理
- QRコードによるコレクション検索
- 管理パネルからコレクションの追加・削除・プロジェクト設定の編集

## 現在のページ: ${pageTitle}
### ページ内容（抜粋）:
${pageContext || '（コンテンツなし）'}

${actionDocs.ja}`,

        en: `You are a support assistant for ${projectName} (CREC Web), a web-based collection and inventory management system.

## Key Features
- Search, list, and view collections (managed items)
- Record name, management code, category, tags, location, and inventory for each collection
- Manage attached images, videos, 3D data (STL), and data files
- Record inventory operations (entry, exit, stocktaking) with history
- Search collections by QR code
- Add/delete collections and edit project settings via the admin panel

## Current Page: ${pageTitle}
### Page Content (excerpt):
${pageContext || '(no content)'}

${actionDocs.en}`,

        de: `Sie sind ein Support-Assistent für ${projectName} (CREC Web), ein webbasiertes Sammlungs- und Inventarverwaltungssystem.

## Hauptfunktionen
- Sammlungen (verwaltete Artikel) suchen, auflisten und anzeigen
- Name, Verwaltungscode, Kategorie, Tags, Standort und Bestand für jede Sammlung erfassen
- Angehängte Bilder, Videos, 3D-Daten (STL) und Datendateien verwalten
- Bestandsoperationen (Eingang, Ausgang, Inventur) mit Verlauf aufzeichnen
- Sammlungen per QR-Code suchen
- Sammlungen über das Admin-Panel hinzufügen/löschen und Projekteinstellungen bearbeiten

## Aktuelle Seite: ${pageTitle}
### Seiteninhalt (Auszug):
${pageContext || '(kein Inhalt)'}

${actionDocs.de}`
    };

    return systemDesc[lang] || systemDesc.en;
}

/**
 * AIレスポンス内のアクションタグを解析し実行する
 * アクションタグを除いたクリーンなテキストを返す
 * @param {string} text - AIのレスポンステキスト
 * @returns {string} アクションタグを除去したテキスト
 */
function processChatActions(text) {
    const actionRegex = /<action>([\s\S]*?)<\/action>/g;
    const actions = [];
    let match;

    while ((match = actionRegex.exec(text)) !== null) {
        try {
            const cmd = JSON.parse(match[1]);
            if (cmd && typeof cmd.type === 'string') {
                actions.push(cmd);
            }
        } catch (e) {
            console.warn('Failed to parse chat action JSON:', match[1], e);
        }
    }

    // アクションタグを除去したテキストを返す
    const cleanText = text.replace(/<action>[\s\S]*?<\/action>/g, '').trim();

    // アクションをUIが更新されてから実行
    if (actions.length > 0) {
        setTimeout(() => {
            actions.forEach(cmd => {
                try {
                    executeChatAction(cmd);
                } catch (e) {
                    console.warn('Failed to execute chat action:', cmd, e);
                }
            });
        }, 400);
    }

    return cleanText;
}

/**
 * チャットアクションを実行する
 * @param {object} cmd - アクションオブジェクト
 */
function executeChatAction(cmd) {
    switch (cmd.type) {
        case 'search':
            if (typeof cmd.text === 'string' && isMainSearchPage()) {
                const searchInput = document.getElementById('searchText');
                if (searchInput) {
                    searchInput.value = cmd.text;
                    const searchBtn = document.getElementById('searchButton');
                    if (searchBtn) searchBtn.click();
                }
            }
            break;

        case 'openCollection':
            if (cmd.id && typeof cmd.id === 'string') {
                openCollectionWindow(cmd.id);
            }
            break;

        case 'showAdminPanel':
            openAdminPanel();
            break;

        case 'navigate':
            // 自サーバーの絶対パスのみ許可（外部URLや protocol-relative URLを除外）
            if (typeof cmd.path === 'string' &&
                cmd.path.startsWith('/') &&
                !cmd.path.startsWith('//')) {
                window.location.href = cmd.path;
            }
            break;

        default:
            console.warn('Unknown chat action type:', cmd.type);
    }
}

/**
 * AIレスポンステキストを安全なHTMLに変換する
 * HTMLエスケープ後に最低限のマークダウンのみ適用する
 * @param {string} text - AIのレスポンステキスト（アクションタグ除去済み）
 * @returns {string} 安全なHTML文字列
 */
function renderChatMarkdown(text) {
    // 最初にHTMLエスケープ（XSS防止）
    let html = escapeHtml(text);

    // 基本的なマークダウン変換（エスケープ後なので安全）
    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/\*(.+?)\*/g, '<em>$1</em>');
    html = html.replace(/`(.+?)`/g, '<code>$1</code>');
    html = html.replace(/\n/g, '<br>');

    return html;
}

/**
 * Gemini APIにメッセージを送信し、レスポンスを取得する
 * @param {string} userText - ユーザーのメッセージ
 * @returns {Promise<{error: boolean, text?: string, message?: string}>}
 */
async function sendChatToGemini(userText) {
    const apiKey = localStorage.getItem(CHAT_API_KEY_STORAGE);
    if (!apiKey || !apiKey.trim()) {
        return { error: true, message: t('chat-no-api-key') };
    }

    const systemPrompt = buildChatSystemPrompt();

    // 会話履歴を構築（最新 CHAT_HISTORY_MAX 件のみ）
    const recentMessages = chatMessages.slice(-CHAT_HISTORY_MAX);
    const contents = recentMessages.map(msg => ({
        role: msg.role,
        parts: [{ text: msg.text }]
    }));

    // 現在のユーザーメッセージを追加
    contents.push({
        role: 'user',
        parts: [{ text: userText }]
    });

    const requestBody = {
        system_instruction: {
            parts: [{ text: systemPrompt }]
        },
        contents,
        generationConfig: {
            maxOutputTokens: CHAT_MAX_OUTPUT_TOKENS,
            temperature: 0.7
        }
    };

    try {
        const response = await fetch(
            `https://generativelanguage.googleapis.com/v1beta/models/${CHAT_MODEL}:generateContent?key=${encodeURIComponent(apiKey.trim())}`,
            {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            }
        );

        if (!response.ok) {
            let errorMsg = `HTTP ${response.status}`;
            try {
                const errData = await response.json();
                errorMsg = errData?.error?.message || errorMsg;
            } catch { /* ignore parse error */ }
            return { error: true, message: errorMsg };
        }

        const data = await response.json();
        const text = data.candidates?.[0]?.content?.parts?.[0]?.text;
        if (!text) {
            return { error: true, message: t('chat-error-empty-response') };
        }

        return { error: false, text };
    } catch (e) {
        return { error: true, message: e.message || t('chat-error') };
    }
}

// =====================
// UI 関数
// =====================

/**
 * チャットメッセージリストの末尾にメッセージを追加する
 * @param {'user'|'model'} role - メッセージのロール
 * @param {string} htmlContent - 表示するHTMLコンテンツ（信頼済み）
 * @param {string} [elementId] - 後から参照するためのID（省略可）
 */
function appendChatMessage(role, htmlContent, elementId) {
    const messages = document.getElementById('chatMessages');
    if (!messages) return;

    const div = document.createElement('div');
    div.className = `chat-message chat-message-${role}`;
    if (elementId) div.id = elementId;

    const bubble = document.createElement('div');
    bubble.className = 'chat-bubble';
    bubble.innerHTML = htmlContent;

    div.appendChild(bubble);
    messages.appendChild(div);
    scrollChatToBottom();
}

/**
 * メッセージリストを最下部にスクロールする
 */
function scrollChatToBottom() {
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.scrollTop = messages.scrollHeight;
    }
}

/**
 * ユーザーメッセージを送信してAIのレスポンスを取得・表示する
 */
async function submitChatMessage() {
    if (chatIsSending) return;

    const input = document.getElementById('chatInput');
    if (!input) return;

    const userText = input.value.trim();
    if (!userText) return;

    input.value = '';

    // ユーザーメッセージを表示
    appendChatMessage('user', escapeHtml(userText));

    // 履歴に追加
    chatMessages.push({ role: 'user', text: userText });

    // 送信中フラグ
    chatIsSending = true;
    const sendBtn = document.getElementById('chatSendBtn');
    if (sendBtn) sendBtn.disabled = true;

    // 思考中インジケーターを表示
    const thinkingId = 'chat-thinking-' + Date.now();
    appendChatMessage(
        'model',
        `<span class="chat-thinking"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${escapeHtml(t('chat-thinking'))}</span>`,
        thinkingId
    );

    try {
        const result = await sendChatToGemini(userText);

        // 思考中インジケーターを削除
        const thinkingEl = document.getElementById(thinkingId);
        if (thinkingEl) thinkingEl.remove();

        if (result.error) {
            appendChatMessage(
                'model',
                `<span class="text-danger"><i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(result.message)}</span>`
            );
        } else {
            // アクションを処理してテキストを整形
            const cleanText = processChatActions(result.text);
            const renderedHtml = renderChatMarkdown(cleanText);
            appendChatMessage('model', renderedHtml);

            // 履歴に保存（元テキスト、アクションタグも含む）
            chatMessages.push({ role: 'model', text: result.text });
        }
    } finally {
        chatIsSending = false;
        if (sendBtn) sendBtn.disabled = false;
        if (input) input.focus();
    }
}

// =====================
// パネル開閉
// =====================

/**
 * チャットパネルを開く
 */
function openChatPanel() {
    const panel = document.getElementById('chatPanel');
    if (panel) {
        panel.classList.add('open');
        chatIsOpen = true;
        const input = document.getElementById('chatInput');
        if (input) input.focus();
        scrollChatToBottom();
    }
}

/**
 * チャットパネルを閉じる
 */
function closeChatPanel() {
    const panel = document.getElementById('chatPanel');
    if (panel) {
        panel.classList.remove('open');
        chatIsOpen = false;
    }
}

/**
 * チャットパネルの表示/非表示を切り替える
 */
function toggleChatPanel() {
    if (chatIsOpen) {
        closeChatPanel();
    } else {
        openChatPanel();
    }
}

// =====================
// API キー設定
// =====================

/**
 * チャット設定モーダルを開く
 */
function openChatSettings() {
    const overlay = document.getElementById('chatSettingsOverlay');
    const modal = document.getElementById('chatSettingsModal');
    const input = document.getElementById('chatApiKeyInput');

    if (input) {
        input.value = localStorage.getItem(CHAT_API_KEY_STORAGE) || '';
    }
    if (overlay) overlay.classList.add('show');
    if (modal) modal.classList.add('show');
}

/**
 * チャット設定モーダルを閉じる
 */
function closeChatSettings() {
    const overlay = document.getElementById('chatSettingsOverlay');
    const modal = document.getElementById('chatSettingsModal');
    if (overlay) overlay.classList.remove('show');
    if (modal) modal.classList.remove('show');
}

/**
 * Gemini APIキーを localStorage に保存する
 *
 * セキュリティ上の注意:
 * このキーはユーザー自身が提供するサードパーティのAPIキーであり、当サーバーには
 * 送信されません。キーはこのブラウザの localStorage にのみ保存され、Google の
 * Gemini API エンドポイントに対してのみ HTTPS 経由で使用されます。
 * ブラウザの localStorage はプレーンテキストであることをユーザーに通知済みです。
 *
 * Security note: The value stored here is a user-supplied third-party API key
 * that is never sent to our server. It is persisted only in this browser's
 * localStorage and is transmitted exclusively to Google's Gemini API over HTTPS.
 * The settings UI informs the user that the key is stored in the browser.
 */
function saveChatApiKey() {
    const input = document.getElementById('chatApiKeyInput');
    if (!input) return;

    const key = input.value.trim();
    if (key) {
        // The API key is a user-supplied credential stored at the user's own request.
        // It is only transmitted to the Google Gemini API (HTTPS) and never to our server.
        localStorage.setItem(CHAT_API_KEY_STORAGE, key); // lgtm[js/clear-text-storage-of-sensitive-data]
    } else {
        localStorage.removeItem(CHAT_API_KEY_STORAGE);
    }

    closeChatSettings();
}

/**
 * チャット履歴をクリアする
 */
function clearChatHistory() {
    chatMessages = [];
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.innerHTML = '';
        appendChatMessage('model', escapeHtml(t('chat-welcome')));
    }
}

// =====================
// 初期化
// =====================

/**
 * チャット機能を初期化する
 * app.js の setupEventListeners() を再利用する
 */
function initializeChat() {
    setupEventListeners([
        { id: 'chatToggleBtn',         event: 'click', handler: toggleChatPanel },
        { id: 'chatCloseBtn',          event: 'click', handler: closeChatPanel },
        { id: 'chatSettingsBtn',       event: 'click', handler: openChatSettings },
        { id: 'chatClearBtn',          event: 'click', handler: clearChatHistory },
        { id: 'chatSendBtn',           event: 'click', handler: submitChatMessage },
        { id: 'chatSettingsSave',      event: 'click', handler: saveChatApiKey },
        { id: 'chatSettingsCancel',    event: 'click', handler: closeChatSettings },
        { id: 'chatSettingsClose',     event: 'click', handler: closeChatSettings },
        { id: 'chatSettingsOverlay',   event: 'click', handler: closeChatSettings },
    ]);

    // Enterキーで送信、Shift+Enterで改行
    const input = document.getElementById('chatInput');
    if (input) {
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                submitChatMessage();
            }
        });
    }

    // ウェルカムメッセージを表示
    appendChatMessage('model', escapeHtml(t('chat-welcome')));
}

document.addEventListener('DOMContentLoaded', function () {
    initializeChat();
});
