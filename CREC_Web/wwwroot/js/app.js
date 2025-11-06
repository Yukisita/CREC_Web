/*
CREC Web - Frontend Application
Copyright (c) [2025] [S.Yukisita]
This software is released under the MIT License.
*/

// グローバル変数
let currentPage = 1;
let currentPageSize = 20;
let currentSearchCriteria = {};
let currentLanguage = 'ja'; // 'ja' は日本語、'en' は英語
let projectSettings = {
    projectName: '',
    objectNameLabel:'Collection Name',
    uuidName: 'UUID',
    managementCodeName: 'MC',
    categoryName: 'カテゴリ',
    tag1Name: 'タグ 1',
    tag2Name: 'タグ 2',
    tag3Name: 'タグ 3'
}; // .crec ファイルから読み込まれるプロジェクト設定

// パネル用イベントハンドラを格納する WeakMap
const panelEventHandlers = new WeakMap();

// 列リサイズが初期化済みか追跡
let columnResizingInitialized = false;

// 言語翻訳
const translations = {
    ja: {
        'loading': '読み込み中...',
        'search-results': '検索結果',
        'items-found': '件見つかりました',
        'no-results': '検索結果がありません',
        'error-loading': 'データの読み込みでエラーが発生しました',
        'registration-date': '登録日',
        'management-code': '管理コード',
        'location': '場所',
        'inventory': '在庫数',
        'inventory-status': '在庫状況',
        'tags': 'タグ',
        'comment': 'コメント',
        'images': '画像',
        'files': 'ファイル',
        'no-thumbnail': 'サムネイルなし',
        'view-details': '詳細表示',
        'stock-out': '在庫切れ',
        'under-stocked': '在庫不足',
        'appropriate': '在庫適正',
        'over-stocked': '在庫過剰',
        'not-set': '未設定',
        'page': 'ページ',
        'of': '/',
        'previous': '前へ',
        'next': '次へ',
        'search-title': '検索フィルター',
        'search-text': 'テキスト検索',
        'search-field': '検索対象',
        'field-all': '全項目',
        'field-id': 'UUID',
        'field-name': '名称',
        'field-mc': '管理コード',
        'field-category': 'カテゴリー',
        'field-tags': 'タグ',
        'field-location': '場所',
        'search-method': '検索方式',
        'method-partial': '部分一致',
        'method-prefix': '前方一致',
        'method-suffix': '後方一致',
        'method-exact': '完全一致',
        'all-status': '全状況',
        'page-size': '表示件数',
        'search-button': '検索',
        'clear-button': 'クリア',
        'close': '閉じる'
    },
    en: {
        'loading': 'Loading...',
        'search-results': 'Search Results',
        'items-found': 'items found',
        'no-results': 'No results found',
        'error-loading': 'Error loading data',
        'registration-date': 'Registration Date',
        'management-code': 'Management Code',
        'location': 'Location',
        'inventory': 'Inventory',
        'inventory-status': 'Inventory Status',
        'tags': 'Tags',
        'comment': 'Comment',
        'images': 'Images',
        'files': 'Files',
        'no-thumbnail': 'No thumbnail',
        'view-details': 'View Details',
        'stock-out': 'Stock Out',
        'under-stocked': 'Under Stocked',
        'appropriate': 'Appropriate',
        'over-stocked': 'Over Stocked',
        'not-set': 'Not Set',
        'page': 'Page',
        'of': 'of',
        'previous': 'Previous',
        'next': 'Next',
        'search-title': 'Search Filters',
        'search-text': 'Search Text',
        'search-field': 'Search Field',
        'field-all': 'All Fields',
        'field-id': 'ID',
        'field-name': 'Name',
        'field-mc': 'Management Code',
        'field-category': 'Category',
        'field-tags': 'Tag',
        'field-location': 'Location',
        'search-method': 'Search Method',
        'method-partial': 'Partial',
        'method-prefix': 'Prefix',
        'method-suffix': 'Suffix',
        'method-exact': 'Exact',
        'all-status': 'All Status',
        'page-size': 'Page Size',
        'search-button': 'Search',
        'clear-button': 'Clear',
        'close': 'Close'
    }
};

