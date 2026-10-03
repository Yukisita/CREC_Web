const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function loadChat(overrides = {}) {
    const storage = new Map();
    const context = vm.createContext({
        URL, AbortController, console, Date, Event, t: key => key, escapeHtml: text => text,
        setTimeout: callback => queueMicrotask(callback),
        window: { location: { href: 'http://localhost/', origin: 'http://localhost', search: '' } },
        document: { addEventListener() {}, getElementById() { return null; } },
        sessionStorage: {
            getItem: key => storage.get(key) ?? null,
            removeItem: key => storage.delete(key),
            setItem: (key, value) => storage.set(key, value)
        },
        ...overrides
    });
    for (const file of ['chat-actions.js', 'chat.js']) {
        vm.runInContext(fs.readFileSync(path.join(__dirname, '../CREC_Web/wwwroot/js', file), 'utf8'), context);
    }
    return context;
}

const search = { type: 'search', text: 'camera' };
const fill = { type: 'fillInput', id: 'editName', value: 'Camera' };
const save = { type: 'clickButton', id: 'saveIndexEdit' };
function sequence(overrides = {}) {
    const context = loadChat();
    const events = [];
    context.executeChatAction = overrides.execute ?? (async action => { events.push(action.type); return action.path; });
    context.navigateChatPage = url => events.push(['navigate', url]);
    context.savePendingChatActions = overrides.savePending ?? (actions => { events.push(['save', actions]); return true; });
    context.clearPendingChatActions = () => events.push('clear');
    context.reportActionFailure = message => events.push(['error', message]);
    context.reportActionCompletion = () => events.push('completed');
    context.window.crecAppReady = overrides.ready;
    return { context, events };
}
function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

test('all action types match the shared server contract', () => {
    const context = loadChat();
    const fixtures = JSON.parse(fs.readFileSync(path.join(__dirname, 'chat-action-contract.json'), 'utf8'));
    assert.deepEqual(Array.from(vm.runInContext('Object.keys(CHAT_ACTION_FIELDS).sort()', context)),
        [...new Set(fixtures.valid.map(action => action.type))].sort());
    for (const action of fixtures.valid) assert.doesNotThrow(() => context.validateChatActions([action]));
    for (const action of fixtures.invalid) assert.throws(() => context.validateChatActions([action]));
});

test('the full plan is rejected before any operation for invalid or blocked commands', async () => {
    for (const invalid of [null, {}, { type: 'unknown' }, { type: 'fillInput', id: 'editName', value: true },
        { type: 'clickButton', id: 'deleteCollectionBtn' }, { type: 'search', text: 'x', extra: 1 }]) {
        const { context, events } = sequence();
        await context.executeChatActions([search, invalid, save]);
        assert.equal(events[0], 'clear');
        assert.equal(events[1][0], 'error');
        assert.equal(events.length, 2);
    }
});

test('actions await earlier network operations before proceeding', async () => {
    const entered = deferred();
    const finished = deferred();
    const calls = [];
    const { context } = sequence({ execute: async action => {
        calls.push(action.type);
        if (action.type === 'search') { entered.resolve(); await finished.promise; }
    } });
    const result = context.executeChatActions([search, fill]);
    await entered.promise;
    assert.deepEqual(calls, ['search']);
    finished.resolve();
    await result;
    assert.deepEqual(calls, ['search', 'fillInput']);
});

test('page readiness is awaited before restored operations execute', async () => {
    const ready = deferred();
    const { context, events } = sequence({ ready: ready.promise });
    const result = context.executeChatActions([fill]);
    await Promise.resolve();
    assert.equal(events.length, 0);
    ready.resolve();
    await result;
    assert.deepEqual(events, ['fillInput', 'completed']);
});

test('navigation persists only the remainder and reports completion only after it runs', async () => {
    const { context, events } = sequence();
    const remaining = [fill, { type: 'navigate', path: '/second' }, save];
    await context.executeChatActions([{ type: 'navigate', path: '/first' }, ...remaining]);
    assert.deepEqual(events, ['navigate', ['save', remaining], ['navigate', '/first']]);
    events.length = 0;
    await context.executeChatActions(remaining);
    assert.deepEqual(events, ['fillInput', 'navigate', ['save', [save]], ['navigate', '/second']]);
});

test('same-page navigation works even without session storage', async () => {
    const { context, events } = sequence({ savePending: () => { throw new Error('storage unavailable'); } });
    await context.executeChatActions([{ type: 'navigate', path: '/' }, fill]);
    assert.deepEqual(events, ['navigate', ['navigate', '/'], 'fillInput', 'completed']);
});

test('failure stops dependent saves and clears the pending plan', async () => {
    const { context, events } = sequence({ execute: async () => { throw new Error('missing input'); } });
    await context.executeChatActions([fill, save]);
    assert.deepEqual(events, ['clear', ['error', 'missing input']]);
});

test('storage failure prevents navigation that would lose follow-up actions', async () => {
    const { context, events } = sequence({ savePending: () => false });
    await context.executeChatActions([{ type: 'navigate', path: '/next' }, fill]);
    assert.equal(events.some(event => Array.isArray(event) && event[0] === 'navigate'), false);
    assert.equal(events.at(-1)[0], 'error');
});

