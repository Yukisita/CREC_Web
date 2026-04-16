/*
CREC Web - AI Chat Support
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Chat requests are handled by the CREC Web server (/api/Chat), which forwards
them to the Python MCP server's process_chat tool.  The MCP server calls the
configured LLM backend and returns a validated AI response.  No data is sent
to any external service.
*/

const CHAT_HISTORY_MAX           = 20;   // Maximum messages to keep in context
const CHAT_PAGE_CONTEXT_MAX      = 2000; // Maximum characters of page content for RAG
const CHAT_SESSION_KEY           = 'crec_chat_history_v1'; // sessionStorage key
const CHAT_ACTION_INITIAL_DELAY  = 400;  // ms to wait before executing the first action
const CHAT_ACTION_INTERVAL       = 600;  // ms gap between consecutive actions

// Chat state
let chatMessages = []; // { role: 'user'|'assistant', content: string }
let chatIsOpen = false;
let chatIsSending = false;

/**
 * Save conversation history to sessionStorage (survives page navigation).
 */
function saveChatSession() {
    try {
        sessionStorage.setItem(CHAT_SESSION_KEY, JSON.stringify(chatMessages));
    } catch (e) {
        // sessionStorage unavailable – ignore
    }
}

/**
 * Load conversation history from sessionStorage.
 * @returns {Array|null}
 */
function loadChatSession() {
    try {
        const saved = sessionStorage.getItem(CHAT_SESSION_KEY);
        return saved ? JSON.parse(saved) : null;
    } catch (e) {
        return null;
    }
}

/**
 * Remove conversation history from sessionStorage.
 */
function clearChatSession() {
    try {
        sessionStorage.removeItem(CHAT_SESSION_KEY);
    } catch (e) {}
}

/**
 * Strip <action>…</action> tags from text for display purposes.
 * (Used when restoring messages from sessionStorage.)
 * @param {string} text
 * @returns {string}
 */
function stripChatActions(text) {
    return text.replace(/<action>[\s\S]*?<\/action>/g, '').trim();
}

/**
 * Get the current page's visible text for RAG context.
 * Includes a compact structured list of visible collections (from data attributes),
 * then appends remaining visible text while staying within the character limit.
 * @returns {string}
 */
function getChatPageContext() {
    // Build a compact structured list from elements that carry data-collection-id
    let structuredContext = '';
    const collectionEls = document.querySelectorAll('[data-collection-id]:not([data-collection-id=""])');
    if (collectionEls.length > 0) {
        const items = Array.from(collectionEls).map(el =>
            JSON.stringify({ name: el.dataset.collectionName || '', id: el.dataset.collectionId })
        );
        structuredContext = `[visible collections (${items.length})]\n` + items.join('\n') + '\n\n';
    }

    const main = document.querySelector('main');
    if (!main) return structuredContext.substring(0, CHAT_PAGE_CONTEXT_MAX);

    const clone = main.cloneNode(true);

    // Drop table rows (potentially huge amount of data)
    clone.querySelectorAll('tbody').forEach(tbody => {
        tbody.innerHTML = '';
    });

    // Drop the chat panel itself to avoid recursive context
    const chatPanel = clone.querySelector('#chatPanel');
    if (chatPanel) chatPanel.remove();

    const text = stripHtmlToText(clone.innerHTML);
    return (structuredContext + text).substring(0, CHAT_PAGE_CONTEXT_MAX);
}

/**
 * Parse and execute <action>…</action> tags from an AI response.
 * Multiple actions are executed sequentially with CHAT_ACTION_INTERVAL gaps
 * so that modals have time to open before inputs are filled.
 * Returns the response text with all action tags removed.
 * @param {string} text - Full AI response text
 * @returns {string} Cleaned text without action tags
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

    const cleanText = text.replace(/<action>[\s\S]*?<\/action>/g, '').trim();

    if (actions.length > 0) {
        let cumulativeDelay = CHAT_ACTION_INITIAL_DELAY;
        actions.forEach(cmd => {
            const delay = cumulativeDelay;
            cumulativeDelay += CHAT_ACTION_INTERVAL;
            setTimeout(() => {
                try {
                    executeChatAction(cmd);
                } catch (e) {
                    console.warn('Failed to execute chat action:', cmd, e);
                }
            }, delay);
        });
    }

    return cleanText;
}

/**
 * Execute a single parsed chat action command.
 * @param {object} cmd - Parsed action object
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

        case 'showCollectionPanel':
            // Show the collection detail panel on the current page when available
            // (home page), otherwise fall back to opening in a new window.
            if (cmd.id && typeof cmd.id === 'string') {
                if (typeof window.showCollectionDetails === 'function') {
                    window.showCollectionDetails(cmd.id);
                } else {
                    openCollectionWindow(cmd.id);
                }
            }
            break;

        case 'showAdminPanel':
            openAdminPanel();
            break;

        case 'navigate':
            // Only allow same-origin absolute paths (block external and protocol-relative URLs)
            if (typeof cmd.path === 'string' &&
                cmd.path.startsWith('/') &&
                !cmd.path.startsWith('//')) {
                const targetPathname = new URL(cmd.path, window.location.origin).pathname;
                if (window.location.pathname !== targetPathname) {
                    window.location.href = cmd.path;
                }
            }
            break;

        case 'clickButton':
            // IDs have already been validated server-side by the MCP server
            if (typeof cmd.id === 'string') {
                const el = document.getElementById(cmd.id);
                if (el) el.click();
                else console.warn('clickButton: element not found:', cmd.id);
            }
            break;

        case 'fillInput':
            // IDs have already been validated server-side by the MCP server
            if (typeof cmd.id === 'string' &&
                (typeof cmd.value === 'string' || typeof cmd.value === 'number')) {
                const el = document.getElementById(cmd.id);
                if (el) {
                    el.value = String(cmd.value);
                    el.dispatchEvent(new Event('input',  { bubbles: true }));
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                } else {
                    console.warn('fillInput: element not found:', cmd.id);
                }
            }
            break;

        default:
            console.warn('Unknown chat action type:', cmd.type);
    }
}

/**
 * Convert AI response text to safe HTML.
 * HTML-escapes first (XSS prevention), then applies minimal Markdown.
 * @param {string} text - Cleaned AI response (action tags already removed)
 * @returns {string} Safe HTML string
 */
