const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function loadChat(overrides = {}) {
    const context = vm.createContext({
        URL, AbortController, console, setTimeout: callback => queueMicrotask(callback),
        window: { location: { href: 'http://localhost/', origin: 'http://localhost' } },
        document: { addEventListener() {}, getElementById() { return null; } },
        sessionStorage: { getItem() { return null; }, removeItem() {}, setItem() {} },
        ...overrides
    });
    for (const file of ['chat-actions.js', 'chat.js']) {
        vm.runInContext(fs.readFileSync(path.join(__dirname, '../CREC_Web/wwwroot/js', file), 'utf8'), context);
    }
    return { context };
}

function createSequence(context, overrides = {}) {
    const events = [];
    context.executeChatAction = overrides.execute ?? (async action => { events.push(action.type); return action.path; });
    context.navigateChatPage = overrides.navigate ?? (path => { events.push(['navigate', path]); return true; });
    context.savePendingChatActions = overrides.savePending ?? (actions => { events.push(['save', actions]); return true; });
    context.clearPendingChatActions = () => events.push('clear');
    context.reportActionFailure = message => events.push(['error', message]);
    context.window.crecAppReady = overrides.ready?.();
    return { runner: { run: context.executeChatActions, cancel: context.cancelChatActions }, events };
}

function deferred() {
    let resolve;
    const promise = new Promise(done => { resolve = done; });
    return { promise, resolve };
}

test('parsing removes action markup without repairing invalid JSON', () => {
    const { context } = loadChat();
    const result = context.parseChatResponse('Hello\n`<action>{"type":"search","text":"日本語"}</action>`\n<action>{"type":"clickButton","id":"save"}}</action>');
    assert.equal(result.text, 'Hello');
    assert.equal(result.actions.length, 1);
    assert.equal(result.actions[0].text, '日本語');
    assert.equal(context.stripChatActions('**Keep** `code`'), '**Keep** `code`');
});

test('actions await earlier network operations before proceeding', async () => {
    const { context } = loadChat();
    const entered = deferred();
    const finished = deferred();
    const calls = [];
    const { runner } = createSequence(context, { execute: async action => {
        calls.push(action.type);
        if (action.type === 'search') { entered.resolve(); await finished.promise; }
    } });
    const result = runner.run([{ type: 'search' }, { type: 'open' }]);
    await entered.promise;
    assert.deepEqual(calls, ['search']);
    finished.resolve();
    await result;
    assert.deepEqual(calls, ['search', 'open']);
});

test('page readiness is awaited before restored operations execute', async () => {
    const { context } = loadChat();
    const ready = deferred();
    const { runner, events } = createSequence(context, { ready: () => ready.promise });
    const result = runner.run([{ type: 'fill' }]);
    await Promise.resolve();
    assert.equal(events.length, 0);
    ready.resolve();
    await result;
    assert.deepEqual(events, ['fill']);
});

test('navigation persists only the remaining actions and stops the old page', async () => {
    const { context } = loadChat();
    const { runner, events } = createSequence(context);
    const remaining = [{ type: 'fill' }, { type: 'navigate', path: '/second' }, { type: 'save' }];
    await runner.run([{ type: 'navigate', path: '/first' }, ...remaining]);
    assert.deepEqual(events, ['navigate', ['save', remaining], ['navigate', '/first']]);
    events.length = 0;
    await runner.run(remaining);
    assert.deepEqual(events, ['fill', 'navigate', ['save', [{ type: 'save' }]], ['navigate', '/second']]);
});

test('same-page navigation continues once and clears persisted actions', async () => {
    const { context } = loadChat();
    const { runner, events } = createSequence(context, { navigate: () => false });
    await runner.run([{ type: 'navigate', path: '/' }, { type: 'fill' }]);
    assert.deepEqual(events, ['navigate', ['save', [{ type: 'fill' }]], 'clear', 'fill']);
});

test('failure clears pending actions and prevents dependent saves', async () => {
    const { context } = loadChat();
    const { runner, events } = createSequence(context, { execute: async () => { throw new Error('missing input'); } });
    await runner.run([{ type: 'fill' }, { type: 'save' }]);
    assert.deepEqual(events, ['clear', ['error', 'missing input']]);
});

