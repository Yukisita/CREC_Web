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
let currentViewMode = 'table'; // 'table' or 'grid'
let lastIsMobile; // 画面サイズ変更前のMobile検知
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

// アニメーション遅延時間（ミリ秒）
const ANIMATION_DELAY = 10

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

// パネル用イベントハンドラを格納する WeakMap
const panelEventHandlers = new WeakMap();

// 言語翻訳
const translations = {
    ja: {
        'loading': '読み込み中...',
        'search-results': '検索結果',
        'items-found': '件見つかりました',
        'no-results': '検索結果がありません',
        'error-loading': 'データの読み込みでエラーが発生しました',
        'registration-date': '登録日',
        'location': '場所',
        'inventory': '在庫数',
        'inventory-status': '在庫状況',
        'comment': 'コメント',
        'images': '画像',
        'files': 'ファイル',
        'stock-out': '在庫切れ',
        'under-stocked': '在庫不足',
        'appropriate-need-reorder': '在庫適正（発注必要）',
        'appropriate': '在庫適正',
        'over-stocked': '在庫過剰',
        'not-set': '未設定',
        'order-quantity': '発注数',
        'excess-quantity': '超過数',
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
        'field-firstTag': 'タグ 1',
        'field-secondTag': 'タグ 2',
        'field-thirdTag': 'タグ 3',
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
        'close': '閉じる',
        'view-details': '詳細を見る',
        'no-thumbnail': 'サムネイルなし',
        'grid-view': 'グリッド',
        'table-view': 'テーブル'
    },
    en: {
        'loading': 'Loading...',
        'search-results': 'Search Results',
        'items-found': 'items found',
        'no-results': 'No results found',
        'error-loading': 'Error loading data',
        'registration-date': 'Registration Date',
        'location': 'Location',
        'inventory': 'Inventory',
        'inventory-status': 'Inventory Status',
        'comment': 'Comment',
        'images': 'Images',
        'files': 'Files',
        'stock-out': 'Stock Out',
        'under-stocked': 'Under Stocked',
        'appropriate-need-reorder': 'Appropriate (Need Reorder)',
        'appropriate': 'Appropriate',
        'over-stocked': 'Over Stocked',
        'not-set': 'Not Set',
        'order-quantity': 'Order Quantity',
        'excess-quantity': 'Excess Quantity',
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
        'field-tags': 'Tags',
        'field-firstTag': 'Tag 1',
        'field-secondTag': 'Tag 2',
        'field-thirdTag': 'Tag 3',
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
        'close': 'Close',
        'view-details': 'View Details',
        'no-thumbnail': 'No Thumbnail',
        'grid-view': 'Grid',
        'table-view': 'Table'
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

        // イベントリスナの一括設定
        setupEventListeners([
            { id: 'searchButton', event: 'click', handler: () => searchCollections() },// 検索ボタンのイベントリスナ
            { id: 'clearFiltersButton', event: 'click', handler: clearFilters },// フィルタクリアボタンのイベントリスナ
            { id: 'languageToggle', event: 'click', handler: toggleLanguage },// 言語切り替えボタンのイベントリスナ
            { id: 'detailPanelOverlay', event: 'click', handler: closeDetailPanel },// 詳細パネルオープンのイベントリスナ
            { id: 'detailPanelClose', event: 'click', handler: closeDetailPanel },// 詳細パネルクローズのイベントリスナ
            { id: 'gridViewBtn', event: 'click', handler: switchToGridView },// グリッド表示ボタンのイベントリスナ
            { id: 'tableViewBtn', event: 'click', handler: switchToTableView }// テーブル表示ボタンのイベントリスナ
        ]);

        // 保存された表示モードの読み込み
        const savedViewMode = localStorage.getItem('crec_view_mode');
        if (savedViewMode === 'grid' || savedViewMode === 'table') {
            currentViewMode = savedViewMode;
        }
        else {
            // 保存された表示モードが無効または存在しない場合、画面幅に基づいて決定
            const isMobile = window.innerWidth < getMobileBreakpoint();

            // 画面幅が閾値未満となった場合はグリッド表示に変更
            if (isMobile && currentViewMode === 'table') {
                currentViewMode = 'grid';
            }
            // 画面幅が閾値以上となった場合はテーブル表示に変更
            else if (!isMobile && currentViewMode === 'grid') {
                currentViewMode = 'table';
            }
        }

        // 初回の画面サイズからモバイルフラグを設定
        lastIsMobile = window.innerWidth < getMobileBreakpoint();

        // ウィンドウリサイズイベントハンドラ登録
        window.addEventListener('resize', handleWindowResize);

        // 列リサイズ機能のセットアップ
        setupColumnResizers();

        // テーブルヘッダーのテキストを更新
        updateTableHeaders();

        // 初回検索
        await searchCollections();
        console.log('App initialized successfully');
    } catch (error) {
        console.error('Error initializing app:', error);
        showError('Failed to initialize application: ' + error.message);
    }
}

