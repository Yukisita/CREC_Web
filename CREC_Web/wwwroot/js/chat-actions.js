/*
CREC Web - AI Chat Actions
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

const CHAT_ACTION_FIELDS = {
    search: ['text'], showCollectionPanel: ['id'], openCollectionByName: ['name'],
    navigateToCollectionByName: ['name'], showAdminPanel: [], createNewCollection: [],
    navigateHome: [], navigate: ['path'], clickButton: ['id'], fillInput: ['id', 'value'],
    switchLanguage: ['lang']
};

/** Validate the full plan before executing its first action, including restored plans. */
function validateChatActions(actions) {
    if (!Array.isArray(actions) || actions.length > 32) throw new Error(t('chat-invalid-actions'));
    for (const action of actions) {
        if (!action || typeof action !== 'object' || Array.isArray(action) || typeof action.type !== 'string')
            throw new Error(t('chat-invalid-actions'));
        const fields = action && Object.hasOwn(CHAT_ACTION_FIELDS, action.type) ? CHAT_ACTION_FIELDS[action.type] : null;
        if (!fields || Object.keys(action).length !== fields.length + 1 ||
            fields.some(field => !Object.hasOwn(action, field))) throw new Error(t('chat-invalid-actions'));
        for (const field of fields) {
            if (field === 'value' && typeof action.value === 'number' &&
                Number.isFinite(action.value) && Math.abs(action.value) <= Number.MAX_SAFE_INTEGER) continue;
            if (typeof action[field] !== 'string' || (!['text', 'value'].includes(field) && !action[field].trim()))
                throw new Error(t('chat-invalid-actions'));
        }
        if (action.type === 'navigate') chatNavigationUrl(action.path);
        if (action.type === 'switchLanguage' && !['ja', 'en', 'de'].includes(action.lang))
            throw new Error(t('chat-invalid-actions'));
        if (action.type === 'clickButton' && action.id === 'deleteCollectionBtn')
            throw new Error(t('chat-deletion-blocked'));
    }
}

let chatActionGeneration = 0;

function cancelChatActions() {
    chatActionGeneration++;
    clearPendingChatActions();
}

/** The chat UI stays busy until this sequence has finished or navigated. */
async function executeChatActions(actions) {
    const generation = chatActionGeneration;
    try {
        validateChatActions(actions);
        await window.crecAppReady;
        await window.crecPageReady;
        for (let index = 0; index < actions.length; index++) {
            // Allow Bootstrap transitions to finish; network operations are awaited.
            await new Promise(resolve => setTimeout(resolve, 400));
            if (generation !== chatActionGeneration) return;
            const destination = await executeChatAction(actions[index]);
            if (generation !== chatActionGeneration) return;
            if (destination) {
                const url = chatNavigationUrl(destination);
                const remaining = actions.slice(index + 1);
                if (chatPageWillReload(url)) {
                    if (remaining.length && !savePendingChatActions(remaining, url))
                        throw new Error('Unable to save the remaining actions for page navigation.');
                    navigateChatPage(destination);
                    return;
                }
                navigateChatPage(destination);
            }
        }
        if (actions.length && generation === chatActionGeneration) reportActionCompletion();
    } catch (error) {
        clearPendingChatActions();
        if (generation === chatActionGeneration) reportActionFailure(error.message);
    }
}

function chatNavigationUrl(path) {
    if (typeof path !== 'string' || !path.startsWith('/') || path.startsWith('//') ||
        /[\\\u0000-\u001f\u007f]/.test(path)) {
        throw new Error('Only paths within CREC Web can be opened.');
    }
    const url = new URL(path, window.location.href);
    if (url.origin !== window.location.origin) throw new Error('Only paths within CREC Web can be opened.');
    return url;
}

function chatPageWillReload(url) {
    const current = new URL(window.location.href);
    return url.pathname !== current.pathname || url.search !== current.search;
}

function navigateChatPage(path) {
    const url = chatNavigationUrl(path);
    if (url.href === window.location.href) return false;
    const reloads = chatPageWillReload(url);
    window.location.href = url.href;
    return reloads;
}

function findCollectionIdByName(name) {
    const items = Array.from(document.querySelectorAll('[data-collection-id]:not([data-collection-id=""])'));
    const lowerName = name.toLowerCase();
    const match = items.find(el => el.dataset.collectionName === name)
        || items.find(el => (el.dataset.collectionName || '').toLowerCase() === lowerName)
        || items.find(el => (el.dataset.collectionName || '').toLowerCase().includes(lowerName));
    return match?.dataset.collectionId ?? null;
}

function requireChatString(command, property) {
    if (typeof command[property] !== 'string' || !command[property].trim())
        throw new Error(`The action is missing "${property}".`);
    return command[property].trim();
}

