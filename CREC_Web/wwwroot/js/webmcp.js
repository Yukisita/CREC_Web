/** C#の共通カタログを、開いているページに固定したWebMCP操作として登録する。 */
(function registerCrecSiteTools() {
    'use strict';
    const context = document.modelContext;
    const session = window.ProjectSession;
    if (typeof context?.registerTool !== 'function' || !session) return;

    let lifetime;
    function unregister() { lifetime?.abort(); }
    document.addEventListener('crec-project-stale', unregister, { once: true });
    window.addEventListener('pagehide', unregister);
    window.addEventListener('pageshow', event => { if (event.persisted) register(); });

    async function request(path, registration, executionSignal, args) {
        if (session.isStale()) throw new Error('projects-stale');
        const signal = AbortSignal.any([registration.signal, ...(executionSignal ? [executionSignal] : [])]);
        signal.throwIfAborted();
        const url = new URL(path, window.location.origin);
        url.searchParams.set('projectRevision', session.revision);
        const response = await fetch(url, {
            signal, cache: 'no-store',
            ...(args === undefined ? {} : {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-CREC-Request': '1', 'X-CREC-Project': session.revision },
                body: JSON.stringify(args)
            })
        });
        const result = await response.json();
        signal.throwIfAborted();
        if (!response.ok) throw new Error(result.code || 'read-failed');
        if (session.isStale()) throw new Error('projects-stale');
        return result;
    }

    async function register() {
        if (session.isStale()) return;
        unregister();
        const registration = new AbortController();
        lifetime = registration;
        try {
            const tools = await request('/api/ai-tools', registration);
            for (const tool of tools) {
                registration.signal.throwIfAborted();
                const schema = structuredClone(tool.inputSchema);
                delete schema.properties.projectRevision;
                if (schema.required) schema.required = schema.required.filter(key => key !== 'projectRevision');
                schema.additionalProperties = false;
                await context.registerTool({
                    name: tool.name, description: tool.description, inputSchema: schema,
                    annotations: { readOnlyHint: true, untrustedContentHint: true },
                    execute: async (args = {}, options = {}) => {
                        // AI側から別プロジェクトを指定させない。サーバーもページの世代を強制する。
                        const { projectRevision: ignored, ...input } = args;
                        const result = await request('/api/ai-tools/' + encodeURIComponent(tool.name),
                            registration, options.signal, input);
                        if (tool.name === 'get_current_project' && result.revision !== session.revision) {
                            session.markStale();
                            throw new Error('projects-stale');
                        }
                        return result;
                    }
                }, { signal: registration.signal });
            }
        } catch (error) {
            const cancelled = registration.signal.aborted;
            registration.abort();
            if (!cancelled) console.warn('CREC site tools registration failed.', error);
        }
    }
    register();
})();
