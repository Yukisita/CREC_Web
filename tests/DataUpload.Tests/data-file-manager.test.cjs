const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sourceRoot = path.resolve(__dirname, '../../CREC_Web/wwwroot/js');
const limit = 8 * 1024 * 1024 * 1024;

function setup(language = 'ja') {
    const translations = {};
    const context = vm.createContext({
        window: {},
        registerTranslations: (locale, strings) => { translations[locale] = strings; },
        t: key => translations[language][key],
        FormData: class { append() {} }
    });
    for (const locale of ['ja', 'en', 'de']) {
        vm.runInContext(fs.readFileSync(path.join(sourceRoot, `i18n/locales/${locale}.js`), 'utf8'), context);
    }
    vm.runInContext(fs.readFileSync(path.join(sourceRoot, 'data-file-manager.js'), 'utf8'), context);
    const manager = new context.window.DataFileManager(null, 'collection');
    manager.setLoading = () => {};
    manager.showInfo = () => {};
    manager.showSuccess = () => {};
    manager.loadDirectory = async () => {};
    return { manager, translations };
}

test('4.8 GB and files up to exactly 8 GB are sent', async () => {
    for (const size of [Math.floor(4.8 * 1024 ** 3), limit - 1, limit]) {
        const { manager } = setup();
        let requests = 0;
        manager.request = async () => { requests++; };
        await manager.uploadFile({ name: 'kali-linux-2026.2-installer-amd64.iso', size });
        assert.equal(requests, 1);
    }
});

test('a file one byte over 8 GB is rejected before sending or changing loading state', async () => {
    const { manager } = setup();
    manager.request = () => assert.fail('The oversized file must not be sent');
    manager.setLoading = () => assert.fail('Loading must not start');
    await assert.rejects(manager.uploadFile({ size: limit + 1 }),
        { message: 'ファイルサイズが上限の8GBを超えています' });
});

test('an upload HTTP 413 shows the size error in every supported language, including a non-JSON response', async () => {
    for (const language of ['ja', 'en', 'de']) {
        const { manager, translations } = setup(language);
        for (const json of [async () => ({ title: 'Payload too large' }), async () => { throw new Error('HTML'); }]) {
            const error = await manager.createResponseError({ status: 413, json });
            manager.request = async () => { throw error; };
            await assert.rejects(manager.uploadFile({ size: 123 }),
                { message: translations[language]['data-upload-too-large'] });
            assert.match(error.message, /8\s?GB/);
            assert.equal(error.status, 413);
        }
    }
});

test('the existing archive limit error is not presented as an 8 GB upload limit', async () => {
    const { manager } = setup();
    const title = 'The directory is too large to archive.';
    const error = await manager.createResponseError({
        status: 413,
        json: async () => ({ title, code: 'archive_size_limit_exceeded' })
    });
    assert.equal(error.message, title);
});

test('invalid names and paths retain their existing error message', async () => {
    const { manager } = setup();
    const error = await manager.createResponseError({ status: 400, json: async () => ({}) });
    assert.equal(error.message, '名前またはパスが正しくありません');
});
