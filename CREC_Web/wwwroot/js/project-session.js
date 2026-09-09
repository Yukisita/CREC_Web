/**
 * 画面が読み込んだプロジェクトの世代を保持し、通信と未保存入力を管理する。
 * @returns {void}
 */
(function initializeProjectSession() {
    'use strict';

    // サーバーが描画した世代を固定する。別画面の切り替えに合わせて更新すると、古い入力を誤送信してしまう。
    const revision = document.querySelector('meta[name="crec-project-revision"]').content;
    const originalFetch = window.fetch.bind(window);// 世代を付けずに状態確認する際にも使う元の通信関数。
    const dirtyInputs = new Set();// 保存した編集欄だけを解除し、別の編集欄の変更は残す。
    let hasProjectChanged = false;
    let hasStartedNavigation = false;// 画面遷移を始めた後は状態確認を止める。
    let activeUploadCount = 0;// アップロード中も切り替え前の破棄確認を必要にする。

    /**
     * 翻訳が利用できる場合はメッセージを取得し、未初期化ならキーを返す。
     * @param {string} key 翻訳キー。
     * @returns {string} 表示するメッセージ。
     */
    function message(key) {
        return typeof t === 'function' ? t(key) : key;
    }

    /**
     * 画面の世代が古くなったことを記録し、入力を残したまま再読み込みを案内する。
     * @returns {void}
     */
    function markStale() {
        hasProjectChanged = true;
        const notice = document.getElementById('projectChangedNotice');
        if (notice) {
            notice.hidden = false;
        }
    }

    /**
     * 未保存入力やアップロードがある場合に、切り替えに伴う破棄を確認する。
     * @returns {boolean} 未保存の操作がない、または利用者が破棄に同意した場合は true。
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

    /**
     * 保存または破棄が完了した編集範囲だけを、未保存入力の一覧から外す。
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

    /**
     * 切り替え後の一覧へ移動し、現在の画面での状態確認を止める。
     * @returns {void}
     */
    function navigateAfterSwitch() {
        hasStartedNavigation = true;
        window.location.assign('/');
    }

    /**
     * 切り替え通知から再読み込みする際に、未保存入力の破棄を確認する。
     * @returns {void} キャンセル時は入力と画面を維持する。
     */
    function reload() {
        if (!confirmDiscard()) {
            return;
        }
        navigateAfterSwitch();
    }

    /**
     * 指定された URL が、このアプリの API を指しているか確認する。
     * @param {URL} targetUrl 解決済みの要求先。
     * @returns {boolean} 同一オリジンの /api/ 配下なら true。
     */
    function isLocalApi(targetUrl) {
        return targetUrl.origin === window.location.origin && targetUrl.pathname.toLowerCase().startsWith('/api/');
    }

    /**
     * ヘッダーを付けられない動画・ダウンロード用の URL に画面の世代を付与する。
     * @param {string|URL} value 元の URL。
     * @returns {string} API の場合は世代付き、それ以外はそのまま解決した絶対 URL。
     */
    function url(value) {
        const targetUrl = new URL(value, window.location.href);
        if (isLocalApi(targetUrl)) {
            targetUrl.searchParams.set('projectRevision', revision);
        }
        return targetUrl.href;
    }

    /**
     * 古い画面から通常の API を利用する処理を止める。
     * @param {boolean} isManagementRequest 状態確認や切り替えの API なら true。
     * @returns {void}
     * @throws {Error} 古い画面から通常の API を利用しようとした場合。
     */
    function ensureCurrentProject(isManagementRequest) {
        if (hasProjectChanged && !isManagementRequest) {
            throw new Error(message('projects-stale'));
        }
    }

    /**
     * 応答の世代と切り替え関連のエラーを調べ、画面に通知できる例外へ変換する。
     * @param {Response} response API からの応答。
     * @returns {Promise<void>} 確認の完了を待つ Promise。元の応答本文は消費しない。
     * @throws {Error} 切り替え関連のエラー。projectCode に翻訳キーを保持する。
     */
    async function checkProjectResponse(response) {
        const responseRevision = response.headers.get('X-CREC-Project');
        if (responseRevision && responseRevision !== revision) {
            markStale();
        }
        if (response.ok || (response.status !== 409 && response.status !== 503)) {
            return;
        }

        // 既存 API のエラー本文は呼び出し元でも使うため、複製した応答から理由だけを読む。
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

    /**
     * 同一アプリの API 通信へ世代を付け、切り替え前の応答が画面へ反映されるのを防ぐ。
     * @param {RequestInfo|URL} input 要求先の URL または Request。
     * @param {RequestInit} [options] 呼び出し元が指定した通信オプション。
     * @returns {Promise<Response>} 世代確認を通過した応答。
     */
    async function fetchForProject(input, options) {
        const targetUrl = new URL(input instanceof Request ? input.url : input, window.location.href);
        if (!isLocalApi(targetUrl)) {
            return originalFetch(input, options);
        }

        const apiPath = targetUrl.pathname.toLowerCase();
        const isManagementRequest = apiPath === '/api/projects' || apiPath.startsWith('/api/projects/');
        ensureCurrentProject(isManagementRequest);

        const headers = new Headers(options?.headers || (input instanceof Request ? input.headers : undefined));
        headers.set('X-CREC-Project', revision);
        headers.set('X-CREC-Request', '1');
        const response = await originalFetch(input, { ...options, headers, cache: 'no-store' });
        await checkProjectResponse(response);

        // 通信中に別の要求や定期確認で切り替えを検出した場合も、古い結果の表示を止める。
        ensureCurrentProject(isManagementRequest);
        return response;
    }

    /**
     * 編集中のテキスト入力を、切り替え前の確認対象として記録する。
     * @param {Event} event document まで伝播した input イベント。
     * @returns {void}
     */
    function trackInput(event) {
        const input = event.target;
        if (input.matches('input, textarea, select') && !input.readOnly
            && !input.closest('#projectSwitchModal, .search-filters')) {
            dirtyInputs.add(input);
        }
    }

    /**
     * 選択肢・ファイル・チェックボックスの変更を確認対象として記録する。
     * @param {Event} event document まで伝播した change イベント。
     * @returns {void}
     */
    function trackChange(event) {
        const input = event.target;
        if (input.matches('select, input[type="file"], input[type="checkbox"]')
            && !input.closest('#projectSwitchModal, .search-filters')) {
            dirtyInputs.add(input);
        }
    }

    /**
     * 閉じたモーダルやリセット済みフォームの入力を確認対象から外す。
     * @param {Event} event モーダルの非表示またはフォームのリセットイベント。
     * @returns {void}
     */
    function clearDismissedEditor(event) {
        saved(event.target);
    }

    /**
     * サーバーの現在の世代を取得し、別画面での切り替えを検出する。
     * @returns {Promise<void>} 確認の完了を待つ Promise。通信失敗時も入力は残す。
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

    /**
     * 再読み込みボタンと、別画面の切り替えを検出する定期確認を有効にする。
     * @returns {void}
     */
    function startProjectMonitoring() {
        document.getElementById('reloadProjectBtn').addEventListener('click', reload);
        checkProject();
        window.setInterval(checkProject, 2000);
    }

    // 既存画面・アップロード処理から利用する公開窓口。世代は書き換えさせない。
    window.ProjectSession = Object.freeze({
        revision, url, markStale, confirmDiscard, reload, saved, navigateAfterSwitch,
        /**
         * この画面の世代が古くなったか確認する。
         * @returns {boolean} プロジェクトが既に切り替わっている場合は true。
         */
        isStale() { return hasProjectChanged; },
        /**
         * アップロードを破棄確認の対象に加える。
         * @returns {void}
         */
        beginUpload() { activeUploadCount++; },
        /**
         * 完了したアップロードを破棄確認の対象から外す。
         * @returns {void}
         */
        endUpload() { activeUploadCount--; }
    });
    window.fetch = fetchForProject;

    document.addEventListener('input', trackInput);
    document.addEventListener('change', trackChange);
    document.addEventListener('hidden.bs.modal', clearDismissedEditor);
    document.addEventListener('reset', clearDismissedEditor);
    document.addEventListener('DOMContentLoaded', startProjectMonitoring);
    window.addEventListener('pageshow', checkProject);
    window.addEventListener('focus', checkProject);
})();