test('storage failure prevents navigation with lost follow-up actions', async () => {
    const { context } = loadChat();
    let navigated = false;
    const { runner, events } = createSequence(context, {
        savePending: () => false,
        navigate: () => { navigated = true; }
    });
    await runner.run([{ type: 'navigate', path: '/next' }, { type: 'fill' }]);
    assert.equal(navigated, false);
    assert.equal(events.at(-1)[0], 'error');
});

test('cancel prevents follow-up operations after an in-flight operation finishes', async () => {
    const { context } = loadChat();
    const entered = deferred();
    const finished = deferred();
    let calls = 0;
    const { runner, events } = createSequence(context, { execute: async () => {
        calls++; entered.resolve(); await finished.promise; return '/next';
    } });
    const result = runner.run([{ type: 'create' }, { type: 'save' }]);
    await entered.promise;
    runner.cancel();
    finished.resolve();
    await result;
    assert.equal(calls, 1);
    assert.deepEqual(events, ['clear']);
});

test('navigation rejects external URL forms and preserves query-only changes', () => {
    const { context } = loadChat();
    for (const unsafe of ['//example.com', '/\\example.com', '/\n/example.com', 'https://example.com']) {
        assert.throws(() => context.chatNavigationUrl(unsafe));
    }
    assert.equal(context.navigateChatPage('/'), false);
    assert.equal(context.navigateChatPage('/?edit=1'), true);
    assert.equal(context.window.location.href, 'http://localhost/?edit=1');
    assert.equal(context.navigateChatPage('/?edit=1#details'), false);
    assert.equal(context.window.location.href, 'http://localhost/?edit=1#details');
});

test('collection creation is awaited and returns a destination without navigating', async () => {
    const response = deferred();
    const { context } = loadChat({ fetch: () => response.promise });
    const action = context.executeChatAction({ type: 'createNewCollection' });
    response.resolve({ ok: true, json: async () => ({ id: 'collection one' }) });
    assert.equal(await action, '/Collection/collection%20one?edit=1');
    assert.equal(context.window.location.href, 'http://localhost/');
});

test('collection creation errors reject the action sequence', async () => {
    const { context } = loadChat({ fetch: async () => ({ ok: false, status: 503 }) });
    await assert.rejects(context.executeChatAction({ type: 'createNewCollection' }), /503/);
});

test('search waits for rendered results', async () => {
    const rendered = deferred();
    let complete = false;
    const input = { value: '', getAttribute() {}, closest() { return null; } };
    const { context } = loadChat({
        isMainSearchPage: () => true, isChatContextElement: () => true,
        document: { addEventListener() {}, getElementById() { return input; } },
        searchCollections: () => rendered.promise
    });
    const action = context.executeChatAction({ type: 'search', text: 'camera' }).then(() => { complete = true; });
    await Promise.resolve();
    assert.equal(complete, false);
    assert.equal(input.value, 'camera');
    rendered.resolve();
    await action;
});

test('collection deletion remains blocked in the browser', async () => {
    const { context } = loadChat();
    await assert.rejects(context.executeChatAction({ type: 'clickButton', id: 'deleteCollectionBtn' }), /manually/);
});

test('failed searches stop the sequence instead of using stale results', async () => {
    const { context } = loadChat({
        isMainSearchPage: () => true, isChatContextElement: () => true,
        document: { addEventListener() {}, getElementById() { return { getAttribute() {} }; } },
        searchCollections: async () => false
    });
    await assert.rejects(context.executeChatAction({ type: 'search', text: 'camera' }), /Failed to search/);
});

test('invalid stored history and pending actions cannot break initialization', async () => {
    const { context } = loadChat({ sessionStorage: {
        getItem(key) { return key.includes('history') ? '[null,42,{"role":"assistant","content":false},{"role":"user","content":"ok"}]' : '{"length":1}'; },
        removeItem() {}, setItem() {}
    } });
    const history = context.loadChatSession();
    assert.equal(history.length, 1);
    assert.equal(history[0].content, 'ok');
    await context.executePendingChatActions();
});

test('clear history aborts the active request and invalidates queued actions', () => {
    const { context } = loadChat();
    vm.runInContext('chatRequestController = new AbortController(); globalThis.activeRequest = chatRequestController; chatMessages = [{role:"user", content:"old"}];', context);
    context.clearChatHistory();
    assert.equal(context.activeRequest.signal.aborted, true);
    assert.equal(vm.runInContext('chatMessages.length', context), 0);
});
