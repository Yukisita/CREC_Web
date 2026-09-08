const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const script = fs.readFileSync(path.join(__dirname, '../CREC_Web/wwwroot/js/project-session.js'), 'utf8');

function session() {
    const events = {};
    const windowEvents = {};
    const calls = [];
    const notice = { hidden: true };
    let confirmResult = false;
    let confirmCount = 0;
    let destination;
    let nextResponse = () => new Response('{}', { headers: { 'X-CREC-Project': 'revision-a' } });
    const window = {
        location: { href: 'http://localhost:5000/', origin: 'http://localhost:5000', assign: value => { destination = value; } },
        fetch: async (input, options) => { calls.push({ input, options }); return nextResponse(); },
        confirm: () => { confirmCount++; return confirmResult; },
        addEventListener: (type, callback) => { windowEvents[type] = callback; },
        setInterval: () => {}
    };
    const document = {
        querySelector: () => ({ content: 'revision-a' }),
        getElementById: id => id === 'projectChangedNotice' ? notice : { addEventListener() {} },
        addEventListener: (type, callback) => { events[type] = callback; }
    };
    vm.runInNewContext(script, { window, document, Headers, Request, Response, URL, t: key => key });
    return { window, events, windowEvents, calls, notice,
        response: callback => { nextResponse = callback; },
        allowDiscard: () => { confirmResult = true; },
        destination: () => destination, confirmCount: () => confirmCount };
}

(async () => {
    const s = session();
    await s.window.fetch('/api/ProjectSettings', { method: 'PUT', body: '{}', headers: { 'Content-Type': 'application/json' } });
    assert.equal(s.calls[0].options.headers.get('X-CREC-Project'), 'revision-a');
    assert.equal(s.calls[0].options.headers.get('Content-Type'), 'application/json');
    assert.equal(s.calls[0].options.cache, 'no-store');
    assert.match(s.window.ProjectSession.url('/api/File/id/video/a.mp4'), /projectRevision=revision-a/);
    s.events.input({ target: { matches: () => true, closest: () => null, readOnly: false, isConnected: true } });
    assert.equal(s.windowEvents.beforeunload, undefined, 'ordinary navigation has no discard guard');
    s.window.ProjectSession.markStale();
    assert.equal(s.notice.hidden, false);
    s.window.ProjectSession.reload();
    assert.equal(s.confirmCount(), 1);
    assert.equal(s.destination(), undefined, 'cancel preserves edited page');
    await assert.rejects(s.window.fetch('/api/ProjectSettings', { method: 'PUT' }), /projects-stale/);
    assert.equal(s.calls.length, 1, 'stale edits never sent');
    s.allowDiscard();
    s.window.ProjectSession.reload();
    assert.equal(s.destination(), '/');
    const staleResponse = session();
    staleResponse.response(() => new Response('{"code":"projects-stale"}', { status: 409 }));
    await assert.rejects(staleResponse.window.fetch('/api/collections'), /projects-stale/);
    assert.equal(staleResponse.notice.hidden, false);
    const otherProject = session();
    otherProject.response(() => new Response('{}', { headers: { 'X-CREC-Project': 'revision-b' } }));
    await assert.rejects(otherProject.window.fetch('/api/collections'), /projects-stale/);
    const busy = session();
    busy.response(() => new Response('{"code":"projects-busy"}', { status: 503 }));
    await assert.rejects(busy.window.fetch('/api/collections', { method: 'POST' }), /projects-busy/);
    assert.equal(busy.calls.length, 1, 'busy operation is not automatically replayed');
    const upload = session();
    upload.window.ProjectSession.beginUpload();
    upload.window.ProjectSession.reload();
    assert.equal(upload.confirmCount(), 1);
    assert.equal(upload.destination(), undefined, 'upload stays on page when discard canceled');
    upload.window.ProjectSession.endUpload();
    upload.window.ProjectSession.reload();
    assert.equal(upload.destination(), '/');
    const edits = session();
    const field = () => ({ matches: () => true, closest: () => null, readOnly: false, isConnected: true });
    const collection = field();
    const inventory = field();
    edits.events.input({ target: collection });
    edits.events.input({ target: inventory });
    assert.equal(edits.window.ProjectSession.confirmDiscard(), false, 'pending or failed saves keep discard confirmation');
    edits.window.ProjectSession.saved({ contains: input => input === collection });
    assert.equal(edits.window.ProjectSession.confirmDiscard(), false, 'saving one editor preserves other unsaved input');
    edits.window.ProjectSession.saved({ contains: input => input === inventory });
    assert.equal(edits.window.ProjectSession.confirmDiscard(), true, 'successfully saved editors need no confirmation');
    edits.events.input({ target: collection });
    edits.events['hidden.bs.modal']({ target: { contains: input => input === collection } });
    assert.equal(edits.window.ProjectSession.confirmDiscard(), true, 'dismissed editor is no longer unsaved');
    edits.events.input({ target: inventory });
    inventory.isConnected = false;
    assert.equal(edits.window.ProjectSession.confirmDiscard(), true, 'replaced editor is no longer unsaved');
    const file = { ...field(), type: 'file', files: [{}] };
    edits.events.change({ target: file });
    assert.equal(edits.window.ProjectSession.confirmDiscard(), false, 'selected file is retained');
    file.files = [];
    assert.equal(edits.window.ProjectSession.confirmDiscard(), true, 'cleared upload input needs no confirmation');
    const dictionaries = {};
    for (const language of ['ja', 'en', 'de']) {
        vm.runInNewContext(fs.readFileSync(path.join(__dirname, `../CREC_Web/wwwroot/js/i18n/locales/${language}.js`), 'utf8'), {
            registerTranslations: (lang, values) => { dictionaries[lang] = values; }
        });
    }
    const keys = Object.keys(dictionaries.en).filter(key => key.startsWith('projects-')).sort();
    for (const language of ['ja', 'de']) assert.deepEqual(Object.keys(dictionaries[language]).filter(key => key.startsWith('projects-')).sort(), keys);
    console.log('PASS: browser revisions, stale responses, scoped discard confirmation, saved editors, uploads, no automatic replay, and translations');
})().catch(error => { console.error(error); process.exitCode = 1; });
