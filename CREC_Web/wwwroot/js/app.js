/*
CREC Web - Frontend Application
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

// グローバル変数
let currentPage = 1;
let currentPageSize = 20;
let currentSearchCriteria = {};
let currentLanguage = localStorage.getItem('crec_language') || 'ja'; // 'ja' は日本語、'en' は英語 (load from localStorage)
let currentViewMode = 'table'; // 'table' or 'grid'
let lastIsMobile; // 画面サイズ変更前のMobile検知
let projectSettings = {
    projectName: '',
    projectDataPath: '',
    objectNameLabel:'Collection Name',
    uuidName: 'UUID',
    managementCodeName: 'MC',
    categoryName: 'カテゴリ',
    tag1Name: 'タグ 1',
    tag2Name: 'タグ 2',
    tag3Name: 'タグ 3'
}; // .crec ファイルから読み込まれるプロジェクト設定

// アニメーション遅延時間（ミリ秒）
const ANIMATION_DELAY = 10
let projectSettingsLoadPromise;

// 最小列幅（ピクセル）
const MIN_COLUMN_WIDTH = (() => {
    const v = (typeof document !== 'undefined')
        ? getComputedStyle(document.documentElement).getPropertyValue('--min-column-width').trim()
        : '';
    const parsed = parseInt(v);
    return Number.isFinite(parsed) ? parsed : 80; // 0 を正しく受け入れ、NaN の場合のみフォールバック
})();

// モバイルブレークポイントをCSSから取得
function getMobileBreakpoint() {
    const breakpoint = getComputedStyle(document.documentElement)
        .getPropertyValue('--mobile-breakpoint')
        .trim();
    return parseInt(breakpoint) || 768; // フォールバック値
}

/**
 * メイン検索ページかどうかを判定する
 * @returns {boolean}
 */
function isMainSearchPage() {
    const path = window.location.pathname.toLowerCase();
    return path === '/' || path === '/home' || path === '/home/index' || path.startsWith('/home/index/');
}

/**
 * コレクション詳細ページかどうかを判定する
 * @returns {boolean}
 */
function isCollectionDetailPage() {
    const path = window.location.pathname.toLowerCase();
    return path.startsWith('/collection/');
}

/**
 * 現在のコレクション詳細ページのコレクションIDをURLから取得する
 * @returns {string|null}
 */
function getCurrentCollectionId() {
    const match = window.location.pathname.match(/\/collection\/([^\/]+)/i);
    return match ? match[1] : null;
}

// DOMContentLoaded イベントで初期化
document.addEventListener('DOMContentLoaded', function () {
    initializeApp();
});

// Chrome Android 画面暗転時のフリッカー防止
// 画面オフ時に img の src を退避して GPU テクスチャ破棄による点滅を回避し、
// 復帰後にキャッシュから復元する。
document.addEventListener('visibilitychange', function () {
    if (document.hidden) {
        // 画面非表示: 画像ソースを退避して GPU テクスチャを除去
        document.querySelectorAll('img[src]').forEach(function (img) {
            var src = img.getAttribute('src');
            if (src && src.indexOf('data:') !== 0) {
                img.dataset.screenWakeSrc = src;
                img.removeAttribute('src');
            }
        });
    } else {
        // 画面復帰: 再描画完了後に画像ソースをキャッシュから復元
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                document.querySelectorAll('img[data-screen-wake-src]').forEach(function (img) {
                    img.src = img.dataset.screenWakeSrc;
                    delete img.dataset.screenWakeSrc;
                });
            });
        });
    }
});

// UI 言語の更新
function updateUILanguage() {
    const lang = currentLanguage;

    // data-lang 属性を持つ全要素を更新
    document.querySelectorAll('[data-lang]').forEach(element => {
        const key = element.getAttribute('data-lang');
        const translation = translations[lang][key];
        // th の場合は .th-content だけを書き換えて子要素(.resizer)を保持
        if (element.tagName === 'TH') {
            const thContent = element.querySelector('.th-content');
            if (thContent) {
                thContent.textContent = translation;
                return;
            }
        }

        if (translation) {
            element.textContent = translation;
        }
    });
}