// DOMContentLoaded イベントで初期化
document.addEventListener('DOMContentLoaded', function () {
    initializeApp();
});

// UI 言語の更新
function updateUILanguage() {
    const lang = currentLanguage;

    // data-lang 属性を持つ全要素を更新
    document.querySelectorAll('[data-lang]').forEach(element => {
        const key = element.getAttribute('data-lang');
        const translation = translations[lang][key];
        element.textContent = translation;
    });
}

async function initializeApp() {
    try {
        console.log('Initializing app...');

        // プロジェクト設定の読み込み
        await loadProjectSettings();

        // UI ラベルの更新（設定値反映）
        updateUILabels();

        // 言語適用
        updateUILanguage();

        // Enter キーで検索
        const searchTextElement = document.getElementById('searchText');
        if (searchTextElement) {
            searchTextElement.addEventListener('keypress', function (e) {
                if (e.key === 'Enter') {
                    searchCollections();
                }
            });
        }

        // 詳細パネルのクローズハンドラ
        const detailPanelOverlay = document.getElementById('detailPanelOverlay');
        const detailPanelClose = document.getElementById('detailPanelClose');
        if (detailPanelOverlay) {
            detailPanelOverlay.addEventListener('click', closeDetailPanel);
        }
        if (detailPanelClose) {
            detailPanelClose.addEventListener('click', closeDetailPanel);
        }

        // 初回検索
        await searchCollections();
        console.log('App initialized successfully');
    } catch (error) {
        console.error('Error initializing app:', error);
        showError('Failed to initialize application: ' + error.message);
    }
}

// 列リサイズ機能の初期化
function initializeColumnResizing() {
    const table = document.querySelector('.collections-table');
    if (!table) {
        console.log('Table not found for column resizing');
        return;
    }

    // 既に初期化済みならスキップ
    if (columnResizingInitialized) {
        console.log('Column resizing already initialized');
        return;
    }
    columnResizingInitialized = true;

    const thead = table.querySelector('thead');
    const ths = thead.querySelectorAll('th.resizable');

    console.log(`Initializing column resizing for ${ths.length} columns`);

    // プロジェクト設定でタグ列ヘッダーを更新
    const tagHeaders = Array.from(ths).filter((th, index) => index >= 1 && index <= 7);
    if (tagHeaders.length === 7) {
        const thContents = tagHeaders.map(th => th.querySelector('.th-content'));
        if (thContents[0]) thContents[0].textContent = projectSettings.objectNameLabel || (currentLanguage === 'ja' ? '名称' : 'Name');
        if (thContents[1]) thContents[1].textContent = projectSettings.uuidName || 'ID';
        if (thContents[2]) thContents[2].textContent = projectSettings.managementCodeName || (currentLanguage === 'ja' ? '管理コード' : 'Management Code');
        if (thContents[3]) thContents[3].textContent = projectSettings.categoryName || (currentLanguage === 'ja' ? 'カテゴリ' : 'Category');
        if (thContents[4]) thContents[4].textContent = projectSettings.tag1Name || (currentLanguage === 'ja' ? 'タグ 1' : 'Tag 1');
        if (thContents[5]) thContents[5].textContent = projectSettings.tag2Name || (currentLanguage === 'ja' ? 'タグ 2' : 'Tag 2');
        if (thContents[6]) thContents[6].textContent = projectSettings.tag3Name || (currentLanguage === 'ja' ? 'タグ 3' : 'Tag 3');
    }

    ths.forEach((th, index) => {
        const resizer = th.querySelector('.resizer');
        if (!resizer) {
            return;
        }

        // リサイズ要素を表示にする
        resizer.style.display = 'block';

        let startX, startWidth;

        const onMouseDown = (e) => {
            e.stopPropagation();
            e.preventDefault();
            startX = e.pageX;
            startWidth = th.offsetWidth;

            th.classList.add('resizing');

            document.addEventListener('mousemove', onMouseMove);
            document.addEventListener('mouseup', onMouseUp);
        };

        const onMouseMove = (e) => {
            e.preventDefault();
            const diff = e.pageX - startX;
            const newWidth = Math.max(50, startWidth + diff);
            th.style.width = newWidth + 'px';
        };

        const onMouseUp = () => {
            th.classList.remove('resizing');
            document.removeEventListener('mousemove', onMouseMove);
            document.removeEventListener('mouseup', onMouseUp);
        };

        resizer.addEventListener('mousedown', onMouseDown);
    });
}

