/*
CREC Web - AI Chat Support
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.

The CREC Web server (/api/Chat) calls the configured LLM backend directly
and returns display text with a validated browser operation plan.
*/

const CHAT_PAGE_CONTEXT_MAX = 4000;

// Maximum length of button label and input hint strings sent to the AI
const CHAT_ELEMENT_LABEL_MAX     = 40;
// Maximum number of select options to include per select element in AI context
const CHAT_SELECT_OPTIONS_MAX    = 10;
// Maximum length of a [page data] value string sent to the AI
const CHAT_DATA_VALUE_MAX        = 80;

// CSS selector for form inputs to include in the AI context (excludes hidden/button/checkbox/radio)
const CHAT_INPUT_ELEMENT_SELECTOR = 'input[id]:not([type="hidden"]):not([type="button"]):not([type="submit"]):not([type="checkbox"]):not([type="radio"]), select[id], textarea[id]';

// Button IDs that have dedicated high-level action types and should NOT appear in the
// [page buttons] context.  Exposing them would cause the AI to try clickButton instead
// of the purpose-built action, which may open a new browser window or behave differently.
const CHAT_EXCLUDED_BUTTON_IDS = new Set([
    'addNewCollectionBtn',   // use createNewCollection action instead
    'editProjectBtn',        // use navigate /ProjectEdit action instead
    'overviewPanelClose',    // close button – AI should not close the overview panel
    'openCollectionWindowBtn', // opens new window; use navigate or navigateToCollectionByName instead
    'confirmProjectSwitchBtn', // project selection and confirmation are performed in the picker
]);

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
    if (el.disabled || el.readOnly || el.getAttribute('aria-disabled') === 'true') return false;
    if (el.closest('[hidden], .d-none')) return false;
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
    const project = {
        selected: ProjectSession.hasProject,
        stale: ProjectSession.isStale(),
        name: typeof projectSettings !== 'undefined' ? projectSettings.projectName : ''
    };
    let structuredContext = '[current project]\n' + JSON.stringify(project) + '\n\n';

    // --- Current collection page (Collection/Index.cshtml exposes this after async load) ---
    if (window.currentPageCollection && window.currentPageCollection.id) {
        const col = window.currentPageCollection;
        const colData = {
            id: col.id,
            name: col.name || '',
            url: col.url || `/Collection/${encodeURIComponent(col.id)}`
        };
        if (col.inventory !== undefined) colData.inventory = col.inventory;
        if (col.managementCode) colData.managementCode = col.managementCode;
        if (col.category) colData.category = col.category;
        if (col.location) colData.location = col.location;
        structuredContext += `[current collection]\n` + JSON.stringify(colData) + '\n\n';
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
        const tag = el.tagName.toLowerCase();
        const info = { id: el.id, type: tag === 'input' ? (el.type || 'text') : tag };
        // Prefer <label for="id"> text, then placeholder/aria-label
        const labelEl = el.id ? document.querySelector(`label[for="${CSS.escape(el.id)}"]`) : null;
        const hint = (labelEl ? labelEl.textContent : (el.getAttribute('placeholder') || el.getAttribute('aria-label') || ''))
            .trim().replace(/\s+/g, ' ').substring(0, CHAT_ELEMENT_LABEL_MAX);
        if (hint) info.hint = hint;
        // For <select>, include available options so the AI knows valid values
        if (tag === 'select') {
            const opts = Array.from(el.options)
                .map(o => `${o.value}:${o.text.trim()}`)
                .slice(0, CHAT_SELECT_OPTIONS_MAX)
                .join(', ');
            if (opts) info.options = opts;
        }
        inputItems.push(JSON.stringify(info));
    });
    if (inputItems.length > 0) {
        structuredContext += `[page inputs (${inputItems.length})]\n` + inputItems.join('\n') + '\n\n';
    }

    // --- Page data (elements tagged with data-chat-label expose displayed values to the AI) ---
    const dataItems = [];
    document.querySelectorAll('[data-chat-label]').forEach(el => {
        if (el.closest('#chatPanel')) return;
        if (!isChatContextElement(el)) return;
        const label = el.getAttribute('data-chat-label');
        if (!label) return;
        // data-chat-value attribute takes priority; otherwise use element text content
        const value = el.hasAttribute('data-chat-value')
            ? el.getAttribute('data-chat-value')
            : el.textContent.trim().replace(/\s+/g, ' ').substring(0, CHAT_DATA_VALUE_MAX);
        dataItems.push(JSON.stringify({ label, value }));
    });
    if (dataItems.length > 0) {
        structuredContext += `[page data (${dataItems.length})]\n` + dataItems.join('\n') + '\n\n';
    }

    // Controls precede the collection list so large result sets cannot truncate form IDs.
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