// テーブルヘッダーのテキストを更新
function updateTableHeaders() {
    const table = document.querySelector('.collections-table');
    if (!table) {
        return;
    }

    const thead = table.querySelector('thead');
    const ths = thead.querySelectorAll('th.resizable');

    // プロジェクト設定でテーブルヘッダーを更新
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
}

// 列リサイズ機能のセットアップ（初回のみ実行）
function setupColumnResizers() {
    const table = document.querySelector('.collections-table');
    if (!table) {
        console.log('Table not found for column resizing');
        return;
    }

    const thead = table.querySelector('thead');
    const ths = thead.querySelectorAll('th.resizable');

    console.log(`Setting up column resizers for ${ths.length} columns`);

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
            const newWidth = Math.max(MIN_COLUMN_WIDTH, startWidth + diff);
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
            // translationsの内容をプロジェクト設定値に合うように更新
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
            // field-firstTag
            translations.ja['field-firstTag'] = projectSettings.tag1Name;
            translations.en['field-firstTag'] = projectSettings.tag1Name;
            // field-secondTag
            translations.ja['field-secondTag'] = projectSettings.tag2Name;
            translations.en['field-secondTag'] = projectSettings.tag2Name;
            // field-thirdTag
            translations.ja['field-thirdTag'] = projectSettings.tag3Name;
            translations.en['field-thirdTag'] = projectSettings.tag3Name;
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
        
        // Store results for view switching
        window.lastSearchResult = result;
        
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
    const gridContainer = document.getElementById('collectionsGridContainer');
    const tableBody = document.getElementById('collectionsTable');
    const summary = document.getElementById('resultsSummary');
    const resultsText = document.getElementById('resultsText');

    // 以前の結果をクリア
    tableBody.innerHTML = '';
    gridContainer.innerHTML = '';

    if (result.collections.length === 0) {
        if (currentViewMode === 'table') {
            tableBody.innerHTML = `
                <tr>
                    <td colspan="12" class="text-center py-5">
                        <i class="bi bi-search display-1 text-muted"></i>
                        <h4 class="mt-3 text-muted">${t('no-results')}</h4>
                    </td>
                </tr>
            `;
        } else {
            gridContainer.innerHTML = `
                <div class="col-12 text-center py-5">
                    <i class="bi bi-search display-1 text-muted"></i>
                    <h4 class="mt-3 text-muted">${t('no-results')}</h4>
                </div>
            `;
        }
        summary.style.display = 'none';
    } else {
        // サマリを更新
        resultsText.textContent = `${t('search-results')}: ${result.totalCount} ${t('items-found')}`;
        summary.style.display = 'block';

        // コレクションを表示
        if (currentViewMode === 'table') {
            result.collections.forEach(collection => {
                const row = createCollectionRow(collection);
                tableBody.appendChild(row);
            });
        } else {
            result.collections.forEach(collection => {
                const card = createCollectionCard(collection);
                gridContainer.appendChild(card);
            });
        }
    }

    // Show appropriate container
    if (currentViewMode === 'table') {
        tableContainer.style.display = 'block';
        gridContainer.style.display = 'none';
    } else {
        tableContainer.style.display = 'none';
        gridContainer.style.display = 'flex';
    }

    // Update view toggle buttons
    updateViewToggleButtons();
}

