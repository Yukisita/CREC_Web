/* Keep requests tied to the project rendered into this page, including old tabs. */
(() => {
    'use strict';
    const revision = document.querySelector('meta[name="crec-project-revision"]').content;
    const originalFetch = window.fetch.bind(window);
    let stale = false;
    let dirty = false;
    let leaving = false;
    let uploads = 0;
    const message = key => typeof t === 'function' ? t(key) : key;

    function markStale() {
        stale = true;
        const notice = document.getElementById('projectChangedNotice');
        if (notice) notice.hidden = false;
        // Leave inputs in place until the user chooses to reload.
    }

    function confirmDiscard() {
        return (!dirty && uploads === 0) || window.confirm(message('projects-discard'));
    }

    function reload() {
        if (!confirmDiscard()) return;
        leaving = true;
        window.location.assign('/');
    }

    function url(value) {
        const target = new URL(value, window.location.href);
        if (target.origin === window.location.origin && target.pathname.toLowerCase().startsWith('/api/'))
            target.searchParams.set('projectRevision', revision);
        return target.href;
    }

    window.ProjectSession = Object.freeze({
        revision, url, markStale, confirmDiscard, reload,
        isStale: () => stale,
        saved: () => { dirty = false; },
        beginUpload: () => { uploads++; },
        endUpload: () => { uploads--; },
        navigateAfterSwitch: () => { leaving = true; window.location.assign('/'); }
    });

    window.fetch = async (input, options) => {
        const target = new URL(input instanceof Request ? input.url : input, window.location.href);
        if (target.origin !== window.location.origin || !target.pathname.toLowerCase().startsWith('/api/'))
            return originalFetch(input, options);
        const apiPath = target.pathname.toLowerCase();
        const management = apiPath === '/api/projects' || apiPath.startsWith('/api/projects/');
        if (stale && !management) throw new Error(message('projects-stale'));
        const headers = new Headers(options?.headers || (input instanceof Request ? input.headers : undefined));
        headers.set('X-CREC-Project', revision);
        headers.set('X-CREC-Request', '1');
        const response = await originalFetch(input, { ...options, headers, cache: 'no-store' });
        const responseRevision = response.headers.get('X-CREC-Project');
        if (responseRevision && responseRevision !== revision) markStale();
        if (!response.ok && (response.status === 409 || response.status === 503)) {
            const problem = await response.clone().json().catch(() => null);
            if (problem?.code === 'projects-stale') markStale();
            if (problem?.code?.startsWith('projects-')) throw new Error(message(problem.code));
        }
        if (stale && !management) throw new Error(message('projects-stale'));
        return response;
    };

    document.addEventListener('input', event => {
        if (event.target.matches('input, textarea, select') && !event.target.readOnly
            && !event.target.closest('#projectSwitchModal, .search-filters')) dirty = true;
    });
    document.addEventListener('change', event => {
        if (event.target.matches('select, input[type="file"], input[type="checkbox"]')
            && !event.target.closest('#projectSwitchModal, .search-filters')) dirty = true;
    });
    window.addEventListener('beforeunload', event => {
        if (!leaving && (dirty || uploads > 0)) { event.preventDefault(); event.returnValue = ''; }
    });

    async function checkProject() {
        if (stale || leaving) return;
        try {
            const response = await originalFetch('/api/projects/status', { cache: 'no-store' });
            if (response.ok && (await response.json()).revision !== revision) markStale();
        } catch { /* Temporary disconnect: retain all input and retry on the next poll. */ }
    }
    document.addEventListener('DOMContentLoaded', () => {
        document.getElementById('reloadProjectBtn').addEventListener('click', reload);
        checkProject();
        window.setInterval(checkProject, 2000);
    });
    window.addEventListener('pageshow', checkProject);
    window.addEventListener('focus', checkProject);
})();