/**
 * 複数の要素にイベントリスナを安全に追加するヘルパー関数
 * 
 * @param {Array<{id: string, event: string, handler: Function}>} listeners - イベントリスナの設定配列
 * 各要素は以下のプロパティを持つ:
 *   - id: 要素のID
 *   - event: イベント名（例: 'click', 'keypress'）
 *   - handler: イベントハンドラ関数
 * 
 *  イベントリスナのベストプラクティス:
 * - HTML要素にインラインのonclick属性を使用しない
 * - 代わりにdata属性（例: data-page）を使用して必要な情報を保存
 * - addEventListener()を使用してイベントリスナを登録
 */
function setupEventListeners(listeners) {
    listeners.forEach(({ id, event, handler }) => {
        const element = document.getElementById(id);
        if (element) {
            element.addEventListener(event, handler);
        }
    });
}

// アプリケーションの初期化
async function initializeApp() {
    try {
        console.log('Initializing app...');

        // 共通項目のイベントリスナを一括設定
        setupEventListeners([
            { id: 'adminPanelToggle', event: 'click', handler: openAdminPanel },// 管理パネルオープンのイベントリスナ
            { id: 'adminPanelClose', event: 'click', handler: closeAdminPanel },// 管理パネルクローズのイベントリスナ
            { id: 'adminPanelOverlay', event: 'click', handler: closeAdminPanel },// 管理パネルオーバーレイクリックのイベントリスナ
            { id: 'addNewCollectionBtn', event: 'click', handler: addNewCollection },// 新しいコレクション追加のイベントリスナ
            { id: 'editProjectBtn', event: 'click', handler: openProjectEdit },// プロジェクト編集のイベントリスナ
            { id: 'deleteCollectionBtn', event: 'click', handler: deleteCollection },// コレクション削除のイベントリスナ
        ]);

        // プロジェクト設定の読み込み
        await loadProjectSettings();

        // UI ラベルの更新（設定値反映）
        updateUILabels();

        // 言語適用
        buildLanguageDropdown();
        updateUILanguage();
        updateLanguageLabel();

        // URLからメインページにいるか確認
        if (isMainSearchPage()) {
            await initializeHomePage();
        }
        else if (isCollectionDetailPage()) {
            // コレクション削除ボタンの描画を削除可能状態に変更
            const deleteBtn = document.getElementById('deleteCollectionBtn');
            if (deleteBtn) {
                deleteBtn.setAttribute('aria-disabled', 'false');
            }
        }
        else {
            // これら以外のページの初期化（必要に応じて今後追加予定のため、場所だけ確保）
        }

        console.log('App initialized successfully');
    } catch (error) {
        console.error('Error initializing app:', error);
        // Only show error if error element exists
        const errorElement = document.getElementById('error');
        if (errorElement) {
            errorElement.textContent = 'Failed to initialize application: ' + error.message;
            errorElement.style.display = 'block';
        } else {
            console.error('Failed to initialize application:', error.message);
        }
    }
}

// API からプロジェクト設定を読み込む
async function loadProjectSettings() {
    if (projectSettingsLoadPromise) {
        return projectSettingsLoadPromise;
    }

    projectSettingsLoadPromise = (async () => {
        try {
            const response = await fetch('/api/ProjectSettings');
            if (response.ok) {
                const settings = await response.json();
                projectSettings = {
                    projectName: settings.projectName || '',
                    projectDataPath: settings.projectDataPath || '',
                    objectNameLabel: settings.objectNameLabel || t('field-name'),
                    uuidName: settings.uuidName || 'ID',
                    managementCodeName: settings.managementCodeName || 'MC',
                    categoryName: settings.categoryName || t('category'),
                    tag1Name: settings.tag1Name || t('field-firstTag'),
                    tag2Name: settings.tag2Name || t('field-secondTag'),
                    tag3Name: settings.tag3Name || t('field-thirdTag')
                };
                console.log('Project settings loaded:', projectSettings);
                // translationsの内容をプロジェクト設定値に合うように更新
                Object.keys(translations).forEach(lang => {
                    translations[lang]['field-name'] = projectSettings.objectNameLabel;
                    translations[lang]['field-id'] = projectSettings.uuidName;
                    translations[lang]['field-mc'] = projectSettings.managementCodeName;
                    translations[lang]['field-category'] = projectSettings.categoryName;
                    translations[lang]['field-firstTag'] = projectSettings.tag1Name;
                    translations[lang]['field-secondTag'] = projectSettings.tag2Name;
                    translations[lang]['field-thirdTag'] = projectSettings.tag3Name;
                });
            }
        } catch (error) {
            console.warn('Could not load project settings, using defaults:', error);
            // 既に初期化されたデフォルト値を保持
        }

        return projectSettings;
    })();

    return projectSettingsLoadPromise;
}

