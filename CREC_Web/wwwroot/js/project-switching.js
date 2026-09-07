(() => {
    'use strict';
    document.addEventListener('DOMContentLoaded', () => {
        const modalElement = document.getElementById('projectSwitchModal');
        const modal = new bootstrap.Modal(modalElement);
        const list = document.getElementById('projectCandidates');
        const status = document.getElementById('projectSwitchStatus');
        const login = document.getElementById('projectAdminLogin');
        const token = document.getElementById('projectAdminToken');
        const confirmPanel = document.getElementById('projectSwitchConfirmation');
        const confirmName = document.getElementById('projectSwitchTarget');
        const switchButton = document.getElementById('confirmProjectSwitchBtn');
        let selected = null;
        let submitting = false;

        function showStatus(code) { status.textContent = code ? t(code) : ''; }
        function resetSelection() { selected = null; confirmPanel.hidden = true; }

        async function load() {
            resetSelection();
            list.replaceChildren();
            showStatus('loading');
            login.hidden = true;
            try {
                const response = await fetch('/api/projects');
                if (response.status === 401) { login.hidden = false; showStatus('projects-auth-help'); return; }
                if (!response.ok) throw new Error('projects-list-failed');
                const result = await response.json();
                if (result.errorCode) { showStatus(result.errorCode); return; }
                showStatus(!result.projects.length ? 'projects-empty'
                    : result.projects.every(project => project.errorCode || project.isCurrent) ? 'projects-no-selectable' : null);
                for (const project of result.projects) {
                    const row = document.createElement('div');
                    row.className = 'border rounded p-3 mb-2';
                    const name = document.createElement('strong');
                    name.textContent = project.name;
                    const location = document.createElement('div');
                    location.className = 'small text-muted text-break';
                    location.textContent = project.location;
                    const button = document.createElement('button');
                    button.type = 'button';
                    button.className = 'btn btn-outline-primary btn-sm mt-2';
                    button.disabled = !!project.errorCode || project.isCurrent;
                    button.textContent = t(project.isCurrent ? 'projects-current' : 'projects-select');
                    button.addEventListener('click', () => {
                        selected = project;
                        confirmName.textContent = `${project.name} (${project.location})`;
                        confirmPanel.hidden = false;
                        switchButton.focus();
                    });
                    row.append(name, location, button);
                    if (project.errorCode) {
                        const error = document.createElement('div');
                        error.className = 'small text-danger mt-2';
                        error.textContent = t(project.errorCode);
                        row.append(error);
                    }
                    list.append(row);
                }
            } catch { showStatus(ProjectSession.isStale() ? 'projects-stale' : 'projects-list-failed'); }
        }

        document.getElementById('openProjectBtn').addEventListener('click', () => { modal.show(); load(); });
        document.getElementById('refreshProjectsBtn').addEventListener('click', load);
        document.getElementById('cancelProjectSelectionBtn').addEventListener('click', resetSelection);
        modalElement.addEventListener('hide.bs.modal', event => { if (submitting) event.preventDefault(); });
        modalElement.addEventListener('hidden.bs.modal', () => { resetSelection(); token.value = ''; });
        login.addEventListener('submit', async event => {
            event.preventDefault();
            const button = login.querySelector('button');
            button.disabled = true;
            try {
                const response = await fetch('/api/projects/login', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ token: token.value.trim() })
                });
                token.value = '';
                if (!response.ok) { showStatus('projects-unauthorized'); return; }
                await load();
            } catch { showStatus('projects-list-failed'); }
            finally { button.disabled = false; }
        });
        switchButton.addEventListener('click', async () => {
            if (!selected || submitting || !ProjectSession.confirmDiscard()) return;
            submitting = true;
            modalElement.querySelectorAll('button').forEach(button => { button.disabled = true; });
            showStatus('projects-switching');
            try {
                const response = await fetch('/api/projects/switch', {
                    method: 'POST', headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ id: selected.id, revision: ProjectSession.revision })
                });
                const result = await response.json();
                if (!response.ok) {
                    if (response.status === 401) login.hidden = false;
                    showStatus(result.code || 'projects-switch-failed');
                    return;
                }
                if (result.code === 'projects-already-current') { showStatus(result.code); return; }
                ProjectSession.navigateAfterSwitch();
            } catch (error) {
                showStatus(ProjectSession.isStale() ? 'projects-stale' : error.projectCode || 'projects-switch-failed');
            } finally {
                submitting = false;
                modalElement.querySelectorAll('button').forEach(button => { button.disabled = false; });
                // Invalidate the selection after every attempt; the next attempt needs a fresh listing.
                resetSelection();
                list.replaceChildren();
            }
        });
    });
})();