function updateViewToggleButtons() {
    const gridBtn = document.getElementById('gridViewBtn');
    const tableBtn = document.getElementById('tableViewBtn');
    
    if (!gridBtn || !tableBtn) return;

    // Update active state
    if (currentViewMode === 'grid') {
        gridBtn.classList.add('active');
        tableBtn.classList.remove('active');
    } else {
        gridBtn.classList.remove('active');
        tableBtn.classList.add('active');
    }
}

function switchToGridView() {
    currentViewMode = 'grid';
    localStorage.setItem('crec_view_mode', 'grid');
    
    // Re-render current results
    if (window.lastSearchResult) {
        displaySearchResults(window.lastSearchResult);
    }
}

function switchToTableView() {
    currentViewMode = 'table';
    localStorage.setItem('crec_view_mode', 'table');
    
    // Re-render current results
    if (window.lastSearchResult) {
        displaySearchResults(window.lastSearchResult);
    }
}

// ウィンドウリサイズ時の処理
function handleWindowResize() {
    const isMobile = window.innerWidth < getMobileBreakpoint();

    // lastIsMobile が未初期化なら基準を合わせて終了（意図しない切替を防止）
    if (typeof lastIsMobile === 'undefined') {
        lastIsMobile = isMobile;
        return;
    }

    // 閾値を跨いでいない場合は何もしない
    if (isMobile === lastIsMobile) {
        return;
    }

    // 状態更新（閾値を跨いだ）
    lastIsMobile = isMobile;

    // 自動切替
    if (isMobile && currentViewMode === 'table') {
        currentViewMode = 'grid';
    } else if (!isMobile && currentViewMode === 'grid') {
        currentViewMode = 'table';
    } else {
        return;
    }

    // 描画更新
    if (window.lastSearchResult) {
        displaySearchResults(window.lastSearchResult);
    }
}