// プロジェクト設定のカスタム値で UI ラベルを更新
function updateUILabels() {
    // 検索フィールドのドロップダウンオプションを更新
    const searchFieldElement = document.getElementById('searchField');
    if (searchFieldElement) {
        // 現在の選択を保持
        const currentValue = searchFieldElement.value;

        // 全てのオプションをクリア
        searchFieldElement.innerHTML = '';

        // 「全項目」オプションを追加 - SearchField.All = 0
        const allFieldsOption = document.createElement('option');
        allFieldsOption.value = '0';
        allFieldsOption.text = t('field-all');
        searchFieldElement.appendChild(allFieldsOption);

        // ID オプションを追加 - SearchField.ID = 1
        const idOption = document.createElement('option');
        idOption.value = '1';
        idOption.text = projectSettings.uuidName;
        searchFieldElement.appendChild(idOption);

        // 名称オプションを追加 - SearchField.Name = 2
        const nameOption = document.createElement('option');
        nameOption.value = '2';
        nameOption.text = projectSettings.objectNameLabel || t('field-name');
        searchFieldElement.appendChild(nameOption);

        // 管理コードオプションを追加 - SearchField.ManagementCode = 3
        const mcOption = document.createElement('option');
        mcOption.value = '3';
        mcOption.text = projectSettings.managementCodeName;
        searchFieldElement.appendChild(mcOption);

        // カテゴリオプションを追加 - SearchField.Category = 4
        const categoryOption = document.createElement('option');
        categoryOption.value = '4';
        categoryOption.text = projectSettings.categoryName;
        searchFieldElement.appendChild(categoryOption);

        // タグ（全て）オプションを追加 - SearchField.Tag = 5
        const tagAllOption = document.createElement('option');
        tagAllOption.value = '5';
        tagAllOption.text = t('field-tags-all');
        searchFieldElement.appendChild(tagAllOption);

        // 個別タグオプションを追加 - SearchField.Tag1/2/3 = 6/7/8
        const tag1Option = document.createElement('option');
        tag1Option.value = '6';
        tag1Option.text = projectSettings.tag1Name;
        searchFieldElement.appendChild(tag1Option);

        const tag2Option = document.createElement('option');
        tag2Option.value = '7';
        tag2Option.text = projectSettings.tag2Name;
        searchFieldElement.appendChild(tag2Option);

        const tag3Option = document.createElement('option');
        tag3Option.value = '8';
        tag3Option.text = projectSettings.tag3Name;
        searchFieldElement.appendChild(tag3Option);

        // 場所オプションを追加 - SearchField.Location = 9
        const locationOption = document.createElement('option');
        locationOption.value = '9';
        locationOption.text = t('location');
        searchFieldElement.appendChild(locationOption);

        // 前の選択を復元（有効な場合）
        if (currentValue && Array.from(searchFieldElement.options).some(opt => opt.value === currentValue)) {
            searchFieldElement.value = currentValue;
        }
    }

    // プロジェクト名があればページタイトルを更新
    if (projectSettings.projectName) {
        document.title = `${projectSettings.projectName} - CREC Web`;
        const titleElement = document.querySelector('h1');
        if (titleElement) {
            titleElement.textContent = projectSettings.projectName;
        }
    }
}