function requireChatElement(id) {
    const element = document.getElementById(id);
    if (!element || element.disabled || element.readOnly || element.getAttribute('aria-disabled') === 'true' ||
        !isChatContextElement(element)) {
        throw new Error(`"${id}" is not available. Please open the required page, modal or panel.`);
    }
    return element;
}

/** Returns a destination path when the sequence must continue on another page. */
async function executeChatAction(command) {
    if (!command || typeof command.type !== 'string') throw new Error('Invalid chat action.');
    switch (command.type) {
        case 'search':
            if (!isMainSearchPage() || typeof searchCollections !== 'function')
                throw new Error('Search can only be performed on the home page.');
            if (typeof command.text !== 'string') throw new Error('Invalid search text.');
            requireChatElement('searchText').value = command.text;
            if (await searchCollections() === false) throw new Error('Failed to search collections.');
            return;
        case 'showCollectionPanel':
        case 'openCollectionByName':
        case 'navigateToCollectionByName': {
            let id;
            if (command.type === 'showCollectionPanel') {
                id = requireChatString(command, 'id');
            } else {
                const name = requireChatString(command, 'name');
                id = findCollectionIdByName(name);
                if (!id) throw new Error(`Collection "${name}" was not found on the current page.`);
            }
            if (command.type !== 'navigateToCollectionByName' && typeof window.showCollectionOverview === 'function') {
                if (await window.showCollectionOverview(id) === false)
                    throw new Error('Failed to open the collection overview.');
                return;
            }
            return `/Collection/${encodeURIComponent(id)}`;
        }
        case 'showAdminPanel':
            if (typeof openAdminPanel !== 'function') throw new Error('The admin panel is unavailable.');
            openAdminPanel();
            return;
        case 'createNewCollection': {
            const response = await fetch('/api/collections', {
                method: 'POST', headers: { 'Content-Type': 'application/json' }
            });
            if (!response.ok) throw new Error(`Failed to create collection (server error: ${response.status}).`);
            const collection = await response.json();
            if (!collection || typeof collection.id !== 'string' || !collection.id)
                throw new Error('The collection was created but its ID could not be retrieved. Please refresh the page.');
            return `/Collection/${encodeURIComponent(collection.id)}?edit=1`;
        }
        case 'navigateHome':
            return '/';
        case 'navigate': {
            const path = requireChatString(command, 'path');
            chatNavigationUrl(path);
            return path;
        }
        case 'clickButton': {
            const id = requireChatString(command, 'id');
            if (id === 'deleteCollectionBtn') throw new Error('Collection deletion must be performed manually.');
            const element = requireChatElement(id);
            if (element.form && !element.form.checkValidity()) throw new Error('The form contains invalid values.');
            if (id === 'searchButton' && typeof searchCollections === 'function') {
                if (await searchCollections() === false) throw new Error('Failed to search collections.');
            }
            else if (typeof element.chatAction === 'function') {
                element.disabled = true;
                try {
                    if (await element.chatAction() !== true) throw new Error(t('chat-operation-failed'));
                } finally {
                    element.disabled = false;
                }
            } else if (['saveIndexEdit', 'inventoryOperationSave', 'inventoryManagementSettingsSave', 'projectEditSaveBtn'].includes(id)) {
                throw new Error('The save operation is not ready. Please reopen the form.');
            } else element.click();
            return;
        }
        case 'fillInput': {
            const value = command.value;
            if (typeof value !== 'string' && !(typeof value === 'number' && Number.isFinite(value)))
                throw new Error('Invalid input value.');
            const element = requireChatElement(requireChatString(command, 'id'));
            if (!element.matches('input:not([type="hidden"]):not([type="password"]),select,textarea'))
                throw new Error('The target is not an editable field.');
            const previous = element.value;
            element.value = String(value);
            if (element.value !== String(value) || !element.checkValidity()) {
                element.value = previous;
                throw new Error('The value is not valid for this field.');
            }
            element.dispatchEvent(new Event('input', { bubbles: true }));
            element.dispatchEvent(new Event('change', { bubbles: true }));
            return;
        }
        case 'switchLanguage':
            if (!['ja', 'en', 'de'].includes(command.lang) || typeof selectLanguage !== 'function')
                throw new Error('Unsupported language. Please use ja, en or de.');
            selectLanguage(command.lang);
            return;
        default:
            throw new Error(`Unsupported chat action: ${command.type}`);
    }
}

function executePendingChatActions() {
    const pending = loadPendingChatActions();
    clearPendingChatActions();
    const current = new URL(window.location.href);
    if (pending && pending.destination === current.pathname + current.search &&
        Number.isFinite(pending.expiresAt) && pending.expiresAt > Date.now()) {
        return executeChatActions(pending.actions);
    }
    return Promise.resolve();
}
