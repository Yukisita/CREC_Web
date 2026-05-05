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
const CHAT_PAGE_CONTEXT_MAX      = 4000; // Maximum characters of page content
const CHAT_SESSION_KEY           = 'crec_chat_history_v1';         // sessionStorage key
const CHAT_PENDING_ACTIONS_KEY   = 'crec_chat_pending_actions_v1'; // sessionStorage key for post-nav actions
const CHAT_PANEL_STATE_KEY       = 'crec_chat_panel_open_v1';      // sessionStorage key for panel open/close state
const CHAT_ACTION_INITIAL_DELAY  = 400;  // ms to wait before executing the first action
const CHAT_ACTION_INTERVAL       = 600;  // ms gap between consecutive actions

// Maximum length of button label and input hint strings sent to the AI
const CHAT_ELEMENT_LABEL_MAX     = 40;

// CSS selector for form inputs to include in the AI context (excludes hidden/button/checkbox/radio)
const CHAT_INPUT_ELEMENT_SELECTOR = 'input[id]:not([type="hidden"]):not([type="button"]):not([type="submit"]):not([type="checkbox"]):not([type="radio"]), select[id], textarea[id]';

// Button IDs that have dedicated high-level action types and should NOT appear in the
// [page buttons] context.  Exposing them would cause the AI to try clickButton instead
// of the purpose-built action, which may open a new browser window or behave differently.
const CHAT_EXCLUDED_BUTTON_IDS = new Set([
    'addNewCollectionBtn', // use createNewCollection action instead
    'editProjectBtn',      // use navigate /ProjectEdit action instead
]);

// Action types that trigger a full page navigation (used to split action sequences)
const CHAT_NAV_ACTION_TYPES = new Set(['navigate', 'createNewCollection']);

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
 * Save pending post-navigation actions to sessionStorage.
 * These are executed when the next page finishes loading.
 * @param {Array} actions
 */
function savePendingChatActions(actions) {
    try {
        sessionStorage.setItem(CHAT_PENDING_ACTIONS_KEY, JSON.stringify(actions));
    } catch (e) {}
}

/**
 * Load pending post-navigation actions from sessionStorage.
 * @returns {Array|null}
 */
function loadPendingChatActions() {
    try {
        const saved = sessionStorage.getItem(CHAT_PENDING_ACTIONS_KEY);
        return saved ? JSON.parse(saved) : null;
    } catch (e) {
        return null;
    }
}

/**
 * Remove pending post-navigation actions from sessionStorage.
 */
function clearPendingChatActions() {
    try {
        sessionStorage.removeItem(CHAT_PENDING_ACTIONS_KEY);
    } catch (e) {}
}

/**
 * Execute pending post-navigation actions stored in sessionStorage.
 * Called on page load and when a navigate action targets the current page.
 */
