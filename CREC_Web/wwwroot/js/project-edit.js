/** 既存の編集画面を、新規作成にも利用する。 */
(async function initProjectEditPage() {
    'use strict';

    const form = document.getElementById('projectEditForm');
    const isNewProject = form.dataset.newProject === 'true';
    const saveButton = document.getElementById('projectEditSaveBtn');
    const errorNotice = document.getElementById('projectEditError');
    let isSaving = false;

    const fields = {
        projectName: 'editProjectName',
        collectionNameLabel: 'editCollectionNameLabel',
        uuidLabel: 'editUUIDLabel',
        managementCodeLabel: 'editManagementCodeLabel',
        categoryLabel: 'editCategoryLabel',
        firstTagLabel: 'editTag1Label',
        secondTagLabel: 'editTag2Label',
        thirdTagLabel: 'editTag3Label'
    };

    function showError(message) {
        errorNotice.textContent = message;
        errorNotice.hidden = false;
    }

    // 新規作成では既存の値や、ブラウザーが復元した入力を引き継がない。
    if (isNewProject) {
        form.reset();
    } else {
        saveButton.disabled = true;
        try {
            const settings = await loadProjectSettings();
            const values = {
                projectName: settings.projectName,
                collectionNameLabel: settings.objectNameLabel,
                uuidLabel: settings.uuidName,
                managementCodeLabel: settings.managementCodeName,
                categoryLabel: settings.categoryName,
                firstTagLabel: settings.tag1Name,
                secondTagLabel: settings.tag2Name,
                thirdTagLabel: settings.tag3Name
            };
            for (const [key, id] of Object.entries(fields))
                document.getElementById(id).value = values[key] || '';
            document.getElementById('editProjectDataPath').value = settings.projectDataPath || '';
            saveButton.disabled = false;
        } catch {
            showError(t('edit-project-error'));
            return;
        }
    }

    form.addEventListener('submit', async event => {
        event.preventDefault();
        if (isSaving || !form.reportValidity()) return;
        const settings = Object.fromEntries(Object.entries(fields)
            .map(([key, id]) => [key, document.getElementById(id).value]));
        if (isNewProject && !settings.projectName.trim()) {
            showError(t('projects-name-required'));
            return;
        }

        isSaving = true;
        saveButton.disabled = true;
        errorNotice.hidden = true;
        try {
            const response = await fetch(isNewProject ? '/api/projects/create' : '/api/ProjectSettings', {
                method: isNewProject ? 'POST' : 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(isNewProject ? { settings, revision: ProjectSession.revision } : settings)
            });
            if (!response.ok) {
                if (isNewProject) {
                    const problem = await response.json();
                    throw new Error(t(problem.code || 'projects-create-failed'));
                }
                throw new Error(await response.text());
            }

            ProjectSession.saved(form);
            if (isNewProject) {
                ProjectSession.navigateAfterSwitch();
            } else {
                alert(t('edit-project-success'));
                window.location.reload();
            }
        } catch (error) {
            showError(error.projectCode ? t(error.projectCode)
                : (isNewProject ? t('projects-create-error') : t('edit-project-error')) + ': ' + error.message);
        } finally {
            isSaving = false;
            saveButton.disabled = false;
        }
    });
})();