/**
 * 在庫状況を表すテキストを取得
 * @param {number} status - The inventory status code (0-5)
 * @param {number|null} currentInventory - The current inventory count
 * @param {number|null} collectionSafetyStock - The safety stock level
 * @param {number|null} collectionOrderPoint - The reorder point
 * @param {number|null} collectionMaxStock - The maximum stock level
 * @returns {string} The inventory status text with optional quantity information
 */
function getInventoryStatusText(status, currentInventory, collectionSafetyStock, collectionOrderPoint, collectionMaxStock) {
    const statusMap = {
        0: t('stock-out'),
        1: t('under-stocked'),
        2: t('appropriate'),// 需要発注、文字数の都合で「適正在庫」のみを表示し、発注数は後で追加
        3: t('appropriate'),
        4: t('over-stocked'),
        5: t('not-set')
    };

    let statusText = statusMap[status] || t('not-set');

    // 該当ステータスの不足/過剰数を追加
    if (status !== 5 && currentInventory !== null) {
        if (status === 0 || status === 1 || status === 2) {
            // 必要な発注数を表示
            const orderPoint = collectionOrderPoint ?? collectionSafetyStock;
            if (orderPoint != null) {
                const diff = Number(orderPoint) - Number(currentInventory ?? 0);
                statusText += `<br>${t('order-quantity')} = ${diff}`;
            }
        } else if (status === 4) {
            if (collectionMaxStock != null) {
                // 過剰在庫数を表示
                const diff = currentInventory - collectionMaxStock;
                statusText += `<br>${t('excess-quantity')} = ${diff}`;
            }
        }
    }

    return statusText;
}

/**
 * 在庫状況に応じたバッジのクラスを取得
 * @param {number} status - 在庫状況のステータスコード (0-5)
 * @returns {string} - バッジに適用するクラス名
 */
function getInventoryStatusBadgeClass(status) {
    const classMap = {
        0: 'bg-danger',
        1: 'bg-warning',
        2: 'bg-success',
        3: 'bg-success',
        4: 'bg-info',
        5: 'bg-secondary'
    };
    return classMap[status] || 'bg-secondary';
}

/**
 * コレクションを別ウィンドウで開く
 * @param {string} collectionId - コレクションID
 * @param {boolean} [openEdit=false] - 編集モードで開くかどうか
 * @param {Window|null} [targetWindow=null] - 既存のウィンドウに表示する場合に指定
 * @returns {Window|null} - 開いたウィンドウオブジェクト
 */
function openCollectionWindow(collectionId, openEdit = false, targetWindow = null) {
    const url = `/Collection/${encodeURIComponent(collectionId)}${openEdit ? '?edit=1' : ''}`;

    if (targetWindow && !targetWindow.closed) {
        targetWindow.location.href = url;
        targetWindow.focus();
        return targetWindow;
    }

    const openedWindow = window.open(url, '_blank');
    if (!openedWindow) {
        window.location.href = url;
        return null;
    }

    return openedWindow;
}

/**
 * XHRを使用してファイルをアップロードし、プログレスバーを更新する
 * @param {string} url - アップロード先URL
 * @param {FormData} formData - アップロードするフォームデータ
 * @param {HTMLElement|null} progressBar - プログレスバー要素
 * @param {HTMLElement|null} progressContainer - プログレスバーコンテナ要素
 * @returns {Promise<void>}
 */
function uploadWithProgress(url, formData, progressBar, progressContainer) {
    if (progressContainer && progressBar) {
        progressContainer.style.display = '';
        progressBar.style.width = '0%';
        progressBar.textContent = '0%';
        progressBar.setAttribute('aria-valuenow', '0');
    }

    return new Promise((resolve, reject) => {
        const xhr = new XMLHttpRequest();
        xhr.open('POST', url);

        xhr.upload.addEventListener('progress', (event) => {
            if (event.lengthComputable && progressBar) {
                // サーバー側処理の余裕を残すため95%で上限を設ける
                const percent = Math.round((event.loaded / event.total) * 95);
                progressBar.style.width = percent + '%';
                progressBar.textContent = percent + '%';
                progressBar.setAttribute('aria-valuenow', String(percent));
            }
        });

        xhr.addEventListener('load', () => {
            if (progressBar) {
                progressBar.style.width = '100%';
                progressBar.textContent = '100%';
                progressBar.setAttribute('aria-valuenow', '100');
            }
            if (xhr.status >= 200 && xhr.status < 300) {
                resolve();
            } else {
                reject(new Error(`HTTP error! status: ${xhr.status}`));
            }
        });

        xhr.addEventListener('error', () => reject(new Error('Network error')));
        xhr.addEventListener('abort', () => reject(new Error('Upload aborted')));

        xhr.send(formData);
    });
}

