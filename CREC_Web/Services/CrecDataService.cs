/*
CREC Web - Data Reader Service
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Extensions;
using CREC_Web.Models;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.Json;

namespace CREC_Web.Services
{
    /// <summary>
    /// CRECデータ読み込みサービス
    /// </summary>
    public class CrecDataService
    {
        private readonly ILogger<CrecDataService> _logger;
        private readonly string _dataFolderPath;
        private readonly List<CollectionData> _collectionsCache = new();
        private DateTime _lastCacheUpdate = DateTime.MinValue;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);

        // クラススコープにキャッシュ用JsonSerializerOptionsを追加
        private static readonly JsonSerializerOptions _indexDataJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public CrecDataService(ILogger<CrecDataService> logger, IConfiguration configuration)
        {
            _logger = logger;
            // プラグインとして実行される場合、WorkingDirectoryがデータフォルダに設定される
            // コマンドライン引数で.crecファイルが指定された場合はそこからパスを取得
            _dataFolderPath = configuration["ProjectDataPath"] ?? Environment.CurrentDirectory;
            _logger.LogInformation($"Data folder path: {_dataFolderPath}");
        }

        /// <summary>
        /// 全てのコレクションデータを取得
        /// </summary>
        public async Task<List<CollectionData>> GetAllCollectionsAsync()
        {
            // キャッシュが有効な場合はキャッシュを返す
            if (_collectionsCache.Any() && DateTime.Now - _lastCacheUpdate < _cacheExpiry)
            {
                _logger.LogInformation($"Returning {_collectionsCache.Count} collections from cache");
                return _collectionsCache;
            }

            _collectionsCache.Clear();

            try
            {
                _logger.LogInformation($"Loading collections from data folder: {_dataFolderPath}");
                _logger.LogInformation($"Data folder exists: {Directory.Exists(_dataFolderPath)}");

                if (!Directory.Exists(_dataFolderPath))
                {
                    _logger.LogWarning($"Data folder does not exist: {_dataFolderPath}");
                    return _collectionsCache;
                }

                // データフォルダ内のサブフォルダを検索
                var directories = Directory.GetDirectories(_dataFolderPath);
                _logger.LogInformation($"Found {directories.Length} subdirectories in data folder");

                foreach (var dir in directories)
                {
                    _logger.LogInformation($"  - {Path.GetFileName(dir)}");
                }

                // $SystemDataフォルダを除外
                directories = directories.Where(dir =>
                    !Path.GetFileName(dir).Equals("$SystemData", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                _logger.LogInformation($"After filtering: {directories.Length} collection folders");

                var tasks = directories.Select(async dir => await LoadCollectionFromDirectoryAsync(dir));
                var collections = await Task.WhenAll(tasks);

                var validCollections = collections.Where(c => c != null).Cast<CollectionData>().ToList();
                _logger.LogInformation($"Successfully loaded {validCollections.Count} collections");

                _collectionsCache.AddRange(validCollections);
                _lastCacheUpdate = DateTime.Now;

                _logger.LogInformation($"Total collections in cache: {_collectionsCache.Count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading collections");
            }

            return _collectionsCache;
        }

        /// <summary>
        /// 指定されたディレクトリからコレクションデータを読み込み
        /// </summary>
        private async Task<CollectionData?> LoadCollectionFromDirectoryAsync(string directoryPath)
        {
            try
            {
                _logger.LogDebug($"Loading collection from: {directoryPath}");

                // SystemData/index.json を探す
                var indexFilePath = Path.Combine(directoryPath, "SystemData", "index.json");

                if (!File.Exists(indexFilePath))
                {
                    // index.jsonが存在しない場合、フォルダ名をIDとして基本的なデータを作成
                    _logger.LogWarning($"No index.json found in {indexFilePath}, creating basic collection data");
                    return CreateBasicCollectionData(directoryPath);
                }

                // index.jsonを読み込み
                var collection = new CollectionData();
                var jsonContent = await File.ReadAllTextAsync(indexFilePath, Encoding.UTF8);
                try
                {
                    var indexData = JsonSerializer.Deserialize<IndexData>(jsonContent, _indexDataJsonOptions);
                    if (indexData is null)
                    {
                        _logger.LogWarning($"Failed to deserialize index.json in {indexFilePath}: Null result");
                        return CreateBasicCollectionData(directoryPath);
                    }
                    collection.IndexData = indexData;
                    _logger.LogDebug($"Successfully deserialized index.json in {indexFilePath}");
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError(jsonEx, $"Failed to parse index.json in {indexFilePath}: Invalid JSON format");
                    return CreateBasicCollectionData(directoryPath);
                }

                collection.CollectionFolderPath = directoryPath;// コレクションフォルダパスを設定

                LoadInventoryData(collection, directoryPath);// 在庫情報を読み込み
                
                LoadFileList(collection, directoryPath);// 画像ファイルとその他のファイルを検索

                return collection;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading collection from {directoryPath}");
                return null;
            }
        }

        /// <summary>
        /// 基本的なコレクションデータを作成（index.jsonが存在しない場合）
        /// </summary>
        private CollectionData CreateBasicCollectionData(string directoryPath)
        {
            var collection = new CollectionData
            {
                CollectionFolderPath = directoryPath,
                IndexData = new IndexData
                {
                    SystemData = new IndexSystemData
                    {
                        Id = Path.GetFileName(directoryPath)
                    },
                    Values = new IndexValues
                    {
                        Name = " - ",
                        ManagementCode = " - ",
                        RegistrationDate = " - ",
                        Category = " - ",
                        FirstTag = " - ",
                        SecondTag = " - ",
                        ThirdTag = " - ",
                        Location = " - "
                    }
                }
            };

            LoadFileList(collection, directoryPath);
            return collection;
        }

        /// <summary>
        /// 在庫情報を読み込み
        /// </summary>
        private void LoadInventoryData(CollectionData collection, string directoryPath)
        {
            var inventoryFilePath = Path.Combine(directoryPath, "SystemData", "inventory.json");
            if (!File.Exists(inventoryFilePath))
            {
                _logger.LogWarning($"{inventoryFilePath} does not exist.");
                collection.CollectionCurrentInventory = null;
                collection.CollectionInventoryStatus = InventoryStatus.NotSet;
                return;
            }

            try
            {
                string json = File.ReadAllText(inventoryFilePath, Encoding.UTF8);
                var serializer = new DataContractJsonSerializer(typeof(InventoryData));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    var inventoryData = serializer.ReadObject(stream) as InventoryData;
                    if (inventoryData != null)
                    {
                        collection.InventoryData = inventoryData;
                        collection.CollectionCurrentInventory = inventoryData.CalculateCurrentInventory();
                        collection.CollectionInventoryStatus = inventoryData.GetInventoryStatus(collection.CollectionCurrentInventory);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Error loading inventory data from {inventoryFilePath}");
                collection.CollectionCurrentInventory = null;
                collection.CollectionInventoryStatus = InventoryStatus.NotSet;
                return;
            }
        }

        /// <summary>
        /// ディレクトリ内のファイルリストを読み込み
        /// </summary>
        private void LoadFileList(CollectionData collection, string directoryPath)
        {
            try
            {
                // まずSystemData/Thumbnail.pngをチェック（優先）
                var systemDataThumbnail = Path.Combine(directoryPath, "SystemData", "Thumbnail.png");
                if (File.Exists(systemDataThumbnail))
                {
                    collection.ThumbnailPath = "SystemData/Thumbnail.png";
                    _logger.LogDebug($"Found thumbnail: {systemDataThumbnail}");
                }

                var files = Directory.GetFiles(directoryPath);
                var imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff" };

                // picturesフォルダから画像を読み込む
                // CREC構造: {dataPath}\{collectionId}\pictures\
                var picturesPath = Path.Combine(directoryPath, "pictures");
                if (Directory.Exists(picturesPath))
                {
                    _logger.LogInformation($"Loading images from pictures folder: {picturesPath}");
                    var pictureFiles = Directory.GetFiles(picturesPath);

                    foreach (var file in pictureFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var extension = Path.GetExtension(file).ToLowerInvariant();

                        if (imageExtensions.Contains(extension))
                        {
                            // picturesフォルダの画像を追加（重複チェック）
                            if (!collection.ImageFiles.Contains(fileName))
                            {
                                collection.ImageFiles.Add(fileName);
                                _logger.LogDebug($"Added image from pictures folder: {fileName}");
                            }
                        }
                    }
                }

                // dataフォルダからデータファイルを読み込む
                // CREC構造: {dataPath}\{collectionId}\data\
                var dataPath = Path.Combine(directoryPath, "data");
                if (Directory.Exists(dataPath))
                {
                    _logger.LogInformation($"Loading data files from data folder: {dataPath}");
                    var dataFiles = Directory.GetFiles(dataPath);

                    foreach (var file in dataFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var extension = Path.GetExtension(file).ToLowerInvariant();

                        if (!collection.OtherFiles.Contains(fileName))
                        {
                            collection.OtherFiles.Add(fileName);
                            _logger.LogDebug($"Added data file from data folder: {fileName}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading file list from {directoryPath}");
            }
        }

        /// <summary>
        /// 検索条件に基づいてコレクションを検索
        /// </summary>
        public async Task<SearchResult> SearchCollectionsAsync(SearchCriteria criteria)
        {
            var allCollections = await GetAllCollectionsAsync();
            _logger.LogInformation($"SearchCollectionsAsync: Total collections loaded: {allCollections.Count}");

            var filteredCollections = allCollections.AsQueryable();

            // テキスト検索
            if (!string.IsNullOrWhiteSpace(criteria.SearchText))
            {
                _logger.LogInformation("Filtering by search text: '{SearchText}', Field: {SearchField}, Method: {SearchMethod}",
                    criteria.SearchText.SanitizeForLog(), criteria.SearchField, criteria.SearchMethod);
                filteredCollections = filteredCollections.Where(c =>
                    MatchesSearchCriteria(c, criteria.SearchText, criteria.SearchField, criteria.SearchMethod));
                _logger.LogInformation("After text search: {Count} collections match", filteredCollections.Count());
            }
            else
            {
                _logger.LogInformation("No search text provided, showing all collections");
            }

            // 在庫状況フィルタ
            if (criteria.InventoryStatus.HasValue)
            {
                _logger.LogInformation($"Filtering by inventory status: {criteria.InventoryStatus.Value}");
                filteredCollections = filteredCollections.Where(c =>
                    c.CollectionInventoryStatus == criteria.InventoryStatus.Value);
                _logger.LogInformation($"After inventory filter: {filteredCollections.Count()} collections match");
            }

            var totalCount = filteredCollections.Count();
            _logger.LogInformation($"Total filtered collections: {totalCount}");

            var pagedCollections = filteredCollections
                .Skip((criteria.Page - 1) * criteria.PageSize)
                .Take(criteria.PageSize)
                .ToList();

            _logger.LogInformation($"Returning {pagedCollections.Count} collections for page {criteria.Page}");

            return new SearchResult
            {
                Collections = pagedCollections,
                TotalCount = totalCount,
                Page = criteria.Page,
                PageSize = criteria.PageSize
            };
        }

        /// <summary>
        /// コレクションが検索条件にマッチするかをチェック
        /// </summary>
        private bool MatchesSearchCriteria(CollectionData collection, string searchText, SearchField searchField, SearchMethod searchMethod)
        {
            // 検索対象フィールドの値を取得
            var fieldsToSearch = new List<string>();

            switch (searchField)
            {
                case SearchField.All:
                    fieldsToSearch.Add(collection.IndexData.SystemData.Id);
                    fieldsToSearch.Add(collection.IndexData.Values.Name);
                    fieldsToSearch.Add(collection.IndexData.Values.ManagementCode);
                    fieldsToSearch.Add(collection.IndexData.Values.Category);
                    fieldsToSearch.Add(collection.IndexData.Values.FirstTag);
                    fieldsToSearch.Add(collection.IndexData.Values.SecondTag);
                    fieldsToSearch.Add(collection.IndexData.Values.ThirdTag);
                    fieldsToSearch.Add(collection.IndexData.Values.Location);
                    break;
                case SearchField.ID:
                    fieldsToSearch.Add(collection.IndexData.SystemData.Id);
                    break;
                case SearchField.Name:
                    fieldsToSearch.Add(collection.IndexData.Values.Name);
                    break;
                case SearchField.ManagementCode:
                    fieldsToSearch.Add(collection.IndexData.Values.ManagementCode);
                    break;
                case SearchField.Category:
                    fieldsToSearch.Add(collection.IndexData.Values.Category);
                    break;
                case SearchField.Tag:
                    fieldsToSearch.Add(collection.IndexData.Values.FirstTag);
                    fieldsToSearch.Add(collection.IndexData.Values.SecondTag);
                    fieldsToSearch.Add(collection.IndexData.Values.ThirdTag);
                    break;
                case SearchField.Tag1:
                    fieldsToSearch.Add(collection.IndexData.Values.FirstTag);
                    break;
                case SearchField.Tag2:
                    fieldsToSearch.Add(collection.IndexData.Values.SecondTag);
                    break;
                case SearchField.Tag3:
                    fieldsToSearch.Add(collection.IndexData.Values.ThirdTag);
                    break;
                case SearchField.Location:
                    fieldsToSearch.Add(collection.IndexData.Values.Location);
                    break;
            }

            // 検索方式に応じてマッチングを行う
            foreach (var fieldValue in fieldsToSearch)
            {
                if (string.IsNullOrEmpty(fieldValue)) continue;

                bool matches = searchMethod switch
                {
                    SearchMethod.Prefix => fieldValue.StartsWith(searchText, StringComparison.OrdinalIgnoreCase),
                    SearchMethod.Suffix => fieldValue.EndsWith(searchText, StringComparison.OrdinalIgnoreCase),
                    SearchMethod.Exact => fieldValue.Equals(searchText, StringComparison.OrdinalIgnoreCase),
                    SearchMethod.Partial => fieldValue.Contains(searchText, StringComparison.OrdinalIgnoreCase),
                    _ => false
                };

                if (matches) return true;
            }

            return false;
        }

        /// <summary>
        /// IDによるコレクション取得
        /// </summary>
        public async Task<CollectionData?> GetCollectionByIdAsync(string id)
        {
            var collections = await GetAllCollectionsAsync();
            return collections.FirstOrDefault(c => c.IndexData.SystemData.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 利用可能なカテゴリ一覧を取得
        /// </summary>
        public async Task<List<string>> GetCategoriesAsync()
        {
            var collections = await GetAllCollectionsAsync();
            return collections
                .Select(c => c.IndexData.Values.Category)
                .Where(cat => !string.IsNullOrWhiteSpace(cat) && cat != " - ")
                .Distinct()
                .OrderBy(cat => cat)
                .ToList();
        }

        /// <summary>
        /// 利用可能なタグ一覧を取得
        /// </summary>
        public async Task<List<string>> GetTagsAsync()
        {
            var collections = await GetAllCollectionsAsync();
            var tags = new List<string>();

            tags.AddRange(collections.Select(c => c.IndexData.Values.FirstTag));
            tags.AddRange(collections.Select(c => c.IndexData.Values.SecondTag));
            tags.AddRange(collections.Select(c => c.IndexData.Values.ThirdTag));

            return tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && tag != " - ")
                .Distinct()
                .OrderBy(tag => tag)
                .ToList();
        }

        /// <summary>
        /// コレクションリストのキャッシュをクリア
        /// </summary>
        public void ClearCollectionsListCache()
        {
            _collectionsCache.Clear();
            _lastCacheUpdate = DateTime.MinValue; // 最終キャッシュ更新時刻を最小値（実質的に「初期化されていない」状態）にリセット
            _logger.LogInformation("Collections list cache cleared");
        }

        /// <summary>
        /// 特定コレクションの画像リストキャッシュのみクリア（全体キャッシュは維持）
        /// </summary>
        /// <param name="id">コレクションID</param>
        public void RefreshCollectionImageFileCache(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return;

            var collection = _collectionsCache.FirstOrDefault(c =>
                c.IndexData.SystemData.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

            if (collection != null)
            {
                collection.ImageFiles.Clear();
                LoadFileList(collection, collection.CollectionFolderPath);
                _logger.LogInformation("File cache refreshed for collection {CollectionId}", id);
            }
        }
    }
}