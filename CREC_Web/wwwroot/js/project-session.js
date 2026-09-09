/** 画面の世代と未保存入力を管理する。
 * @returns {void} */
(function initializeProjectSession() {
    'use strict';

    // 古い入力の誤送信を防ぐため、表示時点の世代を固定する。
    const revision = document.querySelector('meta[name="crec-project-revision"]').content;
    const originalFetch = window.fetch.bind(window);
    const dirtyInputs = new Set();// 保存した編集欄だけを確認対象から外す。
    let hasProjectChanged = false;
    let hasStartedNavigation = false;// 遷移後は定期確認を止める。
    let activeUploadCount = 0;// アップロード中も破棄確認を行う。

    /** 翻訳文を取得する。
     * @param {string} key 翻訳キー。
     * @returns {string} 翻訳文。未初期化ならキー。 */
    const message = key => typeof t === 'function' ? t(key) : key;

    /** 入力を残して再読み込みを案内する。
     * @returns {void} */
    function markStale() {
        hasProjectChanged = true;
        const notice = document.getElementById('projectChangedNotice');
        if (notice) notice.hidden = false;
    }

    /** 未保存入力とアップロードの破棄を確認する。
     * @returns {boolean} 切り替えてよい場合は true。 */
    function confirmDiscard() {
        for (const input of dirtyInputs) {
            // 消えた編集欄と空のファイル入力は確認対象から外す。
            if (!input.isConnected || (input.type === 'file' && input.files.length === 0))
                dirtyInputs.delete(input);
        }
        return (dirtyInputs.size === 0 && activeUploadCount === 0) || window.confirm(message('projects-discard'));
    }

    /** 保存・破棄済みの入力を確認対象から外す。
     * @param {Element|null|undefined} scope 対象の編集範囲。
     * @returns {void} */
    function saved(scope) {
        for (const input of dirtyInputs) {
            if (scope?.contains(input)) dirtyInputs.delete(input);
        }
    }

    /** 切り替え後の一覧へ移動する。
     * @returns {void} */
    function navigateAfterSwitch() {
        hasStartedNavigation = true;
        window.location.assign('/');
    }

    /** 破棄を確認して再読み込みする。
     * @returns {void} */
    function reload() {
        if (confirmDiscard()) navigateAfterSwitch();
    }

    /** このアプリの API か確認する。
     * @param {URL} target 要求先。
     * @returns {boolean} 同一オリジンの API なら true。 */
    function isLocalApi(target) {
        return target.origin === window.location.origin && target.pathname.toLowerCase().startsWith('/api/');
    }

    /** 動画・ダウンロードの URL に世代を付ける。
     * @param {string|URL} value 元の URL。
     * @returns {string} 絶対 URL。アプリの API には世代を付ける。 */
    function url(value) {
        const target = new URL(value, window.location.href);
        if (isLocalApi(target)) target.searchParams.set('projectRevision', revision);
        return target.href;
    }

    /** API 通信に世代を付け、古い応答の反映を防ぐ。
     * @param {RequestInfo|URL} input 要求先。
     * @param {RequestInit} [options] 通信オプション。
     * @returns {Promise<Response>} 世代確認を通過した応答。 */
    window.fetch = async (input, options) => {
        const target = new URL(input instanceof Request ? input.url : input, window.location.href);
        if (!isLocalApi(target)) return originalFetch(input, options);

        const apiPath = target.pathname.toLowerCase();
        const isManagementRequest = apiPath === '/api/projects' || apiPath.startsWith('/api/projects/');
        if (hasProjectChanged && !isManagementRequest)
            throw new Error(message('projects-stale'));

        const headers = new Headers(options?.headers || (input instanceof Request ? input.headers : undefined));
        headers.set('X-CREC-Project', revision);
        headers.set('X-CREC-Request', '1');
        const response = await originalFetch(input, { ...options, headers, cache: 'no-store' });
        const responseRevision = response.headers.get('X-CREC-Project');
        if (responseRevision && responseRevision !== revision) markStale();

        if (!response.ok && (response.status === 409 || response.status === 503)) {
            // 呼び出し元も本文を読めるよう、複製してエラーを確認する。
            let problem;
            try {
                problem = await response.clone().json();
            } catch {
                // JSON として読めない応答は、そのまま呼び出し元へ渡す。
            }
            if (problem?.code === 'projects-stale') markStale();
            if (problem?.code?.startsWith('projects-')) {
                const error = new Error(message(problem.code));
                error.projectCode = problem.code;
                throw error;
            }
        }
        // 通信中に切り替わった場合も、古い結果を表示しない。
        if (hasProjectChanged && !isManagementRequest)
            throw new Error(message('projects-stale'));
        return response;
    };

    /** 入力の変更を破棄確認の対象にする。
     * @param {Event} event input または change イベント。
     * @returns {void} */
    function trackEdit(event) {
        const input = event.target;
        if (input.closest('#projectSwitchModal, .search-filters')) return;
        const isEditable = event.type === 'input'
            ? input.matches('input, textarea, select') && !input.readOnly
            : input.matches('select, input[type="file"], input[type="checkbox"]');
        if (isEditable) dirtyInputs.add(input);
    }

    /** 別画面での切り替えを検出する。
     * @returns {Promise<void>} 確認完了。 */
    async function checkProject() {
        if (hasProjectChanged || hasStartedNavigation) return;
        try {
            const response = await originalFetch('/api/projects/status', { cache: 'no-store' });
            if (response.ok && (await response.json()).revision !== revision) markStale();
        } catch {
            // 一時的な切断では入力を残し、次回に再確認する。
        }
    }

    window.ProjectSession = Object.freeze({
        revision, url, markStale, confirmDiscard, reload, saved, navigateAfterSwitch,
        /** 画面の世代が古いか返す。
         * @returns {boolean} 切り替え検出済みなら true。 */
        isStale() { return hasProjectChanged; },
        /** アップロードを確認対象に加える。
         * @returns {void} */
        beginUpload() { activeUploadCount++; },
        /** 完了したアップロードを確認対象から外す。
         * @returns {void} */
        endUpload() { activeUploadCount--; }
    });

    document.addEventListener('input', trackEdit);
    document.addEventListener('change', trackEdit);
    /** 閉じた編集画面を確認対象から外す。
     * @param {Event} event 非表示またはリセットの通知。
     * @returns {void} */
    const clearDismissedEditor = event => saved(event.target);
    document.addEventListener('hidden.bs.modal', clearDismissedEditor);
    document.addEventListener('reset', clearDismissedEditor);

    /** 再読み込みボタンと定期確認を有効にする。
     * @returns {void} */
    document.addEventListener('DOMContentLoaded', () => {
        document.getElementById('reloadProjectBtn').addEventListener('click', reload);
        checkProject();
        window.setInterval(checkProject, 2000);
    });
    window.addEventListener('pageshow', checkProject);
    window.addEventListener('focus', checkProject);
})();
