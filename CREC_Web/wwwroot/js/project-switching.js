/** プロジェクト選択画面を初期化する。
 * @returns {void} */
document.addEventListener('DOMContentLoaded', function initializeProjectPicker() {
    'use strict';

    const modalElement = document.getElementById('projectSwitchModal');
    const modal = new bootstrap.Modal(modalElement);
    const candidateList = document.getElementById('projectCandidates');
    const statusText = document.getElementById('projectSwitchStatus');
    const confirmationPanel = document.getElementById('projectSwitchConfirmation');
    const confirmationName = document.getElementById('projectSwitchTarget');
    const switchButton = document.getElementById('confirmProjectSwitchBtn');
    let selectedProject = null;// 確認中の候補。送信後は破棄する。
    let isSubmitting = false;// 二重送信と送信中の画面終了を防ぐ。

    /** 状態メッセージを更新する。
     * @param {string|null} code 翻訳キー。null なら表示を消す。
     * @returns {void} */
    const showStatus = code => { statusText.textContent = code ? t(code) : ''; };

    /** 選択を解除して確認パネルを閉じる。
     * @returns {void} */
    function resetSelection() {
        selectedProject = null;
        confirmationPanel.hidden = true;
    }

    /** 候補一覧と、選択できない理由を描画する。
     * @param {Object[]} projects API の候補一覧。
     * @returns {void} */
    function renderProjects(projects) {
        showStatus(null);
        if (projects.length === 0) {
            showStatus('projects-empty');
        } else if (projects.every(project => project.errorCode || project.isCurrent)) {
            showStatus('projects-no-selectable');
        }

        for (const project of projects) {
            const row = document.createElement('div');
            row.className = 'border rounded p-3 mb-2';

            // 名前やパスを HTML として解釈させない。
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
            /** この候補を確認パネルへ表示する。
             * @returns {void} */
            selectButton.addEventListener('click', () => {
                selectedProject = project;
                confirmationName.textContent = project.name + ' (' + project.location + ')';
                confirmationPanel.hidden = false;
                switchButton.focus();
            });
            row.append(name, location, selectButton);

            if (project.errorCode) {
                const errorText = document.createElement('div');
                errorText.className = 'small text-danger mt-2';
                errorText.textContent = t(project.errorCode);
                row.append(errorText);
            }
            candidateList.append(row);
        }
    }

    /** 前回の選択を破棄し、最新の候補を取得する。
     * @returns {Promise<void>} 一覧の取得・描画の完了。 */
    async function loadProjects() {
        resetSelection();
        candidateList.replaceChildren();
        showStatus('loading');
        try {
            const response = await fetch('/api/projects');
            if (!response.ok) throw new Error('projects-list-failed');
            const result = await response.json();
            if (result.errorCode) showStatus(result.errorCode);
            else renderProjects(result.projects);
        } catch {
            showStatus(ProjectSession.isStale() ? 'projects-stale' : 'projects-list-failed');
        }
    }

    /** 未保存入力の破棄を確認し、選択した候補へ切り替える。
     * @returns {Promise<void>} 結果表示または画面遷移の開始。 */
    async function switchProject() {
        if (!selectedProject || isSubmitting || !ProjectSession.confirmDiscard())
            return;

        isSubmitting = true;
        modalElement.querySelectorAll('button').forEach(button => { button.disabled = true; });
        showStatus('projects-switching');
        try {
            const response = await fetch('/api/projects/switch', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ id: selectedProject.id, revision: ProjectSession.revision })
            });
            const result = await response.json();
            if (!response.ok || result.code === 'projects-already-current') {
                showStatus(result.code || 'projects-switch-failed');
                return;
            }
            ProjectSession.navigateAfterSwitch();
        } catch (error) {
            showStatus(ProjectSession.isStale() ? 'projects-stale' : error.projectCode || 'projects-switch-failed');
        } finally {
            // 次の操作では、成否にかかわらず一覧を取得し直す。
            isSubmitting = false;
            modalElement.querySelectorAll('button').forEach(button => { button.disabled = false; });
            resetSelection();
            candidateList.replaceChildren();
        }
    }

    /** 選択画面を開き、候補を取得する。
     * @returns {void} */
    document.getElementById('openProjectBtn').addEventListener('click', () => {
        modal.show();
        loadProjects();
    });
    document.getElementById('refreshProjectsBtn').addEventListener('click', loadProjects);
    document.getElementById('cancelProjectSelectionBtn').addEventListener('click', resetSelection);

    /** 切り替え結果が確定するまで画面を閉じない。
     * @param {Event} event モーダルの非表示イベント。
     * @returns {void} */
    modalElement.addEventListener('hide.bs.modal', event => {
        if (isSubmitting) event.preventDefault();
    });
    modalElement.addEventListener('hidden.bs.modal', resetSelection);
    switchButton.addEventListener('click', switchProject);
});
