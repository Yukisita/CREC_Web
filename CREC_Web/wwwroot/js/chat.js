/*
CREC Web - AI Chat Support
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

Chat requests are handled by the CREC Web server (/api/Chat), which forwards
them to the Python MCP server's process_chat tool.  The MCP server calls the
configured LLM backend and returns a validated AI response.
*/

const CHAT_HISTORY_MAX           = 20;   // Maximum messages to keep in context
const CHAT_SESSION_KEY           = 'crec_chat_history_v2';         // text-only conversation history
const CHAT_PENDING_ACTIONS_KEY   = 'crec_chat_pending_actions_v2'; // structured post-navigation plan
const CHAT_PANEL_STATE_KEY       = 'crec_chat_panel_open_v1';      // sessionStorage key for panel open/close state

// Chat state
let chatMessages = []; // { role: 'user'|'assistant', content: string }
let chatIsOpen = false;
let chatIsSending = false;
let chatRequestController = null;

// =====================
// SessionStorage helpers
// =====================

/** Read a JSON value from sessionStorage. Returns null when key is absent or value is unparseable. */
function sessionGet(key) {
    try {
        // JSON.parse(null) returns null (no throw) when the key is absent,
        // so we don't need a separate null-check before parsing.
        return JSON.parse(sessionStorage.getItem(key));
    } catch (e) { return null; }
}
/** Write a JSON value to sessionStorage. Silently ignores quota/security errors. */
function sessionSet(key, value) {
    try {
        sessionStorage.setItem(key, JSON.stringify(value));
        return true;
    } catch { return false; }
}
/** Remove a key from sessionStorage. Silently ignores errors. */
function sessionDel(key) {
    try { sessionStorage.removeItem(key); } catch (e) {}
}

function saveChatSession() {
    // Keep complete history pairs while one user message is awaiting its reply.
    const pendingUser = chatMessages.at(-1)?.role === 'user' ? 1 : 0;
    chatMessages = chatMessages.slice(-(CHAT_HISTORY_MAX + pendingUser));
    sessionSet(CHAT_SESSION_KEY, chatMessages);
}
function loadChatSession() {
    const history = sessionGet(CHAT_SESSION_KEY);
    return Array.isArray(history) ? history.filter(message => message &&
        ['user', 'assistant'].includes(message.role) && typeof message.content === 'string')
        .slice(-CHAT_HISTORY_MAX) : [];
}
function clearChatSession()         { sessionDel(CHAT_SESSION_KEY); }

