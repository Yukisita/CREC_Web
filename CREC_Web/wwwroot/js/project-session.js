/** 画面の世代と未保存入力を管理する。
 * @returns {void}
 */
(function initializeProjectSession() {
    'use strict';

    // 古い入力の誤送信を防ぐため、画面を読み込んだ時点の世代を固定する。
    const revision = document.querySelector('meta[name="crec-project-revision"]').content;
    const originalFetch = window.fetch.bind(window);// 世代を付けずに状態確認する際にも使う元の通信関数。
    const dirtyInputs = new Set();// 保存した編集欄だけを解除し、別の編集欄の変更は残す。
    let hasProjectChanged = false;
    let hasStartedNavigation = false;// 画面遷移を始めた後は状態確認を止める。
    let activeUploadCount = 0;// アップロード中も切り替え前の破棄確認を必要にする。

    /** 翻訳文を取得する。
     * @param {string} key 翻訳キー。
     * @returns {string} 翻訳文。未初期化ならキー。
     */
    function message(key) {
        return typeof t === 'function' ? t(key) : key;
    }

    /** 入力を残して、世代変更と再読み込みを案内する。
     * @returns {void}
     */
    function markStale() {
        hasProjectChanged = true;
        const notice = document.getElementById('projectChangedNotice');
        if (notice) {
            notice.hidden = false;
        }
    }

    /** 未保存入力とアップロードの破棄を確認する。
     * @returns {boolean} 未保存の操作がない、または破棄に同意した場合は true。
     */
    function confirmDiscard() {
        for (const input of dirtyInputs) {
            // 再描画で消えた欄や、アップロード後に空になったファイル入力は確認対象から外す。
            if (!input.isConnected || (input.type === 'file' && input.files.length === 0)) {
                dirtyInputs.delete(input);
            }
        }
        return (dirtyInputs.size === 0 && activeUploadCount === 0) || window.confirm(message('projects-discard'));
    }

    /** 保存・破棄済みの入力を確認対象から外す。
     * @param {Element|null|undefined} scope 保存済みのフォームや閉じたモーダル。
     * @returns {void}
     */
    function saved(scope) {
        for (const input of dirtyInputs) {
            if (scope?.contains(input)) {
                dirtyInputs.delete(input);
            }
        }
    }

    /** 状態確認を止め、切り替え後の一覧へ移動する。
     * @returns {void}
     */
    function navigateAfterSwitch() {
        hasStartedNavigation = true;
        window.location.assign('/');
    }

    /** 破棄を確認してから画面を再読み込みする。
     * @returns {void} キャンセル時は入力と画面を維持する。
     */
    function reload() {
        if (!confirmDiscard()) {
            return;
        }
        navigateAfterSwitch();
    }

    /** このアプリの API か確認する。
     * @param {URL} targetUrl 解決済みの要求先。
     * @returns {boolean} 同一オリジンの /api/ 配下なら true。
     */
    function isLocalApi(targetUrl) {
        return targetUrl.origin === window.location.origin && targetUrl.pathname.toLowerCase().startsWith('/api/');
    }

    /** 動画・ダウンロード用の URL に画面の世代を付ける。
     * @param {string|URL} value 元の URL。
     * @returns {string} 絶対 URL。アプリの API には世代を付ける。
     */
    function url(value) {
        const targetUrl = new URL(value, window.location.href);
        if (isLocalApi(targetUrl)) {
            targetUrl.searchParams.set('projectRevision', revision);
        }
        return targetUrl.href;
    }

    /** 応答の世代と切り替えエラーを確認する。
     * @param {Response} response API からの応答。
     * @returns {Promise<void>} 確認完了。元の応答本文は消費しない。
     * @throws {Error} 切り替えエラー。projectCode は翻訳キー。
     */
    async function checkProjectResponse(response) {
        const responseRevision = response.headers.get('X-CREC-Project');
        if (responseRevision && responseRevision !== revision) {
            markStale();
        }
        if (response.ok || (response.status !== 409 && response.status !== 503)) {
            return;
        }

        // 呼び出し元が本文を読めるよう、複製した応答から理由を読む。
        let problem;
        try {
            problem = await response.clone().json();
        } catch {
            return;
        }

        if (problem?.code === 'projects-stale') {
            markStale();
        }
        if (!problem?.code?.startsWith('projects-')) {
            return;
        }

        const error = new Error(message(problem.code));
        error.projectCode = problem.code;
        throw error;
    }

    /** API 通信に世代を付け、古い応答の反映を防ぐ。
     * @param {RequestInfo|URL} input 要求先の URL または Request。
     * @param {RequestInit} [options] 通信オプション。
     * @returns {Promise<Response>} 世代確認を通過した応答。
     */
    async function fetchForProject(input, options) {
        const targetUrl = new URL(input instanceof Request ? input.url : input, window.location.href);
        if (!isLocalApi(targetUrl)) {
            return originalFetch(input, options);
        }

        const apiPath = targetUrl.pathname.toLowerCase();
        const isManagementRequest = apiPath === '/api/projects' || apiPath.startsWith('/api/projects/');
        if (hasProjectChanged && !isManagementRequest) {
            throw new Error(message('projects-stale'));
        }

        const headers = new Headers(options?.headers || (input instanceof Request ? input.headers : undefined));
        headers.set('X-CREC-Project', revision);
        headers.set('X-CREC-Request', '1');
        const response = await originalFetch(input, { ...options, headers, cache: 'no-store' });
        await checkProjectResponse(response);

        // 通信中に切り替わった場合も、古い結果を表示しない。
        if (hasProjectChanged && !isManagementRequest) {
            throw new Error(message('projects-stale'));
        }
        return response;
    }

    /** 編集中の入力を破棄確認の対象にする。
     * @param {Event} event 入力イベント。
     * @returns {void}
     */
    function trackInput(event) {
        const input = event.target;
        if (input.matches('input, textarea, select') && !input.readOnly
            && !input.closest('#projectSwitchModal, .search-filters')) {
            dirtyInputs.add(input);
        }
    }

    /** 選択・ファイルの変更を破棄確認の対象にする。
     * @param {Event} event 変更イベント。
     * @returns {void}
     */
    function trackChange(event) {
        const input = event.target;
        if (input.matches('select, input[type="file"], input[type="checkbox"]')
            && !input.closest('#projectSwitchModal, .search-filters')) {
            dirtyInputs.add(input);
        }
    }

    /** 閉じた編集画面の入力を確認対象から外す。
     * @param {Event} event モーダルの非表示またはフォームのリセットイベント。
     * @returns {void}
     */
    function clearDismissedEditor(event) {
        saved(event.target);
    }

    /** 現在の世代を取得し、別画面での切り替えを検出する。
     * @returns {Promise<void>} 確認完了。通信失敗時も入力は残す。
     */
    async function checkProject() {
        if (hasProjectChanged || hasStartedNavigation) {
            return;
        }

        try {
            const response = await originalFetch('/api/projects/status', { cache: 'no-store' });
            if (response.ok && (await response.json()).revision !== revision) {
                markStale();
            }
        } catch {
            // 一時的な切断では画面を変更せず、次回の定期確認で再試行する。
        }
    }

    // 既存画面・アップロード処理から利用する公開窓口。世代は書き換えさせない。
    window.ProjectSession = Object.freeze({
        revision, url, markStale, confirmDiscard, reload, saved, navigateAfterSwitch,
        /** 画面の世代が古いか返す。
         * @returns {boolean} 切り替えを検出済みなら true。
         */
        isStale() { return hasProjectChanged; },
        /** アップロードを破棄確認の対象に加える。
         * @returns {void}
         */
        beginUpload() { activeUploadCount++; },
        /** 完了したアップロードを確認対象から外す。
         * @returns {void}
         */
        endUpload() { activeUploadCount--; }
    });
    window.fetch = fetchForProject;

    document.addEventListener('input', trackInput);
    document.addEventListener('change', trackChange);
    document.addEventListener('hidden.bs.modal', clearDismissedEditor);
    document.addEventListener('reset', clearDismissedEditor);
    /** 再読み込みボタンと定期確認を有効にする。
     * @returns {void}
     */
    document.addEventListener('DOMContentLoaded', () => {
        document.getElementById('reloadProjectBtn').addEventListener('click', reload);
        checkProject();
        window.setInterval(checkProject, 2000);
    });
    window.addEventListener('pageshow', checkProject);
    window.addEventListener('focus', checkProject);
})();
