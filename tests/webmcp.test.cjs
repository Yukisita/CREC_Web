const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../CREC_Web/wwwroot/js/webmcp.js'), 'utf8');
function load({ supported = true, fetchResult, registerError } = {}) {
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
            if (registerError) return registerError();
            assert.ok(options.signal, 'Registration signal belongs in the second argument.');
            assert.equal(tool.signal, undefined);
            assert.equal(tool.annotations.readOnlyHint, true);
            assert.equal(tool.inputSchema.additionalProperties, false);
            if (options.signal.aborted) return;
            tools.set(tool.name, tool);
            options.signal.addEventListener('abort', () => tools.delete(tool.name), { once: true });
        }
    };
    const context = vm.createContext({
        document, window, AbortController, AbortSignal, URL, URLSearchParams,
        console: { warn() {} },
        async fetch(url, options) {
            calls.push({ url, options });
            return fetchResult ? await fetchResult(url, options) : {
                ok: true, json: async () => url.pathname.endsWith('/status')
                    ? { revision: 'project-a', name: 'Project A', hasProject: true }
                    : { collections: [{ id: 'camera-id', name: 'カメラ' }] }
            };
        }
    });
    vm.runInContext(source, context);
    return { tools, document, window, calls };
}

test('unsupported browsers keep normal page behavior', () => {
    const page = load({ supported: false });
    assert.equal(page.tools.size, 0);
    assert.equal(page.calls.length, 0);
});

test('three read tools use the page project and shared API', async () => {
    const page = load();
    assert.deepEqual([...page.tools.keys()], ['get_current_project', 'search_collections', 'get_collection']);
    assert.equal((await page.tools.get('get_current_project').execute({})).name, 'Project A');
    const result = await page.tools.get('search_collections').execute({ query: 'カメラ', pageSize: 5 });
    assert.equal(result.collections[0].name, 'カメラ');
    await page.tools.get('get_collection').execute({ collectionId: 'camera/id?' });
    assert.equal(page.calls[1].url.searchParams.get('query'), 'カメラ');
    assert.equal(page.calls[1].url.searchParams.get('projectRevision'), 'project-a');
    assert.equal(page.calls[1].url.searchParams.get('pageSize'), '5');
    assert.equal(page.calls[2].url.pathname, '/api/collection-queries/camera%2Fid%3F');
    assert.ok(page.calls.every(call => call.options.cache === 'no-store'));
});

test('invalid input is rejected before any request', async () => {
    const page = load();
    const search = page.tools.get('search_collections');
    for (const args of [{ query: 1 }, { query: 'x'.repeat(257) }, { page: 0 }, { page: 1.5 }, { pageSize: 51 }])
        await assert.rejects(search.execute(args), /invalid-query/);
    await assert.rejects(page.tools.get('get_collection').execute({ collectionId: ' ' }), /invalid-collection-id/);
    assert.equal(page.calls.length, 0);
});

test('project changes remove tools and reject cached callbacks', async () => {
    const page = load();
    const search = page.tools.get('search_collections');
    page.window.ProjectSession.markStale();
    assert.equal(page.tools.size, 0);
    await assert.rejects(search.execute({}), /projects-stale/);
    assert.equal(page.calls.length, 0);
});

test('a newer project status is not reported as this page project', async () => {
    const page = load({ fetchResult: async () => ({ ok: true, json: async () => ({ revision: 'project-b' }) }) });
    await assert.rejects(page.tools.get('get_current_project').execute({}), /projects-stale/);
    assert.equal(page.tools.size, 0);
});

test('project changes during a request discard the response', async () => {
    let finish;
    const page = load({ fetchResult: () => new Promise(resolve => { finish = resolve; }) });
    const pending = page.tools.get('search_collections').execute({});
    page.window.ProjectSession.markStale();
    assert.equal(page.calls[0].options.signal.aborted, true);
    finish({ ok: true, json: async () => ({ collections: [] }) });
    await assert.rejects(pending);
});

test('browser tool cancellation interrupts the request', async () => {
    const execution = new AbortController();
    const page = load({ fetchResult: async (_, options) => {
        execution.abort();
        assert.equal(options.signal.aborted, true);
        return { ok: true, json: async () => ({}) };
    } });
    await assert.rejects(page.tools.get('search_collections').execute({}, { signal: execution.signal }));
});

test('history navigation re-registers tools without reloading inputs', async () => {
    const page = load();
    page.window.dispatchEvent(new Event('pagehide'));
    assert.equal(page.tools.size, 0);
    const restore = new Event('pageshow');
    restore.persisted = true;
    page.window.dispatchEvent(restore);
    assert.equal(page.tools.size, 3);
    assert.equal((await page.tools.get('get_current_project').execute({})).name, 'Project A');
});

test('registration failures do not leave partial tool registrations', async () => {
    const page = load({ registerError: () => Promise.reject(new Error('unsupported')) });
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(page.tools.size, 0);
    assert.doesNotThrow(() => load({ registerError: () => { throw new Error('unsupported'); } }));
});

test('API errors are reported as failures without inventing results', async () => {
    const page = load({ fetchResult: async () => ({ ok: false, json: async () => ({ code: 'collection-not-found' }) }) });
    await assert.rejects(page.tools.get('get_collection').execute({ collectionId: 'missing' }), /collection-not-found/);
});
