/*
CREC Web - Data File Manager UI
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

class DataFileManager {
    constructor(container, collectionId) {
        this.container = container;
        this.collectionId = collectionId;
        this.currentPath = '';
        this.entries = [];
        this.loadSequence = 0;
    }

    async initialize(container = this.container) {
        this.container = container;
        this.renderShell();
        await this.loadDirectory(this.currentPath);
    }

    renderShell() {
        this.container.classList.remove('loading');
        this.container.innerHTML = `
            <div class="data-file-manager">
                <div class="data-file-manager-toolbar">
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-data-action="up" title="${escapeHtml(t('data-up'))}" aria-label="${escapeHtml(t('data-up'))}">
                        <i class="bi bi-arrow-up"></i>
                    </button>
                    <button type="button" class="btn btn-sm btn-outline-primary" data-data-action="create-folder">
                        <i class="bi bi-folder-plus"></i> ${escapeHtml(t('data-new-folder'))}
                    </button>
                    <button type="button" class="btn btn-sm btn-outline-primary" data-data-action="upload-file">
                        <i class="bi bi-upload"></i> ${escapeHtml(t('data-upload-file'))}
                    </button>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-data-action="refresh">
                        <i class="bi bi-arrow-clockwise"></i> ${escapeHtml(t('data-refresh'))}
                    </button>
                    <input type="file" data-data-file-input hidden>
                    <nav class="data-file-manager-breadcrumb" aria-label="${escapeHtml(t('data-current-folder'))}">
                        <ol class="breadcrumb mb-0" data-data-breadcrumb></ol>
                    </nav>
                </div>
                <div class="data-file-manager-status" data-data-status aria-live="polite"></div>
                <div data-data-list></div>
                <div class="modal fade" data-data-dialog tabindex="-1" aria-hidden="true">
                    <div class="modal-dialog">
                        <div class="modal-content">
                            <div class="modal-header">
                                <h5 class="modal-title" data-data-dialog-title></h5>
                                <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="${escapeHtml(t('close'))}"></button>
                            </div>
                            <div class="modal-body">
                                <p data-data-dialog-message hidden></p>
                                <div data-data-dialog-input-container hidden>
                                    <label class="form-label" data-data-dialog-label></label>
                                    <input type="text" class="form-control" data-data-dialog-input maxlength="255">
                                    <div class="invalid-feedback">${escapeHtml(t('data-name-required'))}</div>
                                </div>
                            </div>
                            <div class="modal-footer">
                                <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">${escapeHtml(t('cancel'))}</button>
                                <button type="button" class="btn btn-primary" data-data-dialog-confirm>${escapeHtml(t('save'))}</button>
                            </div>
                        </div>
                    </div>
                </div>
            </div>`;

        this.container.querySelector('.data-file-manager').addEventListener('click', event => {
            this.handleClick(event).catch(error => this.showError(error));
        });

        const fileInput = this.container.querySelector('[data-data-file-input]');
        fileInput.addEventListener('change', async () => {
            const file = fileInput.files?.[0];
            if (!file) return;
            try {
                await this.uploadFile(file);
            } catch (error) {
                this.showError(error, t('data-upload-error'));
            } finally {
                fileInput.value = '';
            }
        });
    }

    async handleClick(event) {
        const actionElement = event.target.closest('[data-data-action]');
        if (!actionElement || !this.container.contains(actionElement)) return;

        const action = actionElement.dataset.dataAction;
        const path = actionElement.dataset.path || '';
        switch (action) {
            case 'open-folder':
            case 'breadcrumb':
                await this.loadDirectory(path);
                break;
            case 'up':
                await this.loadDirectory(this.getParentPath());
                break;
            case 'refresh':
                await this.loadDirectory(this.currentPath);
                break;
            case 'create-folder':
                await this.createFolder();
                break;
            case 'upload-file':
                this.container.querySelector('[data-data-file-input]').click();
                break;
            case 'rename':
                await this.renameEntry(path);
                break;
            case 'delete':
                await this.deleteEntry(path);
                break;
        }
    }

    async loadDirectory(path) {
        const sequence = ++this.loadSequence;
        this.setLoading(true);
        this.clearStatus();
        try {
            const query = path ? `?path=${encodeURIComponent(path)}` : '';
            const response = await fetch(`${this.baseUrl}${query}`);
            const listing = await this.readJsonResponse(response);
            if (sequence !== this.loadSequence) return;

            this.currentPath = listing.currentPath || '';
            this.entries = Array.isArray(listing.entries) ? listing.entries : [];
            this.renderBreadcrumb();
            this.renderEntries();
        } catch (error) {
            if (sequence !== this.loadSequence) return;
            this.showError(error, t('data-load-error'));
            this.renderEntries();
        } finally {
            if (sequence === this.loadSequence) this.setLoading(false);
        }
    }

    async createFolder() {
        const name = await this.showInputDialog(
            t('data-new-folder'),
            t('data-create-folder-prompt'));
        if (name === null) return;

        await this.request(`${this.baseUrl}/folders`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ parentPath: this.currentPath, name })
        });
        await this.loadDirectory(this.currentPath);
        this.showSuccess(t('data-create-folder-success'));
    }

    async renameEntry(path) {
        const entry = this.entries.find(item => item.relativePath === path);
        if (!entry) return;

        const newName = await this.showInputDialog(
            t('data-rename'),
            t('data-rename-prompt'),
            entry.name);
        if (newName === null || newName === entry.name) return;

        let confirmExtensionChange = false;
        if (entry.entryType === 'file' && this.hasExtensionChanged(entry.name, newName)) {
            const oldExtension = this.getExtension(entry.name) || t('data-no-extension');
            const newExtension = this.getExtension(newName) || t('data-no-extension');
            const warning = t('data-extension-change-confirm')
                .replace('{oldExtension}', oldExtension)
                .replace('{newExtension}', newExtension);
            const confirmed = await this.showConfirmDialog(
                t('data-extension-change-title'),
                warning,
                t('data-rename'));
            if (!confirmed) return;
            confirmExtensionChange = true;
        }

        await this.request(`${this.baseUrl}/entries`, {
            method: 'PATCH',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ path, newName, confirmExtensionChange })
        });
        await this.loadDirectory(this.currentPath);
        this.showSuccess(t('data-rename-success'));
    }

    async deleteEntry(path) {
        const entry = this.entries.find(item => item.relativePath === path);
        if (!entry) return;

        const messageKey = entry.entryType === 'directory'
            ? 'data-delete-folder-confirm'
            : 'data-delete-file-confirm';
        const message = t(messageKey).replace('{name}', entry.name);
        if (!await this.showConfirmDialog(t('data-delete'), message, t('data-delete'), true)) return;

        await this.request(`${this.baseUrl}/entries?path=${encodeURIComponent(path)}`, {
            method: 'DELETE'
        }, false);
        await this.loadDirectory(this.currentPath);
        this.showSuccess(t('data-delete-success'));
    }

    async uploadFile(file) {
        const formData = new FormData();
        formData.append('file', file);
        this.setLoading(true);
        this.showInfo(t('uploading'));
        try {
            await this.request(
                `${this.baseUrl}/files?path=${encodeURIComponent(this.currentPath)}`,
                { method: 'POST', body: formData });
            await this.loadDirectory(this.currentPath);
            this.showSuccess(t('data-upload-success'));
        } finally {
            this.setLoading(false);
        }
    }

    renderBreadcrumb() {
        const breadcrumb = this.container.querySelector('[data-data-breadcrumb]');
        const segments = this.currentPath ? this.currentPath.split('/') : [];
        const items = [{ name: t('data-root'), path: '' }];
        let accumulatedPath = '';
        for (const segment of segments) {
            accumulatedPath = accumulatedPath ? `${accumulatedPath}/${segment}` : segment;
            items.push({ name: segment, path: accumulatedPath });
        }

        breadcrumb.innerHTML = items.map((item, index) => {
            const isCurrent = index === items.length - 1;
            if (isCurrent) {
                return `<li class="breadcrumb-item active" aria-current="page">${escapeHtml(item.name)}</li>`;
            }
            return `<li class="breadcrumb-item"><button type="button" data-data-action="breadcrumb" data-path="${escapeHtml(item.path)}">${escapeHtml(item.name)}</button></li>`;
        }).join('');

        const upButton = this.container.querySelector('[data-data-action="up"]');
        upButton.disabled = !this.currentPath;
    }

    renderEntries() {
        const list = this.container.querySelector('[data-data-list]');
        if (!this.entries.length) {
            list.innerHTML = `<div class="data-file-manager-empty"><i class="bi bi-folder2-open fs-3 d-block mb-2"></i>${escapeHtml(t('data-empty-folder'))}</div>`;
            return;
        }

        const rows = this.entries.map(entry => {
            const isDirectory = entry.entryType === 'directory';
            const icon = isDirectory ? 'bi-folder-fill text-warning' : 'bi-file-earmark';
            const nameAction = isDirectory ? 'open-folder' : '';
            const nameElement = isDirectory
                ? `<button type="button" class="data-file-manager-name" data-entry-type="directory" data-data-action="${nameAction}" data-path="${escapeHtml(entry.relativePath)}">${escapeHtml(entry.name)}</button>`
                : `<span class="data-file-manager-name">${escapeHtml(entry.name)}</span>`;
            const downloadUrl = isDirectory
                ? `${this.baseUrl}/folders/archive?path=${encodeURIComponent(entry.relativePath)}`
                : `${this.baseUrl}/files?path=${encodeURIComponent(entry.relativePath)}`;

            return `
                <tr>
                    <td><i class="bi ${icon} me-2" aria-hidden="true"></i>${nameElement}</td>
                    <td class="data-file-manager-optional-column">${escapeHtml(isDirectory ? t('data-folder') : t('data-file'))}</td>
                    <td class="data-file-manager-optional-column text-end">${isDirectory ? '—' : escapeHtml(this.formatSize(entry.size))}</td>
                    <td class="data-file-manager-optional-column">${escapeHtml(this.formatDate(entry.lastModifiedUtc))}</td>
                    <td>
                        <div class="data-file-manager-actions">
                            <a href="${escapeHtml(downloadUrl)}" class="btn btn-sm btn-outline-primary" title="${escapeHtml(isDirectory ? t('data-download-folder') : t('data-download'))}" aria-label="${escapeHtml(isDirectory ? t('data-download-folder') : t('data-download'))}">
                                <i class="bi bi-download"></i>
                            </a>
                            <button type="button" class="btn btn-sm btn-outline-secondary" data-data-action="rename" data-path="${escapeHtml(entry.relativePath)}" title="${escapeHtml(t('data-rename'))}" aria-label="${escapeHtml(t('data-rename'))}">
                                <i class="bi bi-pencil"></i>
                            </button>
                            <button type="button" class="btn btn-sm btn-outline-danger" data-data-action="delete" data-path="${escapeHtml(entry.relativePath)}" title="${escapeHtml(t('data-delete'))}" aria-label="${escapeHtml(t('data-delete'))}">
                                <i class="bi bi-trash"></i>
                            </button>
                        </div>
                    </td>
                </tr>`;
        }).join('');

        list.innerHTML = `
            <div class="table-responsive border rounded">
                <table class="table table-hover align-middle data-file-manager-table">
                    <thead class="table-light">
                        <tr>
                            <th>${escapeHtml(t('data-name'))}</th>
                            <th class="data-file-manager-optional-column">${escapeHtml(t('data-type'))}</th>
                            <th class="data-file-manager-optional-column text-end">${escapeHtml(t('data-size'))}</th>
                            <th class="data-file-manager-optional-column">${escapeHtml(t('data-modified'))}</th>
                            <th class="text-end">${escapeHtml(t('data-actions'))}</th>
                        </tr>
                    </thead>
                    <tbody>${rows}</tbody>
                </table>
            </div>`;
    }

    async request(url, options, expectJson = true) {
        const response = await fetch(url, options);
        if (!response.ok) {
            throw await this.createResponseError(response);
        }
        if (!expectJson || response.status === 204) return null;
        return response.json();
    }

    async readJsonResponse(response) {
        if (!response.ok) throw await this.createResponseError(response);
        return response.json();
    }

    async createResponseError(response) {
        const problem = await response.json().catch(() => null);
        if (problem?.code === 'extension_change_confirmation_required') {
            const error = new Error(t('data-extension-confirmation-required'));
            error.status = response.status;
            error.code = problem.code;
            return error;
        }
        const localizedErrorKeys = {
            400: 'data-invalid-request',
            403: 'data-access-denied',
            404: 'data-entry-not-found',
            409: 'data-conflict'
        };
        const localizedKey = localizedErrorKeys[response.status];
        const message = localizedKey
            ? t(localizedKey)
            : (problem?.title || `${t('data-operation-error')} (HTTP ${response.status})`);
        const error = new Error(message);
        error.status = response.status;
        return error;
    }

    showInputDialog(title, label, initialValue = '') {
        return this.showDialog({ title, label, initialValue, requiresInput: true });
    }

    showConfirmDialog(title, message, confirmText, isDanger = false) {
        return this.showDialog({
            title,
            message,
            confirmText,
            isDanger,
            requiresInput: false
        });
    }

    showDialog({
        title,
        label = '',
        message = '',
        initialValue = '',
        confirmText = '',
        isDanger = false,
        requiresInput
    }) {
        const modalElement = this.container.querySelector('[data-data-dialog]');
        const titleElement = modalElement.querySelector('[data-data-dialog-title]');
        const messageElement = modalElement.querySelector('[data-data-dialog-message]');
        const inputContainer = modalElement.querySelector('[data-data-dialog-input-container]');
        const labelElement = modalElement.querySelector('[data-data-dialog-label]');
        const inputElement = modalElement.querySelector('[data-data-dialog-input]');
        const confirmButton = modalElement.querySelector('[data-data-dialog-confirm]');
        const modal = bootstrap.Modal.getOrCreateInstance(modalElement);

        titleElement.textContent = title;
        messageElement.textContent = message;
        messageElement.hidden = requiresInput;
        inputContainer.hidden = !requiresInput;
        labelElement.textContent = label;
        inputElement.value = initialValue;
        inputElement.classList.remove('is-invalid');
        confirmButton.textContent = requiresInput ? t('save') : confirmText;
        confirmButton.classList.toggle('btn-primary', requiresInput || !isDanger);
        confirmButton.classList.toggle('btn-danger', !requiresInput && isDanger);

        return new Promise(resolve => {
            let result = null;
            confirmButton.onclick = () => {
                if (requiresInput) {
                    const value = inputElement.value;
                    if (!value.trim()) {
                        inputElement.classList.add('is-invalid');
                        inputElement.focus();
                        return;
                    }
                    result = value;
                } else {
                    result = true;
                }
                modal.hide();
            };

            modalElement.addEventListener('hidden.bs.modal', () => resolve(result), { once: true });
            if (requiresInput) {
                modalElement.addEventListener('shown.bs.modal', () => {
                    inputElement.focus();
                    inputElement.select();
                }, { once: true });
            }
            modal.show();
        });
    }

    get baseUrl() {
        return `/api/collections/${encodeURIComponent(this.collectionId)}/data`;
    }

    getParentPath() {
        if (!this.currentPath) return '';
        const segments = this.currentPath.split('/');
        segments.pop();
        return segments.join('/');
    }

    hasExtensionChanged(oldName, newName) {
        return this.getExtension(oldName).toLowerCase() !== this.getExtension(newName).toLowerCase();
    }

    getExtension(name) {
        const lastDotIndex = name.lastIndexOf('.');
        if (lastDotIndex < 0 || lastDotIndex === name.length - 1) return '';
        return name.slice(lastDotIndex);
    }

    setLoading(isLoading) {
        this.container.querySelectorAll('button, input').forEach(element => {
            if (element.dataset.dataAction === 'up' && !this.currentPath) return;
            element.disabled = isLoading;
        });
        const list = this.container.querySelector('[data-data-list]');
        if (isLoading && !this.entries.length) {
            list.innerHTML = `<div class="loading"><div class="spinner-border text-primary" role="status"><span class="visually-hidden">${escapeHtml(t('data-loading'))}</span></div></div>`;
        }
    }

    showSuccess(message) {
        const status = this.container.querySelector('[data-data-status]');
        status.innerHTML = `<div class="alert alert-success py-2" role="status">${escapeHtml(message)}</div>`;
    }

    showInfo(message) {
        const status = this.container.querySelector('[data-data-status]');
        status.innerHTML = `<div class="alert alert-info py-2" role="status"><span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>${escapeHtml(message)}</div>`;
    }

    showError(error, fallbackMessage = t('data-operation-error')) {
        console.error('Data file manager error:', error);
        const status = this.container.querySelector('[data-data-status]');
        const message = error?.message || fallbackMessage;
        status.innerHTML = `<div class="alert alert-danger py-2" role="alert">${escapeHtml(message)}</div>`;
    }

    clearStatus() {
        this.container.querySelector('[data-data-status]').innerHTML = '';
    }

    formatSize(size) {
        if (!Number.isFinite(size)) return '—';
        if (size < 1024) return `${size} B`;
        const units = ['KB', 'MB', 'GB', 'TB'];
        let value = size / 1024;
        let unitIndex = 0;
        while (value >= 1024 && unitIndex < units.length - 1) {
            value /= 1024;
            unitIndex++;
        }
        return `${value.toFixed(value >= 10 ? 0 : 1)} ${units[unitIndex]}`;
    }

    formatDate(value) {
        if (!value) return '—';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString();
    }
}

window.DataFileManager = DataFileManager;
