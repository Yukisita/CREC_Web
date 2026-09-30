/*
CREC Web - AI Chat Actions
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

/** Parse only valid JSON. Never repair rejected commands into executable actions. */
function parseChatResponse(text) {
    const actions = [];
    const plainText = text.replace(/<action>([\s\S]*?)<\/action>/g, (_, json) => {
        try {
            const command = JSON.parse(json);
            if (command && typeof command.type === 'string') actions.push(command);
        } catch { /* Malformed action tags are removed from display and never executed. */ }
        return '';
    });
    return {
        actions,
        text: plainText
            .replace(/```[^\n]*\n\s*```/g, '')
            .replace(/`\s*`/g, '')
            .replace(/^[ \t]*`+[ \t]*$/gm, '')
            .replace(/^[ \t]*[{}\[\]]+[ \t]*$/gm, '')
            .replace(/\n{3,}/g, '\n\n').trim()
    };
}

function stripChatActions(text) {
    return parseChatResponse(text).text;
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
        await window.crecAppReady;
        await window.crecCollectionReady;
        for (let index = 0; index < actions.length; index++) {
            // Allow Bootstrap transitions to finish; network operations are awaited.
            await new Promise(resolve => setTimeout(resolve, 400));
            if (generation !== chatActionGeneration) return;
            const destination = await executeChatAction(actions[index]);
            if (generation !== chatActionGeneration) return;
            if (destination) {
                const remaining = actions.slice(index + 1);
                if (remaining.length && !savePendingChatActions(remaining))
                    throw new Error('Unable to save the remaining actions for page navigation.');
                if (navigateChatPage(destination)) return;
                clearPendingChatActions();
            }
        }
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

function navigateChatPage(path) {
    const url = chatNavigationUrl(path);
    if (url.href === window.location.href) return false;
    const current = new URL(window.location.href);
    window.location.href = url.href;
    // Fragment changes do not reload the page, so continue the sequence here.
    return url.pathname !== current.pathname || url.search !== current.search;
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
    if (!element || element.disabled || element.getAttribute('aria-disabled') === 'true' ||
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
            if (id === 'searchButton' && typeof searchCollections === 'function') {
                if (await searchCollections() === false) throw new Error('Failed to search collections.');
            }
            else element.click();
            return;
        }
        case 'fillInput': {
            const value = command.value;
            if (typeof value !== 'string' && !(typeof value === 'number' && Number.isFinite(value)))
                throw new Error('Invalid input value.');
            const element = requireChatElement(requireChatString(command, 'id'));
            element.value = String(value);
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
    if (Array.isArray(pending) && pending.length) return executeChatActions(pending);
    return Promise.resolve();
}
