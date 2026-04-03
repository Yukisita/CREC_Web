/*
CREC Web - AI Chat Support
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Chat requests are handled by the CREC Web server (/api/Chat), which proxies
them to a local Ollama instance. No data is sent to any external service.
Ollama URL and model are configured in appsettings.json on the server.
*/

const CHAT_HISTORY_MAX      = 20;   // コンテキストに保持する最大メッセージ数
const CHAT_PAGE_CONTEXT_MAX = 2000; // RAGに含めるページコンテンツの最大文字数

// チャット状態
let chatMessages = []; // { role: 'user'|'assistant', content: string }
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
 * サーバー側の /api/Chat エンドポイントを通じてメッセージを送信する
 * サーバーはリクエストをローカルの Ollama インスタンスに転送する
 * @param {string} userText - ユーザーのメッセージ
 * @returns {Promise<{error: boolean, text?: string, message?: string}>}
 */
async function sendChatToServer(userText) {
    const pageContext = getChatPageContext();
    const pageTitle = document.title || 'CREC Web';
    const lang = currentLanguage || 'ja';
    const projectName = (typeof projectSettings !== 'undefined' && projectSettings.projectName)
        ? projectSettings.projectName
        : 'CREC Web';

    // 会話履歴を構築（最新 CHAT_HISTORY_MAX 件のみ）
    const history = chatMessages.slice(-CHAT_HISTORY_MAX);

    const requestBody = {
        message: userText,
        history,
        pageContext,
        pageTitle,
        lang,
        projectName
    };

    try {
        const response = await fetch('/api/Chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(requestBody)
        });

        const data = await response.json();

        if (!response.ok || data.error) {
            return { error: true, message: data.error || `HTTP ${response.status}` };
        }

        if (!data.text) {
            return { error: true, message: t('chat-error-empty-response') };
        }

        return { error: false, text: data.text };
    } catch (e) {
        return { error: true, message: e.message || t('chat-error') };
    }
}

// =====================
// UI 関数
// =====================

/**
 * チャットメッセージリストの末尾にメッセージを追加する
 * @param {'user'|'assistant'} role - メッセージのロール
 * @param {string} htmlContent - 表示するHTMLコンテンツ（信頼済み）
 * @param {string} [elementId] - 後から参照するためのID（省略可）
 */
function appendChatMessage(role, htmlContent, elementId) {
    const messages = document.getElementById('chatMessages');
    if (!messages) return;

    const div = document.createElement('div');
    // 'user' はそのまま、'assistant' は 'model' クラスとしてスタイルを適用
    div.className = `chat-message chat-message-${role === 'user' ? 'user' : 'model'}`;
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
    chatMessages.push({ role: 'user', content: userText });

    // 送信中フラグ
    chatIsSending = true;
    const sendBtn = document.getElementById('chatSendBtn');
    if (sendBtn) sendBtn.disabled = true;

    // 思考中インジケーターを表示
    const thinkingId = 'chat-thinking-' + Date.now();
    appendChatMessage(
        'assistant',
        `<span class="chat-thinking"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${escapeHtml(t('chat-thinking'))}</span>`,
        thinkingId
    );

    try {
        const result = await sendChatToServer(userText);

        // 思考中インジケーターを削除
        const thinkingEl = document.getElementById(thinkingId);
        if (thinkingEl) thinkingEl.remove();

        if (result.error) {
            appendChatMessage(
                'assistant',
                `<span class="text-danger"><i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(result.message)}</span>`
            );
        } else {
            // アクションを処理してテキストを整形
            const cleanText = processChatActions(result.text);
            const renderedHtml = renderChatMarkdown(cleanText);
            appendChatMessage('assistant', renderedHtml);

            // 履歴に保存（元テキスト、アクションタグも含む）
            chatMessages.push({ role: 'assistant', content: result.text });
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

/**
 * チャット履歴をクリアする
 */
function clearChatHistory() {
    chatMessages = [];
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.innerHTML = '';
        appendChatMessage('assistant', escapeHtml(t('chat-welcome')));
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
        { id: 'chatToggleBtn', event: 'click', handler: toggleChatPanel },
        { id: 'chatCloseBtn',  event: 'click', handler: closeChatPanel },
        { id: 'chatClearBtn',  event: 'click', handler: clearChatHistory },
        { id: 'chatSendBtn',   event: 'click', handler: submitChatMessage },
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
    appendChatMessage('assistant', escapeHtml(t('chat-welcome')));
}

document.addEventListener('DOMContentLoaded', function () {
    initializeChat();
});
