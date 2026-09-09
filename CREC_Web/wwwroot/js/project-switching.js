/**
 * プロジェクト候補の表示・選択・切り替え確定を初期化する。
 * @returns {void}
 */
document.addEventListener('DOMContentLoaded', function initializeProjectPicker() {
    'use strict';

    const modalElement = document.getElementById('projectSwitchModal');
    const modal = new bootstrap.Modal(modalElement);
    const candidateList = document.getElementById('projectCandidates');// 候補一覧の描画先。
    const statusText = document.getElementById('projectSwitchStatus');// 読み込み状態やエラーの表示先。
    const confirmationPanel = document.getElementById('projectSwitchConfirmation');
    const confirmationName = document.getElementById('projectSwitchTarget');
    const switchButton = document.getElementById('confirmProjectSwitchBtn');
    let selectedProject = null;// 確認パネルに表示中の候補。切り替えを試みた後は必ず破棄する。
    let isSubmitting = false;// 二重送信と、送信中のモーダル終了を防ぐ。

    /**
     * 状態メッセージを更新する。
     * @param {string|null} code 翻訳キー。null の場合は表示を消す。
     * @returns {void}
     */
    function showStatus(code) {
        statusText.textContent = code ? t(code) : '';
    }

    /**
     * 候補の選択を解除して確認パネルを閉じる。
     * @returns {void}
     */
    function resetSelection() {
        selectedProject = null;
        confirmationPanel.hidden = true;
    }

    /**
     * 選択した候補を確認パネルへ表示する。
     * @param {Object} project API から取得した候補。name と location を表示に使用する。
     * @returns {void}
     */
    function selectProject(project) {
        selectedProject = project;
        confirmationName.textContent = project.name + ' (' + project.location + ')';
        confirmationPanel.hidden = false;
        switchButton.focus();
    }

    /**
     * 候補1件の名前・保存場所・選択ボタン・選択できない理由を作成する。
     * @param {Object} project API から取得した候補。
     * @returns {HTMLDivElement} 候補一覧へ追加する要素。
     */
    function createProjectRow(project) {
        const row = document.createElement('div');
        row.className = 'border rounded p-3 mb-2';

        // プロジェクト名やパスを HTML として解釈せず、文字列として表示する。
        const name = document.createElement('strong');
        name.textContent = project.name;
        const location = document.createElement('div');
        location.className = 'small text-muted text-break';
        location.textContent = project.location;

        const selectButton = document.createElement('button');
        selectButton.type = 'button';
        selectButton.className = 'btn btn-outline-primary btn-sm mt-2';
        selectButton.disabled = !!project.errorCode || project.isCurrent;
        selectButton.textContent = t(project.isCurrent ? 'projects-current' : 'projects-select');
        selectButton.addEventListener('click', () => selectProject(project));
        row.append(name, location, selectButton);

        if (project.errorCode) {
            const errorText = document.createElement('div');
            errorText.className = 'small text-danger mt-2';
            errorText.textContent = t(project.errorCode);
            row.append(errorText);
        }
        return row;
    }

    /**
     * 候補の件数と選択可否に応じて案内を更新し、一覧を描画する。
     * @param {Object[]} projects 選択不可の項目を含む候補一覧。
     * @returns {void}
     */
    function renderProjects(projects) {
        showStatus(null);
        if (projects.length === 0) {
            showStatus('projects-empty');
        } else if (projects.every(project => project.errorCode || project.isCurrent)) {
            showStatus('projects-no-selectable');
        }

        for (const project of projects) {
            candidateList.append(createProjectRow(project));
        }
    }

    /**
     * 最新の候補を取得し、前回の選択を破棄して一覧を表示する。
     * @returns {Promise<void>} 一覧取得と描画の完了を待つ Promise。
     */
    async function loadProjects() {
        resetSelection();
        candidateList.replaceChildren();
        showStatus('loading');
        try {
            const response = await fetch('/api/projects');
            if (!response.ok) {
                throw new Error('projects-list-failed');
            }
            const result = await response.json();
            if (result.errorCode) {
                showStatus(result.errorCode);
                return;
            }
            renderProjects(result.projects);
        } catch {
            showStatus(ProjectSession.isStale() ? 'projects-stale' : 'projects-list-failed');
        }
    }

    /**
     * モーダルを表示し、最新の候補の取得を開始する。
     * @returns {void}
     */
    function openPicker() {
        modal.show();
        loadProjects();
    }

    /**
     * 送信中のフラグとボタンの有効状態をまとめて更新する。
     * @param {boolean} submitting 切り替え要求を送信中なら true。
     * @returns {void}
     */
    function setSubmitting(submitting) {
        isSubmitting = submitting;
        modalElement.querySelectorAll('button').forEach(button => { button.disabled = submitting; });
    }

    /**
     * 切り替え結果が確定するまでモーダルを閉じないようにする。
     * @param {Event} event Bootstrap の hide.bs.modal イベント。
     * @returns {void}
     */
    function preventClosingDuringSwitch(event) {
        if (isSubmitting) {
            event.preventDefault();
        }
    }

    /**
     * 未保存入力の破棄を確認し、選択した識別子と画面の世代をサーバーへ送信する。
     * @returns {Promise<void>} 切り替え結果の表示、または新プロジェクトへの遷移開始まで待つ Promise。
     */
    async function switchProject() {
        if (!selectedProject || isSubmitting || !ProjectSession.confirmDiscard()) {
            return;
        }

        setSubmitting(true);
        showStatus('projects-switching');
        try {
            const response = await fetch('/api/projects/switch', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: selectedProject.id, revision: ProjectSession.revision })
            });
            const result = await response.json();
            if (!response.ok) {
                showStatus(result.code || 'projects-switch-failed');
                return;
            }
            if (result.code === 'projects-already-current') {
                showStatus(result.code);
                return;
            }
            ProjectSession.navigateAfterSwitch();
        } catch (error) {
            showStatus(ProjectSession.isStale() ? 'projects-stale' : error.projectCode || 'projects-switch-failed');
        } finally {
            // 成否にかかわらず古い候補を破棄し、次回の操作では一覧を取得し直す。
            setSubmitting(false);
            resetSelection();
            candidateList.replaceChildren();
        }
    }

    document.getElementById('openProjectBtn').addEventListener('click', openPicker);
    document.getElementById('refreshProjectsBtn').addEventListener('click', loadProjects);
    document.getElementById('cancelProjectSelectionBtn').addEventListener('click', resetSelection);
    modalElement.addEventListener('hide.bs.modal', preventClosingDuringSwitch);
    modalElement.addEventListener('hidden.bs.modal', resetSelection);
    switchButton.addEventListener('click', switchProject);
});