function renderChatMarkdown(text) {
    let html = escapeHtml(text);

    // Basic Markdown (safe because HTML was escaped first)
    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/\*(.+?)\*/g, '<em>$1</em>');
    html = html.replace(/`(.+?)`/g, '<code>$1</code>');
    html = html.replace(/\n/g, '<br>');

    return html;
}

/**
 * Send a chat message to the C# backend (/api/Chat), which forwards it to
 * the Python MCP server's process_chat tool.
 * @param {string} userText - User's message
 * @returns {Promise<{error: boolean, text?: string, message?: string}>}
 */
async function sendChatToServer(userText) {
    const pageContext = getChatPageContext();
    const pageTitle = document.title || 'CREC Web';
    const lang = currentLanguage || 'ja';
    const projectName = (typeof projectSettings !== 'undefined' && projectSettings.projectName)
        ? projectSettings.projectName
        : 'CREC Web';

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
// UI helpers
// =====================

/**
 * Append a message bubble to the chat messages list.
 * @param {'user'|'assistant'} role
 * @param {string} htmlContent - Trusted HTML content to display
 * @param {string} [elementId] - Optional element ID for later reference
 */
function appendChatMessage(role, htmlContent, elementId) {
    const messages = document.getElementById('chatMessages');
    if (!messages) return;

    const div = document.createElement('div');
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
 * Scroll the messages list to the bottom.
 */
function scrollChatToBottom() {
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.scrollTop = messages.scrollHeight;
    }
}

/**
 * Send user message and display AI response.
 */
async function submitChatMessage() {
    if (chatIsSending) return;

    const input = document.getElementById('chatInput');
    if (!input) return;

    const userText = input.value.trim();
    if (!userText) return;

    input.value = '';

    appendChatMessage('user', escapeHtml(userText));

    chatMessages.push({ role: 'user', content: userText });
    saveChatSession();

    chatIsSending = true;
    const sendBtn = document.getElementById('chatSendBtn');
    if (sendBtn) sendBtn.disabled = true;

    const thinkingId = 'chat-thinking-' + Date.now();
    appendChatMessage(
        'assistant',
        `<span class="chat-thinking"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${escapeHtml(t('chat-thinking'))}</span>`,
        thinkingId
    );

    try {
        const result = await sendChatToServer(userText);

        const thinkingEl = document.getElementById(thinkingId);
        if (thinkingEl) thinkingEl.remove();

        if (result.error) {
            appendChatMessage(
                'assistant',
                `<span class="text-danger"><i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(result.message)}</span>`
            );
        } else {
            const cleanText = processChatActions(result.text);
            const renderedHtml = renderChatMarkdown(cleanText);
            appendChatMessage('assistant', renderedHtml);

            chatMessages.push({ role: 'assistant', content: result.text });
            saveChatSession();
        }
    } finally {
        chatIsSending = false;
        if (sendBtn) sendBtn.disabled = false;
        if (input) input.focus();
    }
}

// =====================
// Panel open/close
// =====================

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

function closeChatPanel() {
    const panel = document.getElementById('chatPanel');
    if (panel) {
        panel.classList.remove('open');
        chatIsOpen = false;
    }
}

function toggleChatPanel() {
    if (chatIsOpen) {
        closeChatPanel();
    } else {
        openChatPanel();
    }
}

function clearChatHistory() {
    chatMessages = [];
    clearChatSession();
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.innerHTML = '';
        appendChatMessage('assistant', escapeHtml(t('chat-welcome')));
    }
}

// =====================
// Initialization
// =====================

function initializeChat() {
    setupEventListeners([
        { id: 'chatToggleBtn', event: 'click', handler: toggleChatPanel },
        { id: 'chatCloseBtn',  event: 'click', handler: closeChatPanel },
        { id: 'chatClearBtn',  event: 'click', handler: clearChatHistory },
        { id: 'chatSendBtn',   event: 'click', handler: submitChatMessage },
    ]);

    const input = document.getElementById('chatInput');
    if (input) {
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                submitChatMessage();
            }
        });
    }

    // Restore conversation history from sessionStorage (continues across page navigation)
    const savedHistory = loadChatSession();
    if (savedHistory && savedHistory.length > 0) {
        chatMessages = savedHistory;
        savedHistory.forEach(msg => {
            if (msg.role === 'user') {
                appendChatMessage('user', escapeHtml(msg.content));
            } else if (msg.role === 'assistant') {
                const clean = stripChatActions(msg.content);
                appendChatMessage('assistant', renderChatMarkdown(clean));
            }
        });
    } else {
        appendChatMessage('assistant', escapeHtml(t('chat-welcome')));
    }
}

document.addEventListener('DOMContentLoaded', function () {
    initializeChat();
});