// API からプロジェクト設定を読み込む
async function loadProjectSettings() {
    try {
        const response = await fetch('/api/ProjectSettings');
        if (response.ok) {
            const settings = await response.json();
            projectSettings = {
                projectName: settings.projectName || '',
                objectNameLabel: settings.objectNameLabel || (currentLanguage === 'ja' ? '名称' : 'Name'),
                uuidName: settings.uuidName || 'ID',
                managementCodeName: settings.managementCodeName || 'MC',
                categoryName: settings.categoryName || (currentLanguage === 'ja' ? 'カテゴリ' : 'Category'),
                tag1Name: settings.tag1Name || (currentLanguage === 'ja' ? 'タグ 1' : 'Tag 1'),
                tag2Name: settings.tag2Name || (currentLanguage === 'ja' ? 'タグ 2' : 'Tag 2'),
                tag3Name: settings.tag3Name || (currentLanguage === 'ja' ? 'タグ 3' : 'Tag 3')
            };
            console.log('Project settings loaded:', projectSettings);
            // translationsの内容をプロジェクト設定値と合うようにを更新
            // field-name
            translations.ja['field-name'] = projectSettings.objectNameLabel;
            translations.en['field-name'] = projectSettings.objectNameLabel;
            // field-id
            translations.ja['field-id'] = projectSettings.uuidName;
            translations.en['field-id'] = projectSettings.uuidName;
            // field-mc
            translations.ja['field-mc'] = projectSettings.managementCodeName;
            translations.en['field-mc'] = projectSettings.managementCodeName;
            // field-category
            translations.ja['field-category'] = projectSettings.categoryName;
            translations.en['field-category'] = projectSettings.categoryName;
        }
    } catch (error) {
        console.warn('Could not load project settings, using defaults:', error);
        // 既に初期化されたデフォルト値を保持
    }
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
        allFieldsOption.text = currentLanguage === 'ja' ? 'すべてのフィールド' : 'All Fields';
        searchFieldElement.appendChild(allFieldsOption);

        // ID オプションを追加 - SearchField.ID = 1
        const idOption = document.createElement('option');
        idOption.value = '1';
        idOption.text = projectSettings.uuidName;
        searchFieldElement.appendChild(idOption);

        // 名称オプションを追加 - SearchField.Name = 2
        const nameOption = document.createElement('option');
        nameOption.value = '2';
        nameOption.text = projectSettings.objectNameLabel || (currentLanguage === 'ja' ? '名称' : 'Name');
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
        tagAllOption.text = currentLanguage === 'ja' ? 'タグ (全て)' : 'Tags (All)';
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
        locationOption.text = currentLanguage === 'ja' ? '場所' : 'Location';
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

// コレクション検索
async function searchCollections(page = 1) {
    try {
        currentPage = page;

        // ページサイズ要素を取得 - デフォルト値を使う安全な方法
        const pageSizeElement = document.getElementById('pageSize');
        currentPageSize = pageSizeElement ? parseInt(pageSizeElement.value) : 20;

        // 検索フィルタ要素を防御的に取得
        const searchTextElement = document.getElementById('searchText');
        const searchFieldElement = document.getElementById('searchField');
        const searchMethodElement = document.getElementById('searchMethod');
        const inventoryStatusElement = document.getElementById('inventoryStatusFilter');

        // 安全にアクセス可能なデフォルト値付きで検索条件を構築
        const criteria = {
            searchText: searchTextElement ? searchTextElement.value : '',
            searchField: searchFieldElement ? (parseInt(searchFieldElement.value) || 0) : 0,
            searchMethod: searchMethodElement ? (parseInt(searchMethodElement.value) || 0) : 0,
            inventoryStatus: inventoryStatusElement ? (inventoryStatusElement.value || null) : null,
            page: currentPage,
            pageSize: currentPageSize
        };

        currentSearchCriteria = criteria;
        console.log('Search criteria:', criteria);

        showLoading(true);
        hideError();

        const queryParams = new URLSearchParams();
        Object.keys(criteria).forEach(key => {
            if (criteria[key] !== null && criteria[key] !== '') {
                queryParams.append(key, criteria[key]);
            }
        });

        console.log('Query params:', queryParams.toString());
        const response = await fetch(`/api/collections/search?${queryParams}`);
        console.log('Response status:', response.status);

        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        const result = await response.json();
        console.log('Search result:', result);
        displaySearchResults(result);
        updatePagination(result);
    } catch (error) {
        console.error('Error searching collections:', error);
        showError(t('error-loading') + ': ' + error.message);
    } finally {
        showLoading(false);
    }
}

function displaySearchResults(result) {
    const tableContainer = document.getElementById('collectionsTableContainer');
    const tableBody = document.getElementById('collectionsTable');
    const summary = document.getElementById('resultsSummary');
    const resultsText = document.getElementById('resultsText');
    const resultsCount = document.getElementById('resultsCount');

    // 以前の結果をクリア
    tableBody.innerHTML = '';

    if (result.collections.length === 0) {
        tableBody.innerHTML = `
            <tr>
                <td colspan="12" class="text-center py-5">
                    <i class="bi bi-search display-1 text-muted"></i>
                    <h4 class="mt-3 text-muted">${t('no-results')}</h4>
                </td>
            </tr>
        `;
        summary.style.display = 'none';
    } else {
        // サマリを更新
        resultsText.textContent = `${t('search-results')}: ${result.totalCount} ${t('items-found')}`;
        resultsCount.textContent = result.totalCount;
        summary.style.display = 'block';

        // コレクションをテーブル行として表示
        result.collections.forEach(collection => {
            const row = createCollectionRow(collection);
            tableBody.appendChild(row);
        });
    }

    tableContainer.style.display = 'block';

    // テーブル表示後に列リサイズを初期化
    initializeColumnResizing();
}

function createCollectionRow(collection) {
    const row = document.createElement('tr');
    row.addEventListener('click', () => showCollectionDetails(collection.collectionID));

    const inventoryStatusText = getInventoryStatusText(
        collection.collectionInventoryStatus,
        collection.collectionCurrentInventory,
        collection.collectionOrderPoint,
        collection.collectionMaxStock
    );
    const inventoryBadgeClass = getInventoryStatusBadgeClass(collection.collectionInventoryStatus);

    const collectionId = collection.collectionID || 'unknown';
    const thumbnailUrl = `/api/Files/thumbnail/${encodeURIComponent(collectionId)}`;

    const thumbnailImg = document.createElement('img');
    thumbnailImg.src = thumbnailUrl;
    thumbnailImg.className = 'thumbnail-small';
    thumbnailImg.alt = 'Thumbnail';

    const thumbnailPlaceholder = document.createElement('div');
    thumbnailPlaceholder.className = 'thumbnail-placeholder-small';
    thumbnailPlaceholder.style.display = 'none';
    thumbnailPlaceholder.innerHTML = '<i class="bi bi-image"></i>';

    thumbnailImg.addEventListener('error', () => {
        thumbnailImg.style.display = 'none';
        thumbnailPlaceholder.style.display = 'flex';
    });

    const thumbnailCell = document.createElement('td');
    thumbnailCell.className = 'thumbnail-cell';
    thumbnailCell.appendChild(thumbnailImg);
    thumbnailCell.appendChild(thumbnailPlaceholder);

    const nameCell = document.createElement('td');
    nameCell.innerHTML = `<strong>${escapeHtml(collection.collectionName)}</strong>`;
    nameCell.title = collection.collectionName;

    const idCell = document.createElement('td');
    idCell.innerHTML = `<small class="text-muted">${escapeHtml(collection.collectionID)}</small>`;
    idCell.title = collection.collectionID;

    const mcCell = document.createElement('td');
    mcCell.textContent = collection.collectionMC || '-';
    mcCell.title = collection.collectionMC || '-';

    const categoryCell = document.createElement('td');
    categoryCell.textContent = collection.collectionCategory || '-';
    categoryCell.title = collection.collectionCategory || '-';

    const tag1Cell = document.createElement('td');
    tag1Cell.textContent = (collection.collectionTag1 && collection.collectionTag1 !== ' - ') ? collection.collectionTag1 : '-';
    tag1Cell.title = tag1Cell.textContent;

    const tag2Cell = document.createElement('td');
    tag2Cell.textContent = (collection.collectionTag2 && collection.collectionTag2 !== ' - ') ? collection.collectionTag2 : '-';
    tag2Cell.title = tag2Cell.textContent;

    const tag3Cell = document.createElement('td');
    tag3Cell.textContent = (collection.collectionTag3 && collection.collectionTag3 !== ' - ') ? collection.collectionTag3 : '-';
    tag3Cell.title = tag3Cell.textContent;

    const locationCell = document.createElement('td');
    locationCell.textContent = collection.collectionRealLocation || '-';
    locationCell.title = collection.collectionRealLocation || '-';

    const dateCell = document.createElement('td');
    dateCell.textContent = collection.collectionRegistrationDate || '-';
    dateCell.title = collection.collectionRegistrationDate || '-';

    const inventoryCell = document.createElement('td');
    inventoryCell.textContent = collection.collectionCurrentInventory !== null ? collection.collectionCurrentInventory : '-';
    inventoryCell.title = inventoryCell.textContent;

    const statusCell = document.createElement('td');
    statusCell.innerHTML = `<span class="badge ${inventoryBadgeClass}">${inventoryStatusText}</span>`;
    statusCell.title = inventoryStatusText;

    row.appendChild(thumbnailCell);
    row.appendChild(nameCell);
    row.appendChild(idCell);
    row.appendChild(mcCell);
    row.appendChild(categoryCell);
    row.appendChild(tag1Cell);
    row.appendChild(tag2Cell);
    row.appendChild(tag3Cell);
    row.appendChild(locationCell);
    row.appendChild(dateCell);
    row.appendChild(inventoryCell);
    row.appendChild(statusCell);

    return row;
}

function handleThumbnailError(imgElement) {
    // この関数は後方互換性のために残しています（他で必要な場合）
    imgElement.style.display = 'none';
    imgElement.nextElementSibling.style.display = 'flex';
}

function getInventoryStatusText(status, currentInventory, collectionOrderPoint, collectionMaxStock) {
    const statusMap = {
        0: t('stock-out'),
        1: t('under-stocked'),
        2: t('appropriate'),
        3: t('over-stocked'),
        4: t('not-set')
    };

    let statusText = statusMap[status] || t('not-set');

    // 該当ステータスの不足/過剰数を追加
    if (status !== 4 && currentInventory !== null && collectionOrderPoint !== null) {
        if (status === 0 || status === 1) {
            // 在庫切れ/不足 - 不足数を表示
            const diff = currentInventory - collectionOrderPoint;
            statusText += `: ${diff}`;
        } else if (status === 3) {
            // 過剰在庫 - 余剰数を表示
            const diff = collectionMaxStock - currentInventory;
            statusText += `: +${diff}`;
        }
    }

    return statusText;
}

function getInventoryStatusBadgeClass(status) {
    const classMap = {
        0: 'bg-danger',
        1: 'bg-warning',
        2: 'bg-success',
        3: 'bg-info',
        4: 'bg-secondary'
    };
    return classMap[status] || 'bg-secondary';
}

async function showCollectionDetails(collectionId) {
    try {
        const response = await fetch(`/api/collections/${encodeURIComponent(collectionId)}`);
        if (!response.ok) {
            throw new Error(`HTTP error! status: ${response.status}`);
        }

        const collection = await response.json();
        displayCollectionPanel(collection);
    } catch (error) {
        console.error('Error loading collection details:', error);
        alert(t('error-loading') + ': ' + error.message);
    }
}

function displayCollectionPanel(collection) {
    const panel = document.getElementById('detailPanel');
    const overlay = document.getElementById('detailPanelOverlay');
    const panelTitle = document.getElementById('detailPanelTitle');
    const panelBody = document.getElementById('detailPanelBody');

    // 新しいリスナを設定する前に既存のイベントリスナをクリーンアップ
    cleanupPanelEventListeners(panel);

    panelTitle.textContent = collection.collectionName;

    const inventoryStatusText = getInventoryStatusText(
        collection.collectionInventoryStatus,
        collection.collectionCurrentInventory,
        collection.collectionOrderPoint,
        collection.collectionMaxStock
    );
    const inventoryBadgeClass = getInventoryStatusBadgeClass(collection.collectionInventoryStatus);

    const images = collection.imageFiles || [];
    let currentImageIndex = 0;

    const imagesHtml = images.length > 0
        ? `
            <div class="image-carousel">
                <img id="carouselImage" src="/api/File/${encodeURIComponent(collection.collectionID)}/${encodeURIComponent(images[0])}" 
                        class="detail-image" 
                        alt="${escapeHtml(images[0])}"
                        onerror="this.onerror=null; this.src='data:image/svg+xml,%3Csvg xmlns=\'http://www.w3.org/2000/svg\' width=\'200\' height=\'200\'%3E%3Crect width=\'200\' height=\'200\' fill=\'%23ddd\'/%3E%3Ctext x=\'50%25\' y=\'50%25\' dominant-baseline=\'middle\' text-anchor=\'middle\' font-family=\'sans-serif\' font-size=\'16\' fill=\'%23999\'%3EImage not found%3C/text%3E%3C/svg%3E';">
                <div class="text-center mt-3" style="display: flex; justify-content: center; align-items: center; gap: 15px;">
                    <button id="prevImage" class="btn btn-outline-secondary btn-sm">◀</button>
                    <span id="imageCounter" style="font-size: 14px; min-width: 60px;">${currentImageIndex + 1} / ${images.length}</span>
                    <button id="nextImage" class="btn btn-outline-secondary btn-sm">▶</button>
                </div>
                <p id="imageName" class="small text-muted text-center mt-2">${escapeHtml(images[0])}</p>
            </div>
            `
        : `<p class="text-muted">${t('no-images')}</p>`;

    const filesHtml = collection.otherFiles.length > 0
        ? collection.otherFiles.map(file => `
            <li class="list-group-item d-flex justify-content-between align-items-center">
                ${escapeHtml(file)}
                <a href="/api/File/data/${encodeURIComponent(collection.collectionID)}/${encodeURIComponent(file)}" 
                    class="btn btn-sm btn-outline-primary" target="_blank">
                    <i class="bi bi-download"></i>
                </a>
            </li>
            `).join('')
        : `<p class="text-muted">No files</p>`;

    panelBody.innerHTML = `
        <div class="detail-section">
            <h6>${projectSettings.uuidName}</h6>
            <p>${escapeHtml(collection.collectionID)}</p>
        </div>

        <div class="detail-section">
            <h6>${projectSettings.managementCodeName}</h6>
            <p>${escapeHtml(collection.collectionMC)}</p>
        </div>
            
        <div class="detail-section">
            <h6>${projectSettings.categoryName}</h6>
            <p>${escapeHtml(collection.collectionCategory)}</p>
        </div>
            
        <div class="detail-section">
            <h6>${t('registration-date')}</h6>
            <p>${escapeHtml(collection.collectionRegistrationDate)}</p>
        </div>
            
        <div class="detail-section">
            <h6>${t('location')}</h6>
            <p>${escapeHtml(collection.collectionRealLocation)}</p>
        </div>
            
        <div class="detail-section">
            <h6>${t('inventory')}</h6>
            <p>${collection.collectionCurrentInventory !== null ? collection.collectionCurrentInventory : t('not-set')}</p>
        </div>
            
        <div class="detail-section">
            <h6>${t('inventory-status')}</h6>
            <p><span class="badge ${inventoryBadgeClass}">${inventoryStatusText}</span></p>
        </div>
            
        <div class="detail-section">
            <h6>${t('tags')}</h6>
            <div>
                ${collection.collectionTag1 && collection.collectionTag1 !== ' - ' ? `<p>${projectSettings.tag1Name || (currentLanguage === 'ja' ? 'タグ 1' : 'Tag 1')}: ${escapeHtml(collection.collectionTag1)}</p>` : ''}
                ${collection.collectionTag2 && collection.collectionTag2 !== ' - ' ? `<p>${projectSettings.tag2Name || (currentLanguage === 'ja' ? 'タグ 2' : 'Tag 2')}: ${escapeHtml(collection.collectionTag2)}</p>` : ''}
                ${collection.collectionTag3 && collection.collectionTag3 !== ' - ' ? `<p>${projectSettings.tag3Name || (currentLanguage === 'ja' ? 'タグ 3' : 'Tag 3')}: ${escapeHtml(collection.collectionTag3)}</p>` : ''}
                ${(!collection.collectionTag1 || collection.collectionTag1 === ' - ') &&
            (!collection.collectionTag2 || collection.collectionTag2 === ' - ') &&
            (!collection.collectionTag3 || collection.collectionTag3 === ' - ') ? `<p>${t('not-set')}</p>` : ''}
            </div>
        </div>
            
        ${collection.comment ? `
            <div class="detail-section">
                <h6>${t('comment')}</h6>
                <div class="border rounded p-3" style="white-space: pre-wrap; background-color: #f8f9fa;">${escapeHtml(collection.comment)}</div>
            </div>
        ` : ''}
            
        <div class="detail-section">
            <h6>${t('images')}</h6>
            ${imagesHtml}
        </div>
            
        <div class="detail-section">
            <h6>${t('files')}</h6>
            <ul class="list-group">
                ${filesHtml}
            </ul>
        </div>
    `;

    // パネルとオーバーレイを表示
    overlay.classList.add('show');
    setTimeout(() => {
        panel.classList.add('open');
    }, 10);

    // 画像がある場合はカルーセル制御を設定
    if (images.length > 0) {
        setTimeout(() => {
            const carouselImage = document.getElementById('carouselImage');
            const imageCounter = document.getElementById('imageCounter');
            const imageName = document.getElementById('imageName');
            const prevBtn = document.getElementById('prevImage');
            const nextBtn = document.getElementById('nextImage');

            if (!carouselImage || !imageCounter || !imageName || !prevBtn || !nextBtn) {
                return;
            }

            function updateImage() {
                carouselImage.src = `/api/File/${encodeURIComponent(collection.collectionID)}/${encodeURIComponent(images[currentImageIndex])}`;
                carouselImage.alt = images[currentImageIndex];
                imageCounter.textContent = `${currentImageIndex + 1} / ${images.length}`;
                imageName.textContent = images[currentImageIndex];
            }

            const prevHandler = () => {
                currentImageIndex = (currentImageIndex - 1 + images.length) % images.length;
                updateImage();
            };

            const nextHandler = () => {
                currentImageIndex = (currentImageIndex + 1) % images.length;
                updateImage();
            };

            prevBtn.addEventListener('click', prevHandler);
            nextBtn.addEventListener('click', nextHandler);

            // WeakMap を使ってクリーンアップ用のハンドラを保存
            panelEventHandlers.set(panel, {
                prevBtn: prevBtn,
                nextBtn: nextBtn,
                prevHandler: prevHandler,
                nextHandler: nextHandler
            });
        }, 100);
    }
}

function cleanupPanelEventListeners(panel) {
    // カルーセルのイベントリスナがあれば削除
    const handlers = panelEventHandlers.get(panel);
    if (handlers) {
        const { prevBtn, nextBtn, prevHandler, nextHandler } = handlers;
        if (prevBtn) prevBtn.removeEventListener('click', prevHandler);
        if (nextBtn) nextBtn.removeEventListener('click', nextHandler);
        panelEventHandlers.delete(panel);
    }
}

function closeDetailPanel() {
    const panel = document.getElementById('detailPanel');
    const overlay = document.getElementById('detailPanelOverlay');

    // イベントリスナをクリーンアップ
    cleanupPanelEventListeners(panel);

    panel.classList.remove('open');
    overlay.classList.remove('show');
}

function updatePagination(result) {
    const pagination = document.getElementById('pagination');

    if (result.totalPages <= 1) {
        pagination.innerHTML = '';
        return;
    }

    let paginationHtml = '<nav><ul class="pagination">';

    // 「前へ」ボタン
    if (result.page > 1) {
        paginationHtml += `
            <li class="page-item">
                <a class="page-link" href="#" data-page="${result.page - 1}">${t('previous')}</a>
            </li>
        `;
    }

    // ページ番号
    const startPage = Math.max(1, result.page - 2);
    const endPage = Math.min(result.totalPages, result.page + 2);

    for (let i = startPage; i <= endPage; i++) {
        paginationHtml += `
            <li class="page-item ${i === result.page ? 'active' : ''}">
                <a class="page-link" href="#" data-page="${i}">${i}</a>
            </li>
        `;
    }

    // 「次へ」ボタン
    if (result.page < result.totalPages) {
        paginationHtml += `
            <li class="page-item">
                <a class="page-link" href="#" data-page="${result.page + 1}">${t('next')}</a>
            </li>
        `;
    }

    paginationHtml += '</ul></nav>';
    pagination.innerHTML = paginationHtml;

    // ページネーションリンクにイベントリスナを追加
    const paginationLinks = pagination.querySelectorAll('a.page-link[data-page]');
    paginationLinks.forEach(link => {
        link.addEventListener('click', (e) => {
            e.preventDefault();
            const page = parseInt(link.getAttribute('data-page'));
            searchCollections(page);
        });
    });
}

function clearFilters() {
    const searchTextElement = document.getElementById('searchText');
    const searchFieldElement = document.getElementById('searchField');
    const searchMethodElement = document.getElementById('searchMethod');
    const inventoryStatusElement = document.getElementById('inventoryStatusFilter');

    if (searchTextElement) searchTextElement.value = '';
    if (searchFieldElement) searchFieldElement.value = '0';
    if (searchMethodElement) searchMethodElement.value = '0';
    if (inventoryStatusElement) inventoryStatusElement.value = '';

    searchCollections();
}

function showLoading(show) {
    document.getElementById('loading').style.display = show ? 'block' : 'none';
}

function hideError() {
    document.getElementById('error').style.display = 'none';
}

function showError(message) {
    const errorElement = document.getElementById('error');
    errorElement.textContent = message;
    errorElement.style.display = 'block';
}

function toggleLanguage() {
    currentLanguage = currentLanguage === 'ja' ? 'en' : 'ja';
    updateUILanguage();
}

function t(key) {
    return translations[currentLanguage][key] || key;
}

function escapeHtml(text) {
    const map = {
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#039;'
    };
    return text.toString().replace(/[&<>"']/g, function (m) { return map[m]; });
}