function executePendingChatActions() {
    const pending = loadPendingChatActions();
    if (!pending || pending.length === 0) return;
    clearPendingChatActions();
    let delay = CHAT_ACTION_INITIAL_DELAY;
    pending.forEach(cmd => {
        const d = delay;
        delay += CHAT_ACTION_INTERVAL;
        setTimeout(() => {
            try {
                executeChatAction(cmd);
            } catch (e) {
                console.warn('Failed to execute pending chat action:', cmd, e);
            }
        }, d);
    });
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
 * Returns true if the element should be included in the AI page context.
 * Excludes elements that are inside closed/hidden sliding panels or hidden modals,
 * so the AI only sees elements that are currently reachable by the user.
 *
 * Covered cases:
 *   1. Sliding panels (.admin-panel, .detail-panel) – toggled via .open class.
 *   2. Custom overlay modals (.qr-scanner-modal) – toggled via .show class.
 *   3. Bootstrap modals (.modal.fade) – toggled via .show class.
 *
 * @param {Element} el
 * @returns {boolean}
 */
function isChatContextElement(el) {
    // Sliding panels: visible only when the .open class is present
    if (el.closest('.admin-panel:not(.open)')) return false;
    if (el.closest('.detail-panel:not(.open)')) return false;

    // Custom overlay modals (qr-scanner-modal type): visible only when .show is present
    if (el.closest('.qr-scanner-modal:not(.show)')) return false;

    // Bootstrap modals: visible only when .show is present
    if (el.closest('.modal.fade:not(.show)')) return false;

    return true;
}

/**
 * Get the current page's context for the AI.
 * Includes:
 *   - Structured list of visible collections (from data attributes)
 *   - Interactive buttons and inputs currently on the page (dynamic UI element discovery)
 *   - Remaining visible text
 * The button/input sections allow the AI to use the correct IDs without any
 * hardcoded ID lists in the system prompt — new UI elements are automatically
 * discovered without needing prompt updates.
 * @returns {string}
 */
function getChatPageContext() {
    let structuredContext = '';

    // --- Visible collections ---
    const collectionEls = document.querySelectorAll('[data-collection-id]:not([data-collection-id=""])');
    if (collectionEls.length > 0) {
        const items = Array.from(collectionEls)
            .filter(el => el.dataset.collectionId)
            .map(el => {
                const name = el.dataset.collectionName || '';
                const id = el.dataset.collectionId;
                return JSON.stringify({ name, id, url: `/Collection/${encodeURIComponent(id)}` });
            });
        structuredContext += `[visible collections (${items.length})]\n` + items.join('\n') + '\n\n';
    }

    // --- Page buttons (clickButton action targets) ---
    const buttonItems = [];
    document.querySelectorAll('button[id], input[type="button"][id], input[type="submit"][id]').forEach(el => {
        if (el.closest('#chatPanel')) return; // exclude chat panel
        if (!isChatContextElement(el)) return; // exclude elements in closed/hidden panels
        if (CHAT_EXCLUDED_BUTTON_IDS.has(el.id)) return; // exclude buttons with dedicated action types
        const label = (el.textContent || el.value || el.getAttribute('aria-label') || '').trim().replace(/\s+/g, ' ').substring(0, CHAT_ELEMENT_LABEL_MAX);
        buttonItems.push(JSON.stringify({ id: el.id, label }));
    });
    if (buttonItems.length > 0) {
        structuredContext += `[page buttons (${buttonItems.length})]\n` + buttonItems.join('\n') + '\n\n';
    }

    // --- Page inputs (fillInput action targets) ---
    const inputItems = [];
    document.querySelectorAll(CHAT_INPUT_ELEMENT_SELECTOR).forEach(el => {
        if (el.closest('#chatPanel')) return; // exclude chat panel
        if (!isChatContextElement(el)) return; // exclude elements in closed/hidden panels
        const info = { id: el.id, type: el.tagName.toLowerCase() === 'input' ? (el.type || 'text') : el.tagName.toLowerCase() };
        const hint = (el.getAttribute('placeholder') || el.getAttribute('aria-label') || '').trim().substring(0, CHAT_ELEMENT_LABEL_MAX);
        if (hint) info.hint = hint;
        inputItems.push(JSON.stringify(info));
    });
    if (inputItems.length > 0) {
        structuredContext += `[page inputs (${inputItems.length})]\n` + inputItems.join('\n') + '\n\n';
    }

    // --- Remaining visible page text ---
    const main = document.querySelector('main');
    if (!main) return structuredContext.substring(0, CHAT_PAGE_CONTEXT_MAX);

    const clone = main.cloneNode(true);
    clone.querySelectorAll('tbody').forEach(tbody => { tbody.innerHTML = ''; });
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
        let cmd = null;
        const raw = match[1];
        try {
            cmd = JSON.parse(raw);
        } catch (e) {
            // Fallback 1: trim whitespace and retry
            try {
                cmd = JSON.parse(raw.trim());
            } catch (e2) {
            // Fallback 2: strip trailing extra closing braces and retry.
            // This only runs when JSON.parse already failed, so the input is
            // already invalid JSON — we are recovering from a single common
            // malformation (e.g. {"type":"x"}} with a doubled closing brace).
            try {
                cmd = JSON.parse(raw.trim().replace(/}+$/, '}'));
            } catch (e3) {
                console.warn('Failed to parse chat action JSON:', raw, e3);
            }
            }
        }
        if (cmd && typeof cmd.type === 'string') {
            actions.push(cmd);
        }
    }

    // Remove action tags
    let cleanText = text.replace(/<action>[\s\S]*?<\/action>/g, '');

    // Strip triple-backtick code fences that are now empty (only whitespace between the fences)
    cleanText = cleanText.replace(/```[^\n]*\n\s*```/g, '');

    // Strip empty backtick pairs left behind by inline-wrapped action tags (e.g. `<action>…</action>`)
    // Only matches pairs where the content is purely whitespace — legitimate code spans are preserved.
    cleanText = cleanText.replace(/`\s*`/g, '');

    // Strip lines that consist of nothing but backticks and whitespace
    // (left behind when the LLM wraps a single action tag in a backtick pair on its own line)
    cleanText = cleanText.replace(/^[ \t]*`+[ \t]*$/gm, '');

    // Strip lines that consist of only JSON-like debris (standalone braces/brackets)
    // left behind when the LLM places JSON punctuation outside <action> tags.
    cleanText = cleanText.replace(/^[ \t]*[\{\}\[\]]+[ \t]*$/gm, '');

    // Collapse three or more consecutive newlines into at most two (one blank line)
    cleanText = cleanText.replace(/\n{3,}/g, '\n\n');

    cleanText = cleanText.trim();

    if (actions.length > 0) {
        // Navigation actions cause a full page reload, so any actions that follow
        // them must be persisted to sessionStorage and executed on the new page.
        const navIdx = actions.findIndex(a => CHAT_NAV_ACTION_TYPES.has(a.type));
        const priorActions = navIdx >= 0 ? actions.slice(0, navIdx + 1) : actions;
        const afterActions  = navIdx >= 0 ? actions.slice(navIdx + 1) : [];

        if (afterActions.length > 0) {
            savePendingChatActions(afterActions);
        }

        let cumulativeDelay = CHAT_ACTION_INITIAL_DELAY;
        priorActions.forEach(cmd => {
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
            // Show the collection overview panel on the current page when available
            // (home page), otherwise navigate to the collection page.
            if (cmd.id && typeof cmd.id === 'string') {
                if (typeof window.showCollectionOverview === 'function') {
                    window.showCollectionOverview(cmd.id);
                } else {
                    openCollectionWindow(cmd.id);
                }
            }
            break;

        case 'openCollectionByName': {
            // Open a collection by its display name — looks up the ID from the DOM.
            // This is the preferred action for opening collections because the AI only
            // needs the name visible on screen, not an internal ID.
            const targetName = typeof cmd.name === 'string' ? cmd.name.trim() : '';
            if (!targetName) break;

            let collectionId = null;
            const allCollectionEls = document.querySelectorAll('[data-collection-id]:not([data-collection-id=""])');
            const lowerTarget = targetName.toLowerCase();

            // 1. Exact match
            for (const el of allCollectionEls) {
                if (el.dataset.collectionName === targetName) {
                    collectionId = el.dataset.collectionId;
                    break;
                }
            }
            // 2. Case-insensitive exact match
            if (!collectionId) {
                for (const el of allCollectionEls) {
                    if ((el.dataset.collectionName || '').toLowerCase() === lowerTarget) {
                        collectionId = el.dataset.collectionId;
                        break;
                    }
                }
            }
            // 3. Partial (contains) match
            if (!collectionId) {
                for (const el of allCollectionEls) {
                    if ((el.dataset.collectionName || '').toLowerCase().includes(lowerTarget)) {
                        collectionId = el.dataset.collectionId;
                        break;
                    }
                }
            }

            if (collectionId) {
                if (typeof window.showCollectionOverview === 'function') {
                    window.showCollectionOverview(collectionId);
                } else {
                    openCollectionWindow(collectionId);
                }
            } else {
                console.warn('openCollectionByName: no collection found for name:', targetName);
            }
            break;
        }

        case 'showAdminPanel':
            openAdminPanel();
            break;

        case 'createNewCollection':
            // Create a new collection via the API and navigate to its detail page
            // with ?edit=1 so that the edit modal opens automatically.
            (async () => {
                try {
                    const response = await fetch('/api/collections', {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' }
                    });
                    if (response.ok) {
                        const result = await response.json();
                        if (result && result.id) {
                            window.location.href = `/Collection/${encodeURIComponent(result.id)}?edit=1`;
                        } else {
                            clearPendingChatActions();
                            console.warn('createNewCollection: missing id in API response', result);
                        }
                    } else {
                        clearPendingChatActions();
                        console.warn('createNewCollection: API returned', response.status);
                    }
                } catch (e) {
                    clearPendingChatActions();
                    console.warn('createNewCollection action failed:', e);
                }
            })();
            break;

        case 'navigate':
            // Only allow same-origin absolute paths (block external and protocol-relative URLs)
            if (typeof cmd.path === 'string' &&
                cmd.path.startsWith('/') &&
                !cmd.path.startsWith('//')) {
                const targetPathname = new URL(cmd.path, window.location.origin).pathname;
                if (window.location.pathname !== targetPathname) {
                    window.location.href = cmd.path;
                } else {
                    // Already on the target page — run any queued post-navigation actions now
                    executePendingChatActions();
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

        case 'switchLanguage':
            // Switch the display language via the existing selectLanguage() in app.js
            if (typeof cmd.lang === 'string' && ['ja', 'en', 'de'].includes(cmd.lang)) {
                if (typeof selectLanguage === 'function') {
                    selectLanguage(cmd.lang);
                } else {
                    console.warn('switchLanguage: selectLanguage() not available');
                }
            } else {
                console.warn('switchLanguage: unsupported or missing lang:', cmd.lang);
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
            // Remove the user message that was saved before the failed request so that
            // the next request does not send two consecutive user messages (which causes
            // the LLM API to reject the request with HTTP 400 Bad Request).
            if (chatMessages.length > 0 && chatMessages[chatMessages.length - 1].role === 'user') {
                chatMessages.pop();
                saveChatSession();
            }
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
        try { sessionStorage.setItem(CHAT_PANEL_STATE_KEY, '1'); } catch (e) {}
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
        try { sessionStorage.removeItem(CHAT_PANEL_STATE_KEY); } catch (e) {}
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
        appendChatWelcome();
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
        appendChatWelcome();
    }

    // Execute any post-navigation actions that were queued on the previous page
    executePendingChatActions();

    // Restore panel open/close state from before page navigation
    try {
        if (sessionStorage.getItem(CHAT_PANEL_STATE_KEY) === '1') {
            openChatPanel();
        }
    } catch (e) {}

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
}

document.addEventListener('DOMContentLoaded', function () {
    initializeChat();
});
