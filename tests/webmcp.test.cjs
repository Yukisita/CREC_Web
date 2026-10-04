const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../CREC_Web/wwwroot/js/webmcp.js'), 'utf8');
const flush = () => new Promise(resolve => setImmediate(resolve));
const definitions = [
    { name: 'get_current_project', description: 'Current project', inputSchema: { type: 'object', properties: {} } },
    { name: 'search_collections', description: 'Search', inputSchema: { type: 'object',
        properties: { projectRevision: { type: 'string' }, query: { type: 'string' },
            field: { type: 'string', enum: ['All', 'Name'] } }, required: ['projectRevision'] } },
    { name: 'read_collection_file', description: 'Read a file', inputSchema: { type: 'object',
        properties: { projectRevision: { type: 'string' }, collectionId: { type: 'string' }, path: { type: 'string' } },
        required: ['projectRevision', 'collectionId', 'path'] } }
];

async function load({ supported = true, fetchResult, discover, registerError } = {}) {
    const tools = new Map();
    const document = new EventTarget();
    const window = new EventTarget();
    const calls = [];
    let stale = false;
    window.location = { origin: 'http://localhost', reload() { throw new Error('Must preserve page inputs.'); } };
    window.ProjectSession = {
        revision: 'project-a', isStale: () => stale,
        markStale() { stale = true; document.dispatchEvent(new Event('crec-project-stale')); }
    };
    if (supported) document.modelContext = {
        registerTool(tool, options) {
            if (registerError) registerError(tool);
            assert.ok(options.signal);
            assert.equal(tool.annotations.readOnlyHint, true);
            if (options.signal.aborted) return;
            tools.set(tool.name, tool);
            options.signal.addEventListener('abort', () => tools.delete(tool.name), { once: true });
        }
    };
    vm.runInNewContext(source, {
        document, window, AbortController, AbortSignal, URL, structuredClone,
        console: { warn() {} },
        async fetch(url, options) {
            calls.push({ url, options });
            if (url.pathname === '/api/ai-tools')
                return discover ? await discover(url, options) : { ok: true, json: async () => structuredClone(definitions) };
            return fetchResult ? await fetchResult(url, options) : {
                ok: true, json: async () => url.pathname.endsWith('/get_current_project')
                    ? { revision: 'project-a', name: 'Project A', hasProject: true }
                    : { items: [{ id: 'camera-id', name: 'カメラ' }] }
            };
        }
    });
    await flush();
    return { tools, document, window, calls };
}

test('unsupported browsers keep normal page behavior without requests', async () => {
    const page = await load({ supported: false });
    assert.equal(page.tools.size, 0);
    assert.equal(page.calls.length, 0);
});

test('tools and schemas come from the shared server catalog and bind to the page', async () => {
    const page = await load();
    assert.deepEqual([...page.tools.keys()], definitions.map(tool => tool.name));
    const schema = page.tools.get('search_collections').inputSchema;
    assert.equal(schema.properties.projectRevision, undefined);
    assert.equal(schema.required.length, 0);
    assert.deepEqual(schema.properties.field.enum, ['All', 'Name']);
    assert.equal(schema.additionalProperties, false);
    assert.equal((await page.tools.get('get_current_project').execute({})).name, 'Project A');
    await page.tools.get('search_collections').execute({ query: 'カメラ', field: 'Name', projectRevision: 'other' });
    const call = page.calls.at(-1);
    assert.equal(call.url.pathname, '/api/ai-tools/search_collections');
    assert.equal(call.url.searchParams.get('projectRevision'), 'project-a');
    assert.equal(call.options.headers['X-CREC-Project'], 'project-a');
    assert.deepEqual(JSON.parse(call.options.body), { query: 'カメラ', field: 'Name' });
    assert.ok(page.calls.every(call => call.options.cache === 'no-store'));
});

test('file paths are carried as data rather than as request URLs', async () => {
    const page = await load();
    await page.tools.get('read_collection_file').execute({ collectionId: 'camera-id', path: '説明/readme.txt' });
    assert.equal(page.calls.at(-1).url.pathname, '/api/ai-tools/read_collection_file');
    assert.equal(JSON.parse(page.calls.at(-1).options.body).path, '説明/readme.txt');
});

test('project changes remove tools and reject cached callbacks', async () => {
    const page = await load();
    const search = page.tools.get('search_collections');
    page.window.ProjectSession.markStale();
    assert.equal(page.tools.size, 0);
    await assert.rejects(search.execute({}), /projects-stale/);
    assert.equal(page.calls.length, 1);
});

test('project changes during discovery do not register stale tools', async () => {
    let finish;
    const page = await load({ discover: () => new Promise(resolve => { finish = resolve; }) });
    page.window.ProjectSession.markStale();
    finish({ ok: true, json: async () => definitions });
    await flush();
    assert.equal(page.tools.size, 0);
    assert.equal(page.calls[0].options.signal.aborted, true);
});

test('a newer project status is not reported as this page project', async () => {
    const page = await load({ fetchResult: async () => ({ ok: true, json: async () => ({ revision: 'project-b' }) }) });
    await assert.rejects(page.tools.get('get_current_project').execute({}), /projects-stale/);
    assert.equal(page.tools.size, 0);
});

test('project changes during a request discard the response', async () => {
    let finish;
    const page = await load({ fetchResult: () => new Promise(resolve => { finish = resolve; }) });
    const pending = page.tools.get('search_collections').execute({});
    page.window.ProjectSession.markStale();
    assert.equal(page.calls.at(-1).options.signal.aborted, true);
    finish({ ok: true, json: async () => ({ items: [] }) });
    await assert.rejects(pending);
});

test('browser cancellation interrupts the request', async () => {
    const execution = new AbortController();
    const page = await load({ fetchResult: async (_, options) => {
        execution.abort();
        assert.equal(options.signal.aborted, true);
        return { ok: true, json: async () => ({}) };
    } });
    await assert.rejects(page.tools.get('search_collections').execute({}, { signal: execution.signal }));
});

test('history navigation discovers tools again and keeps old callbacks cancelled', async () => {
    const page = await load();
    const old = page.tools.get('search_collections');
    page.window.dispatchEvent(new Event('pagehide'));
    assert.equal(page.tools.size, 0);
    const restore = new Event('pageshow');
    restore.persisted = true;
    page.window.dispatchEvent(restore);
    await flush();
    assert.equal(page.tools.size, definitions.length);
    await assert.rejects(old.execute({}));
    assert.equal((await page.tools.get('get_current_project').execute({})).name, 'Project A');
});

test('registration failure removes tools already registered', async () => {
    const page = await load({ registerError: tool => {
        if (tool.name === 'search_collections') throw new Error('unsupported');
    } });
    assert.equal(page.tools.size, 0);
});

test('server validation and file errors are returned without inventing results', async () => {
    const page = await load({ fetchResult: async () => ({ ok: false, json: async () => ({ code: 'file-changed' }) }) });
    await assert.rejects(page.tools.get('read_collection_file').execute({ collectionId: 'camera-id', path: 'file' }), /file-changed/);
});