function savePendingChatActions(actions, destination) {
    return sessionSet(CHAT_PENDING_ACTIONS_KEY, {
        actions,
        destination: destination.pathname + destination.search,
        expiresAt: Date.now() + 5 * 60 * 1000
    });
}
function loadPendingChatActions()   { return sessionGet(CHAT_PENDING_ACTIONS_KEY); }
function clearPendingChatActions()  { sessionDel(CHAT_PENDING_ACTIONS_KEY); }

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
async function sendChatToServer(userText, signal) {
    const pageContext = getChatPageContext();
    const pageTitle = document.title || 'CREC Web';
    const projectName = (typeof projectSettings !== 'undefined' && projectSettings.projectName)
        ? projectSettings.projectName
        : 'CREC Web';

    // The current user message is sent separately, not as an orphaned history turn.
    const history = chatMessages.slice(0, -1).slice(-CHAT_HISTORY_MAX);

    const requestBody = {
        message: userText,
        history,
        pageContext,
        pageTitle,
        projectName
    };

    try {
        const response = await fetch('/api/Chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(requestBody),
            signal
        });

        if (response.status === 504) return { error: true, message: t('chat-error-timeout') };
        if (!response.ok) return { error: true, message: t('chat-error-server') };
        const data = await response.json();

        if (data.error === 'empty_response') {
            return { error: true, message: t('chat-error-empty-response') };
        }
        if (data.error) {
            return { error: true, message: t('chat-error-server') };
        }

        if (typeof data.text !== 'string') throw new Error('Invalid chat response.');
        validateChatActions(data.actions);
        const warnings = { invalid_actions: 'chat-invalid-actions', deletion_blocked: 'chat-deletion-blocked' };
        if (data.warning && (!Object.hasOwn(warnings, data.warning) || data.actions.length))
            throw new Error('Invalid chat warning.');
        return { error: false, text: data.warning ? t(warnings[data.warning]) : data.text, actions: data.actions };
    } catch (e) {
        if (e instanceof TypeError) {
            // TypeError from fetch usually means a network error ("Failed to fetch")
            return { error: true, message: t('chat-error-network') };
        }
        if (e && (e.name === 'AbortError' || (e.message && e.message.toLowerCase().includes('timeout')))) {
            return { error: true, message: t('chat-error-timeout') };
        }
        return { error: true, message: t('chat-error') };
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
 * Display an action execution failure message in the chat panel.
 * Called when a chat action cannot be executed (element not found, API error, etc.).
 * Execution results are also recorded in history so future replies know whether
 * an operation actually completed.
 * @param {string} reason - Plain-text explanation of why the action failed.
 *   The entire string is HTML-escaped before rendering, so dynamic values
 *   (e.g. collection names or element IDs from the LLM response) are safe to
 *   interpolate directly into `reason` without pre-escaping them.
 */
function reportActionFailure(reason) {
    appendChatMessage(
        'assistant',
        `<span class="text-warning"><i class="bi bi-exclamation-circle-fill me-1"></i>${escapeHtml(reason)}</span>`
    );
    recordChatOperationResult('Failed: ' + reason);
}

function reportActionCompletion() {
    appendChatMessage('assistant', escapeHtml(t('chat-operation-completed')));
    recordChatOperationResult('Completed.');
}

function recordChatOperationResult(result) {
    const last = chatMessages.at(-1);
    if (last?.role === 'assistant') {
        last.content += '\n[Browser operation result] ' + result;
        saveChatSession();
    }
}

/**
 * Append the welcome message bubble.
 * The bubble carries data-lang="chat-welcome" so that updateUILanguage()
 * automatically updates its text when the display language is switched.
 */
function appendChatWelcome() {
    const messages = document.getElementById('chatMessages');
    if (!messages) return;

    const div = document.createElement('div');
    div.className = 'chat-message chat-message-model';

    const bubble = document.createElement('div');
    bubble.className = 'chat-bubble';
    bubble.setAttribute('data-lang', 'chat-welcome');
    bubble.textContent = t('chat-welcome');

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
    const controller = new AbortController();
    chatRequestController = controller;
    const sendBtn = document.getElementById('chatSendBtn');
    if (sendBtn) sendBtn.disabled = true;

    const thinkingId = 'chat-thinking-' + Date.now();
    appendChatMessage(
        'assistant',
        `<span class="chat-thinking"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${escapeHtml(t('chat-thinking'))}</span>`,
        thinkingId
    );

    try {
        const result = await sendChatToServer(userText, controller.signal);
        if (controller.signal.aborted) return;

        const thinkingEl = document.getElementById(thinkingId);
        if (thinkingEl) thinkingEl.remove();

        if (result.error) {
            // Remove ALL trailing user messages so the history ends on an assistant turn.
            // A single pop() is not enough if multiple orphaned user messages accumulated
            // (e.g. due to page navigations during pending requests).  Removing all of them
            // ensures the next request will have a valid alternating history.
            const before = chatMessages.length;
            while (chatMessages.length > 0 && chatMessages[chatMessages.length - 1].role === 'user') {
                chatMessages.pop();
            }
            if (chatMessages.length < before) saveChatSession();
            appendChatMessage(
                'assistant',
                `<span class="text-danger"><i class="bi bi-exclamation-triangle-fill"></i> ${escapeHtml(result.message)}</span>`
            );
        } else {
            const text = result.text || t('chat-actions-planned');
            const renderedHtml = renderChatMarkdown(text);
            appendChatMessage('assistant', renderedHtml);

            chatMessages.push({ role: 'assistant', content: text });
            saveChatSession();
            await executeChatActions(result.actions);
        }
    } finally {
        document.getElementById(thinkingId)?.remove();
        chatRequestController = null;
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
        sessionSet(CHAT_PANEL_STATE_KEY, 1); // stored as JSON number 1 (truthy)
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
        sessionDel(CHAT_PANEL_STATE_KEY);
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
    chatRequestController?.abort();
    cancelChatActions();
    chatMessages = [];
    clearChatSession();
    const messages = document.getElementById('chatMessages');
    if (messages) {
        messages.innerHTML = '';
        appendChatWelcome();
    }
}

// =====================
// Initialization
// =====================

async function initializeChat() {
    setupEventListeners([
        { id: 'chatToggleBtn', event: 'click', handler: toggleChatPanel },
        { id: 'chatCloseBtn',  event: 'click', handler: closeChatPanel },
        { id: 'chatClearBtn',  event: 'click', handler: clearChatHistory },
        { id: 'chatSendBtn',   event: 'click', handler: submitChatMessage },
    ]);

    const input = document.getElementById('chatInput');
    if (input) {
        input.addEventListener('keydown', e => {
            if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
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
                appendChatMessage('assistant', renderChatMarkdown(msg.content));
            }
        });
    } else {
        appendChatWelcome();
    }

    // Restore panel open/close state from before page navigation
    if (sessionGet(CHAT_PANEL_STATE_KEY)) {
        openChatPanel();
    }

    // Prevent Bootstrap modal focus trap from stealing focus when the user
    // interacts with the chat panel while a modal is open.  Bootstrap adds a
    // capture-phase 'focusin' listener on the document that redirects focus
    // back to the modal whenever it detects focus leaving the modal element.
    // By intercepting the event first (capture phase, stopImmediatePropagation)
    // we ensure Bootstrap never sees the event when focus is inside the chat panel.
    const chatPanelEl = document.getElementById('chatPanel');
    if (chatPanelEl) {
        document.addEventListener('focusin', function (e) {
            if (chatPanelEl.contains(e.target)) {
                e.stopImmediatePropagation();
            }
        }, true); // capture = true so this runs before Bootstrap's handler
    }

    // Keep new requests from overtaking operations restored after navigation.
    chatIsSending = true;
    const sendButton = document.getElementById('chatSendBtn');
    if (sendButton) sendButton.disabled = true;
    try {
        await executePendingChatActions();
    } finally {
        chatIsSending = false;
        if (sendButton) sendButton.disabled = false;
    }
}

document.addEventListener('DOMContentLoaded', function () {
    initializeChat();
});