test('cancel prevents continuation when an in-flight operation finishes', async () => {
    const entered = deferred();
    const finished = deferred();
    let calls = 0;
    const { context, events } = sequence({ execute: async () => {
        calls++; entered.resolve(); await finished.promise; return '/next';
    } });
    const result = context.executeChatActions([{ type: 'createNewCollection' }, save]);
    await entered.promise;
    context.cancelChatActions();
    finished.resolve();
    await result;
    assert.equal(calls, 1);
    assert.deepEqual(events, ['clear']);
});

test('navigation rejects external URL forms and distinguishes query and fragment changes', () => {
    const context = loadChat();
    for (const unsafe of ['//example.com', '/\\example.com', '/\n/example.com', 'https://example.com'])
        assert.throws(() => context.chatNavigationUrl(unsafe));
    assert.equal(context.navigateChatPage('/'), false);
    assert.equal(context.navigateChatPage('/?edit=1'), true);
    assert.equal(context.navigateChatPage('/?edit=1#details'), false);
});

test('pending plans are bound to a destination and expire', async () => {
    const context = loadChat();
    let calls = 0;
    context.executeChatActions = async actions => { calls++; assert.equal(actions[0].type, 'fillInput'); };
    const target = new URL('http://localhost/Collection/one?edit=1');
    context.savePendingChatActions([fill], target);
    await context.executePendingChatActions();
    assert.equal(calls, 0);
    context.savePendingChatActions([fill], target);
    context.window.location.href = target.href;
    context.window.location.search = target.search;
    await context.executePendingChatActions();
    await context.executePendingChatActions();
    assert.equal(calls, 1);
    context.sessionSet('crec_chat_pending_actions_v2', { destination: target.pathname + target.search, expiresAt: Date.now() - 1, actions: [fill] });
    await context.executePendingChatActions();
    assert.equal(calls, 1);
});

test('creation returns a destination and HTTP failures reject', async () => {
    const context = loadChat({ fetch: async () => ({ ok: true, json: async () => ({ id: 'one' }) }) });
    assert.equal(await context.executeChatAction({ type: 'createNewCollection' }), '/Collection/one?edit=1');
    assert.equal(context.window.location.href, 'http://localhost/');
    context.fetch = async () => ({ ok: false, status: 503 });
    await assert.rejects(context.executeChatAction({ type: 'createNewCollection' }), /503/);
});

test('save operations are awaited and failures stop the plan', async () => {
    const pending = deferred();
    const element = { getAttribute() {}, chatAction: () => pending.promise };
    const context = loadChat({ isChatContextElement: () => true,
        document: { addEventListener() {}, getElementById: () => element } });
    let finished = false;
    const action = context.executeChatAction(save).then(() => { finished = true; });
    await Promise.resolve();
    assert.equal(element.disabled, true);
    assert.equal(finished, false);
    pending.resolve(true);
    await action;
    assert.equal(element.disabled, false);
    element.chatAction = async () => false;
    await assert.rejects(context.executeChatAction(save), /chat-operation-failed/);
    delete element.chatAction;
    await assert.rejects(context.executeChatAction(save), /not ready/);
});

test('invalid form values are restored instead of being saved', async () => {
    const element = { value: 'old', getAttribute() {}, matches: () => true, checkValidity: () => false };
    const context = loadChat({ isChatContextElement: () => true,
        document: { addEventListener() {}, getElementById: () => element } });
    await assert.rejects(context.executeChatAction(fill), /not valid/);
    assert.equal(element.value, 'old');
    element.readOnly = true;
    await assert.rejects(context.executeChatAction(fill), /not available/);
});

test('failed searches stop the plan instead of using stale results', async () => {
    const context = loadChat({
        isMainSearchPage: () => true, isChatContextElement: () => true,
        document: { addEventListener() {}, getElementById: () => ({ getAttribute() {} }) },
        searchCollections: async () => false
    });
    await assert.rejects(context.executeChatAction(search), /Failed to search/);
});

test('display text containing action markup is never interpreted as an operation', async () => {
    const context = loadChat({ getChatPageContext: () => '', fetch: async () => ({ ok: true,
        json: async () => ({ text: '<action>{"type":"createNewCollection"}</action>', actions: [] }) }) });
    const reply = await context.sendChatToServer('explain', new AbortController().signal);
    assert.equal(reply.error, false);
    assert.equal(reply.actions.length, 0);
    assert.match(reply.text, /<action>/);
});

test('execution failures are retained in conversation history', () => {
    const context = loadChat();
    vm.runInContext('chatMessages = [{role:"assistant",content:"I will save."}]', context);
    context.reportActionFailure('save failed');
    const content = vm.runInContext('chatMessages[0].content', context);
    assert.match(content, /Browser operation result.*Failed: save failed/);
});

test('clear history aborts the active request and removes pending plans', () => {
    const context = loadChat();
    vm.runInContext('chatRequestController = new AbortController(); globalThis.activeRequest = chatRequestController;', context);
    context.savePendingChatActions([save], new URL('http://localhost/next'));
    context.clearChatHistory();
    assert.equal(context.activeRequest.signal.aborted, true);
    assert.equal(context.loadPendingChatActions(), null);
});