function createCollectionRow(collection) {
    const row = document.createElement('tr');
    row.addEventListener('click', () => showCollectionDetails(collection.collectionID));

    const inventoryStatusText = getInventoryStatusText(
        collection.collectionInventoryStatus,
        collection.collectionCurrentInventory,
        collection.collectionSafetyStock,
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
    // 値は HTML を含むため innerHTML で挿入して改行を反映する
    statusCell.innerHTML = `<span class="badge ${inventoryBadgeClass}">${inventoryStatusText}</span>`;
    // title 属性には HTML を含まないプレーンテキストを設定（<br> 等を取り除く）
    statusCell.title = stripHtmlToText(inventoryStatusText);

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

function createCollectionCard(collection) {
    const colDiv = document.createElement('div');
    colDiv.className = 'col-12 col-sm-6 col-md-4 col-lg-3 col-xl-2 mb-4';

    const inventoryStatusText = getInventoryStatusText(
        collection.collectionInventoryStatus,
        collection.collectionCurrentInventory,
        collection.collectionSafetyStock,
        collection.collectionOrderPoint,
        collection.collectionMaxStock
    );
    const inventoryBadgeClass = getInventoryStatusBadgeClass(collection.collectionInventoryStatus);

    // Build tag HTML - display each tag on a separate line like category
    let tagsHtml = '';
    if (collection.collectionTag1 && collection.collectionTag1 !== ' - ') {
        const tag1Label = projectSettings.tag1Name || (currentLanguage === 'ja' ? 'タグ 1' : 'Tag 1');
        tagsHtml += `<small class="text-muted">${tag1Label}: ${escapeHtml(collection.collectionTag1)}</small><br>`;
    }
    if (collection.collectionTag2 && collection.collectionTag2 !== ' - ') {
        const tag2Label = projectSettings.tag2Name || (currentLanguage === 'ja' ? 'タグ 2' : 'Tag 2');
        tagsHtml += `<small class="text-muted">${tag2Label}: ${escapeHtml(collection.collectionTag2)}</small><br>`;
    }
    if (collection.collectionTag3 && collection.collectionTag3 !== ' - ') {
        const tag3Label = projectSettings.tag3Name || (currentLanguage === 'ja' ? 'タグ 3' : 'Tag 3');
        tagsHtml += `<small class="text-muted">${tag3Label}: ${escapeHtml(collection.collectionTag3)}</small><br>`;
    }

    // Use the collection ID (which is the folder name) for the thumbnail URL
    const collectionId = collection.collectionID || 'unknown';
    const thumbnailUrl = `/api/Files/thumbnail/${encodeURIComponent(collectionId)}`;
    
    const thumbnailImg = document.createElement('img');
    thumbnailImg.src = thumbnailUrl;
    thumbnailImg.className = 'card-img-top';
    thumbnailImg.alt = 'Thumbnail';
    thumbnailImg.style.display = 'block';

    const thumbnailPlaceholder = document.createElement('div');
    thumbnailPlaceholder.className = 'thumbnail-placeholder';
    thumbnailPlaceholder.style.display = 'none';
    thumbnailPlaceholder.innerHTML = `<i class="bi bi-image display-4"></i><br><small>${t('no-thumbnail')}</small>`;

    thumbnailImg.addEventListener('error', () => {
        thumbnailImg.style.display = 'none';
        thumbnailPlaceholder.style.display = 'flex';
    });

    const thumbnailContainer = document.createElement('div');
    thumbnailContainer.style.position = 'relative';
    thumbnailContainer.appendChild(thumbnailImg);
    thumbnailContainer.appendChild(thumbnailPlaceholder);

    const cardBody = document.createElement('div');
    cardBody.className = 'card-body';
    cardBody.innerHTML = `
        <h6 class="card-title">${escapeHtml(collection.collectionName)}</h6>
        <p class="card-text">
            <small class="text-muted">${projectSettings.uuidName}: ${escapeHtml(collection.collectionID)}</small><br>
            <small class="text-muted">${projectSettings.categoryName}: ${escapeHtml(collection.collectionCategory)}</small><br>
            ${tagsHtml}
            ${collection.collectionCurrentInventory !== null ? 
                `<small class="text-muted">${t('inventory')}: ${collection.collectionCurrentInventory}</small><br>` : ''}
            <span class="badge ${inventoryBadgeClass}">${inventoryStatusText}</span>
        </p>
    `;

    const detailsBtn = document.createElement('button');
    detailsBtn.className = 'btn btn-primary btn-sm w-100';
    detailsBtn.innerHTML = `<i class="bi bi-eye"></i> ${t('view-details')}`;
    detailsBtn.addEventListener('click', () => showCollectionDetails(collection.collectionID));

    const cardFooter = document.createElement('div');
    cardFooter.className = 'card-footer';
    cardFooter.appendChild(detailsBtn);

    const card = document.createElement('div');
    card.className = 'card h-100 collection-card';
    card.appendChild(thumbnailContainer);
    card.appendChild(cardBody);
    card.appendChild(cardFooter);

    colDiv.appendChild(card);
    return colDiv;
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
        collection.collectionSafetyStock,
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
    }, ANIMATION_DELAY);

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
        }, ANIMATION_DELAY);
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
    updateTableHeaders();
    // 現在の結果を新しい言語で再描画
    if (currentSearchCriteria && Object.keys(currentSearchCriteria).length > 0) {
        searchCollections(currentPage);
    }
}

function t(key) {
    return translations[currentLanguage][key] || key;
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