/**
 * 画像をコレクションにアップロードする
 * @param {string} collectionId - コレクションID
 * @param {File} file - アップロード画像ファイル
 * @param {Function} onSuccess - アップロード成功後のコールバック
 */
async function uploadCollectionImage(collectionId, file, onSuccess) {
    const allowedExtensions = ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp'];
    const fileExtension = '.' + file.name.split('.').pop().toLowerCase();
    if (!allowedExtensions.includes(fileExtension)) {
        alert(t('add-image-invalid-format'));
        return;
    }

    const uploadBtn = document.getElementById('addImageBtn');
    let originalBtnHtml = '';
    if (uploadBtn) {
        originalBtnHtml = uploadBtn.innerHTML;
        uploadBtn.disabled = true;
        uploadBtn.innerHTML = `<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${t('uploading')}`;
    }

    const formData = new FormData();
    formData.append('image', file);

    const progressContainer = document.getElementById('imageUploadProgress');
    const progressBar = document.getElementById('imageUploadProgressBar');

    try {
        await uploadWithProgress(
            `/api/File/${encodeURIComponent(collectionId)}/upload/image`,
            formData,
            progressBar,
            progressContainer
        );

        alert(t('add-image-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error uploading image:', error);
        alert(t('add-image-error'));
    } finally {
        if (uploadBtn) {
            uploadBtn.disabled = false;
            uploadBtn.innerHTML = originalBtnHtml;
        }
        if (progressContainer) {
            progressContainer.style.display = 'none';
        }
    }
}

/**
 * 指定した画像をコレクションのサムネイルとして設定する
 * @param {string} collectionId - コレクションID
 * @param {string} fileName - サムネイルに設定する画像ファイル名
 */
async function setCollectionThumbnail(collectionId, fileName) {
    try {
        const response = await fetch(`/api/File/${encodeURIComponent(collectionId)}/set-thumbnail?fileName=${encodeURIComponent(fileName)}`, {
            method: 'POST'
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        // サムネイル画像のキャッシュを更新（対象コレクションのサムネイルのみ）
        const baseUrl = `/api/Files/thumbnail/${encodeURIComponent(collectionId)}`;
        const cacheBustedUrl = `${baseUrl}?t=${Date.now()}`;
        document.querySelectorAll(`img[src^="${baseUrl}"]`).forEach(img => {
            img.src = cacheBustedUrl;
        });

        alert(t('set-thumbnail-success'));
    } catch (error) {
        console.error('Error setting thumbnail:', error);
        alert(t('set-thumbnail-error'));
    }
}

/**
 * 指定した画像をコレクションから削除する
 * @param {string} collectionId - コレクションID
 * @param {string} fileName - 削除する画像ファイル名
 * @param {Function} onSuccess - 削除成功後のコールバック
 */
async function deleteCollectionImage(collectionId, fileName, onSuccess) {
    try {
        const response = await fetch(`/api/File/${encodeURIComponent(collectionId)}/image?fileName=${encodeURIComponent(fileName)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        alert(t('delete-image-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error deleting image:', error);
        alert(t('delete-image-error'));
    }
}

/**
 * 動画をコレクションにアップロードする
 * @param {string} collectionId - コレクションID
 * @param {File} file - アップロード動画ファイル
 * @param {Function} onSuccess - アップロード成功後のコールバック
 */
async function uploadCollectionVideo(collectionId, file, onSuccess) {
    const allowedExtensions = ['.mp4', '.mov', '.avi', '.mkv', '.webm', '.wmv', '.flv', '.m4v'];
    const fileExtension = '.' + file.name.split('.').pop().toLowerCase();
    if (!allowedExtensions.includes(fileExtension)) {
        alert(t('add-video-invalid-format'));
        return;
    }

    const uploadBtn = document.getElementById('addVideoBtn');
    let originalBtnHtml = '';
    if (uploadBtn) {
        originalBtnHtml = uploadBtn.innerHTML;
        uploadBtn.disabled = true;
        uploadBtn.innerHTML = `<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${t('uploading')}`;
    }

    const formData = new FormData();
    formData.append('video', file);

    const progressContainer = document.getElementById('videoUploadProgress');
    const progressBar = document.getElementById('videoUploadProgressBar');

    try {
        await uploadWithProgress(
            `/api/File/${encodeURIComponent(collectionId)}/upload/video`,
            formData,
            progressBar,
            progressContainer
        );

        alert(t('add-video-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error uploading video:', error);
        alert(t('add-video-error'));
    } finally {
        if (uploadBtn) {
            uploadBtn.disabled = false;
            uploadBtn.innerHTML = originalBtnHtml;
        }
        if (progressContainer) {
            progressContainer.style.display = 'none';
        }
    }
}

/**
 * 指定した動画をコレクションから削除する
 * @param {string} collectionId - コレクションID
 * @param {string} fileName - 削除する動画ファイル名
 * @param {Function} onSuccess - 削除成功後のコールバック
 */
async function deleteCollectionVideo(collectionId, fileName, onSuccess) {
    try {
        const response = await fetch(`/api/File/${encodeURIComponent(collectionId)}/video?fileName=${encodeURIComponent(fileName)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        alert(t('delete-video-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error deleting video:', error);
        alert(t('delete-video-error'));
    }
}

/**
 * 3Dファイル（STL）をコレクションにアップロードする
 * @param {string} collectionId - コレクションID
 * @param {File} file - アップロード3Dファイル
 * @param {Function} onSuccess - アップロード成功後のコールバック
 */
async function uploadCollection3DFile(collectionId, file, onSuccess) {
    const allowedExtensions = ['.stl'];
    const fileExtension = '.' + file.name.split('.').pop().toLowerCase();
    if (!allowedExtensions.includes(fileExtension)) {
        alert(t('add-3d-invalid-format'));
        return;
    }

    const uploadBtn = document.getElementById('add3DBtn');
    let originalBtnHtml = '';
    if (uploadBtn) {
        originalBtnHtml = uploadBtn.innerHTML;
        uploadBtn.disabled = true;
        uploadBtn.innerHTML = `<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> ${t('uploading')}`;
    }

    const formData = new FormData();
    formData.append('threeDFile', file);

    const progressContainer = document.getElementById('threeDUploadProgress');
    const progressBar = document.getElementById('threeDUploadProgressBar');

    try {
        await uploadWithProgress(
            `/api/File/${encodeURIComponent(collectionId)}/upload/3ddata`,
            formData,
            progressBar,
            progressContainer
        );

        alert(t('add-3d-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error uploading 3D file:', error);
        alert(t('add-3d-error'));
    } finally {
        if (uploadBtn) {
            uploadBtn.disabled = false;
            uploadBtn.innerHTML = originalBtnHtml;
        }
        if (progressContainer) {
            progressContainer.style.display = 'none';
        }
    }
}

/**
 * 指定した3Dファイル（STL）をコレクションから削除する
 * @param {string} collectionId - コレクションID
 * @param {string} fileName - 削除する3Dファイル名
 * @param {Function} onSuccess - 削除成功後のコールバック
 */
async function deleteCollection3DFile(collectionId, fileName, onSuccess) {
    try {
        const response = await fetch(`/api/File/${encodeURIComponent(collectionId)}/3ddata?fileName=${encodeURIComponent(fileName)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        alert(t('delete-3d-success'));
        if (typeof onSuccess === 'function') {
            await onSuccess();
        }
    } catch (error) {
        console.error('Error deleting 3D file:', error);
        alert(t('delete-3d-error'));
    }
}

// =====================
// Admin Panel Functions
// =====================

/**
 * 管理画面パネルを開く
 */
function openAdminPanel() {
    const panel = document.getElementById('adminPanel');
    const overlay = document.getElementById('adminPanelOverlay');
    const toggle = document.getElementById('adminPanelToggle');
    if (panel) panel.classList.add('open');
    if (overlay) overlay.classList.add('show');
    if (toggle) toggle.setAttribute('aria-expanded', 'true');
}

/**
 * 管理画面パネルを閉じる
 */
function closeAdminPanel() {
    const panel = document.getElementById('adminPanel');
    const overlay = document.getElementById('adminPanelOverlay');
    const toggle = document.getElementById('adminPanelToggle');
    if (panel) panel.classList.remove('open');
    if (overlay) overlay.classList.remove('show');
    if (toggle) toggle.setAttribute('aria-expanded', 'false');
}

/**
 * プロジェクト編集画面を新しいタブで開く
 */
function openProjectEdit() {
    window.open('/ProjectEdit', '_blank');
}

/**
 * 新規コレクションを追加する
 */
async function addNewCollection() {
    const btn = document.getElementById('addNewCollectionBtn');
    if (btn) btn.disabled = true;

    // ポップアップブロック回避のため、先にウィンドウを開いておく
    const newWindow = window.open('about:blank', '_blank');

    try {
        const response = await fetch('/api/collections', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' }
        });

        if (!response.ok) {
            if (newWindow && !newWindow.closed) newWindow.close();
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        const result = await response.json();
        const newId = result.id;

        // メイン画面の場合はコレクション一覧を更新
        if (isMainSearchPage()) {
            await searchCollections(currentPage);
        }

        // 詳細画面を別ウインドウで開き編集モーダルを自動で表示する
        openCollectionWindow(newId, true, newWindow);
    } catch (error) {
        if (newWindow && !newWindow.closed) newWindow.close();
        console.error('Error creating new collection:', error);
        alert(t('add-collection-error') + ': ' + error.message);
    } finally {
        if (btn) btn.disabled = false;
    }
}

/**
 * 現在のコレクションを削除する（RecycleBinフォルダに移動）
 */
async function deleteCollection() {
    // コレクション詳細ページでない場合はメッセージを表示
    if (!isCollectionDetailPage()) {
        alert(t('delete-collection-open-required'));
        return;
    }

    // 現在のコレクションIDを取得
    const collectionId = getCurrentCollectionId();
    // コレクションIDが取得できない場合はメッセージを表示
    if (!collectionId) {
        alert(t('delete-collection-open-required'));
        return;
    }

    // 確認ダイアログを表示
    if (!confirm(t('delete-collection-confirm'))) {
        return;
    }

    // ボタンを無効化して多重クリックを防止
    const btn = document.getElementById('deleteCollectionBtn');
    if (btn) {
        btn.disabled = true;
        btn.classList.add('disabled');
        btn.setAttribute('aria-disabled', 'true');
    }

    try {
        const response = await fetch(`/api/collections/${encodeURIComponent(collectionId)}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        alert(t('delete-collection-success'));// 削除成功メッセージをユーザに表示
        window.location.href = '/';// ホームページにリダイレクト
    } catch (error) {
        console.error('Error deleting collection:', error);// エラーメッセージをコンソールに表示
        alert(t('delete-collection-error') + ': ' + error.message);// エラーメッセージをユーザーに表示
        // ボタンを有効化
        if (btn) {
            btn.disabled = false;
            btn.classList.remove('disabled');
            btn.setAttribute('aria-disabled', 'false');
        }
    }
}

// 言語ドロップダウンを構築し、各アイテムのクリックイベントを設定する
function buildLanguageDropdown() {
    const menu = document.getElementById('languageDropdownMenu');
    if (!menu) return;

    menu.innerHTML = '';
    Object.keys(translations).forEach(lang => {
        const li = document.createElement('li');
        const a = document.createElement('a');
        a.className = 'dropdown-item';
        a.href = '#';
        a.textContent = languageNames[lang] || lang;
        a.addEventListener('click', (e) => {
            e.preventDefault();
            selectLanguage(lang);
        });
        li.appendChild(a);
        menu.appendChild(li);
    });

    updateLanguageLabel();
}

// 指定した言語に切り替える
function selectLanguage(lang) {
    if (!translations[lang]) return;
    currentLanguage = lang;
    // Save language preference to localStorage so it persists across pages
    localStorage.setItem('crec_language', currentLanguage);
    updateLanguageLabel();
    updateUILanguage();
    updateUILabels();
    if (isMainSearchPage()) {
        updateTableHeaders();
        // 現在の結果を新しい言語で再描画
        if (currentSearchCriteria && Object.keys(currentSearchCriteria).length > 0) {
            searchCollections(currentPage);
        }
    }
}

// 言語ラベルを現在の言語名で更新する
function updateLanguageLabel() {
    const label = document.getElementById('languageLabel');
    if (label) {
        label.textContent = languageNames[currentLanguage] || currentLanguage;
    }
}

/**
 * 使用可能な言語かどうかを安全に判定し、必要に応じてフォールバックする
 * @returns
 */
function getSafeLanguage() {
    // 現在の言語がtranslationsオブジェクト内に存在するかを安全にチェック
    if (typeof translations === 'object' && translations !== null && translations[currentLanguage]) {
        return currentLanguage;
    }

    // 日本語が使用可能であれば日本語にフォールバックし、そうでなければ利用可能な最初の言語を使用する
    const hasTranslationsObject = typeof translations === 'object' && translations !== null;
    const fallback =
        (hasTranslationsObject && translations['ja'])
            ? 'ja'
            : (hasTranslationsObject ? Object.keys(translations)[0] : undefined);

    if (fallback) {
        currentLanguage = fallback;
        try {
            localStorage.setItem('crec_language', currentLanguage);
        } catch (e) {
            // localStorageへのアクセスに失敗した場合は、そのまま続行する
        }
        return fallback;
    }

    // 使用可能な言語が定義されていない場合は、そのままとする。
    return currentLanguage;
}

function t(key) {
    const lang = getSafeLanguage();
    return translations[lang]?.[key] || key;
}

/**
 * HTMLエスケープを行う
 * @param {any} text
 * @returns
 */
function escapeHtml(text) {
    if (text === null || text === undefined) return '';
    const map = {
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
    };
    return String(text).replace(/[&<>"']/g, ch => map[ch]);
}

/**
 * HTMLからテキストを抽出する
 * @param {any} html
 * @returns
 */
function stripHtmlToText(html) {
    const div = document.createElement('div');
    div.innerHTML = html || '';
    // textContent はタグを取り除いた生テキストを返す
    return (div.textContent || div.innerText || '').replace(/\s+/g, ' ').trim();
}

/**
 * UTC日時文字列をローカル日時文字列に変換
 * @param {string} utcDateString - UTC形式の日時文字列 (例: "2024-11-08T18:13:37.0000000+00:00")
 * @returns {string} - ローカルタイムゾーンでフォーマットされた日時文字列、または元の文字列（パース失敗時）
 */
function formatUtcToLocal(utcDateString) {
    if (!utcDateString || utcDateString === '-' || utcDateString === ' - ') {
        return '-';
    }
    
    try {
        // ISO 8601形式の基本的な検証（簡易チェック）
        // 最低限、日付部分（YYYY-MM-DD）と時刻のT区切りがあることを確認
        if (typeof utcDateString !== 'string' || !utcDateString.includes('T')) {
            return utcDateString;
        }
        
        const date = new Date(utcDateString);
        
        // 日付が無効な場合は元の文字列を返す
        if (isNaN(date.getTime())) {
            return utcDateString;
        }
        
        // ローカルタイムゾーンで日時をフォーマット
        // YYYY-MM-DD HH:mm:ss 形式
        const year = date.getFullYear();
        const month = String(date.getMonth() + 1).padStart(2, '0');
        const day = String(date.getDate()).padStart(2, '0');
        const hours = String(date.getHours()).padStart(2, '0');
        const minutes = String(date.getMinutes()).padStart(2, '0');
        const seconds = String(date.getSeconds()).padStart(2, '0');
        
        return `${year}-${month}-${day} ${hours}:${minutes}:${seconds}`;
    } catch (error) {
        console.warn('Failed to parse UTC date:', utcDateString, error);
        return utcDateString;
    }
}
