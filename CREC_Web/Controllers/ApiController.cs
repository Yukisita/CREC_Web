/*
CREC Web - API Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Extensions;
using CREC_Web.Models;
using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CollectionsController : ControllerBase
    {
        private readonly CrecDataService _crecDataService;
        private readonly ILogger<CollectionsController> _logger;
        private readonly IConfiguration _configuration;

        public CollectionsController(CrecDataService crecDataService, ILogger<CollectionsController> logger, IConfiguration configuration)
        {
            _crecDataService = crecDataService;
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>
        /// コレクション検索
        /// </summary>
        [HttpGet("search")]
        public async Task<ActionResult<SearchResult>> Search([FromQuery] SearchCriteria criteria)
        {
            try
            {
                _logger.LogInformation("Search request: Text={SearchText}, Field={SearchField}, Method={SearchMethod}",
                    criteria.SearchText.SanitizeForLog(), criteria.SearchField, criteria.SearchMethod);
                var result = await _crecDataService.SearchCollectionsAsync(criteria);
                _logger.LogInformation("Search returned {Count} collections out of {TotalCount} total", result.Collections.Count, result.TotalCount);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching collections");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// 全コレクション取得
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<CollectionData>>> GetAll()
        {
            try
            {
                _logger.LogInformation("GetAll collections request");
                var collections = await _crecDataService.GetAllCollectionsAsync();
                _logger.LogInformation($"Returning {collections.Count} collections");
                return Ok(collections);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all collections");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// IDによるコレクション取得
        /// </summary>
        [HttpGet("{id}")]
        public async Task<ActionResult<CollectionData>> GetById(string id)
        {
            try
            {
                var collection = await _crecDataService.GetCollectionByIdAsync(id);
                if (collection == null)
                {
                    return NotFound($"Collection with ID '{id}' not found");
                }
                return Ok(collection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting collection with ID {id}", id.SanitizeForLog());
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// 新規コレクション作成
        /// </summary>
        [HttpPost]
        public async Task<ActionResult<object>> CreateCollection()
        {
            try
            {
                var newId = Guid.NewGuid().ToString();

                var configuredDataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
                var dataFolder = Path.GetFullPath(configuredDataFolder);
                var collectionFolder = Path.GetFullPath(Path.Combine(dataFolder, newId));

                // パストラバーサル防止
                var dataFolderWithSeparator =
                    dataFolder.EndsWith(Path.DirectorySeparatorChar) || dataFolder.EndsWith(Path.AltDirectorySeparatorChar)
                        ? dataFolder
                        : dataFolder + Path.DirectorySeparatorChar;
                if (!collectionFolder.StartsWith(dataFolderWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Access denied");
                }

                var systemDataFolder = Path.Combine(collectionFolder, "SystemData");
                Directory.CreateDirectory(systemDataFolder);

                var indexData = new IndexData
                {
                    SystemData = new IndexSystemData
                    {
                        Id = newId,
                        SystemCreateDate = DateTimeOffset.UtcNow.ToString("o")
                    },
                    Values = new IndexValues()
                };

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var json = System.Text.Json.JsonSerializer.Serialize(indexData, options);
                var indexFilePath = Path.Combine(systemDataFolder, "index.json");
                await System.IO.File.WriteAllTextAsync(indexFilePath, json, System.Text.Encoding.UTF8);

                _crecDataService.ClearCollectionsListCache();

                _logger.LogInformation("New collection created with ID {CollectionId}", newId);

                return Ok(new { id = newId });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating new collection");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// 利用可能なカテゴリ一覧取得
        /// </summary>
        [HttpGet("categories")]
        public async Task<ActionResult<List<string>>> GetCategories()
        {
            try
            {
                var categories = await _crecDataService.GetCategoriesAsync();
                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting categories");
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// 利用可能なタグ一覧取得
        /// </summary>
        [HttpGet("tags")]
        public async Task<ActionResult<List<string>>> GetTags()
        {
            try
            {
                var tags = await _crecDataService.GetTagsAsync();
                return Ok(tags);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting tags");
                return StatusCode(500, "Internal server error");
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class InventoryController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<InventoryController> _logger;
        private readonly CrecDataService _crecDataService;

        public InventoryController(IConfiguration configuration, ILogger<InventoryController> logger, CrecDataService crecDataService)
        {
            _configuration = configuration;
            _logger = logger;
            _crecDataService = crecDataService;
        }

        /// <summary>
        /// 在庫操作リクエスト
        /// </summary>
        public class InventoryOperationRequest
        {
            // 在庫操作タイプ
            public InventoryOperationType OperationType { get; set; }

            // 数量（入庫は正、出庫は負）
            public long Quantity { get; set; }

            // 在庫操作コメント
            public string Note { get; set; } = string.Empty;
        }

        /// <summary>
        /// 在庫操作を追加
        /// </summary>
        [HttpPost("{collectionId}")]
        public async Task<IActionResult> AddInventoryOperation(string collectionId, [FromBody] InventoryOperationRequest request)
        {
            try
            {
                // コレクションIDの評価
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    collectionId.Contains("..") || collectionId.Contains("/") || collectionId.Contains("\\"))
                {
                    return BadRequest("Invalid collection ID");
                }

                // 在庫操作数の評価: JavaScriptのNumber.MAX_SAFE_INTEGER/MIN_SAFE_INTEGER範囲内か確認
                const long maxSafeInteger = 9007199254740991L;
                const long minSafeInteger = -9007199254740991L;
                if (request.Quantity > maxSafeInteger || request.Quantity < minSafeInteger)
                {
                    return BadRequest("Quantity is out of safe integer range");
                }

                // 在庫操作数の評価: 入庫は正の数、出庫は負の数
                if (request.OperationType == InventoryOperationType.EntryOperation && request.Quantity <= 0)
                {
                    return BadRequest("Entry operation must have a positive quantity");
                }
                if (request.OperationType == InventoryOperationType.ExitOperation && request.Quantity >= 0)
                {
                    return BadRequest("Exit operation must have a negative quantity");
                }

                // コレクションが存在するか確認
                var collection = await _crecDataService.GetCollectionByIdAsync(collectionId);
                if (collection == null)
                {
                    return NotFound($"Collection with ID '{collectionId}' not found");
                }

                var dataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
                var collectionFolder = Path.GetFullPath(Path.Combine(dataFolder, collectionId));
                var systemDataFolder = Path.Combine(collectionFolder, "SystemData");
                var inventoryFilePath = Path.Combine(systemDataFolder, "inventory.json");

                // パストラバーサル防止
                if (!collectionFolder.StartsWith(dataFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Access denied");
                }

                // SystemDataフォルダが存在しない場合は作成
                if (!Directory.Exists(systemDataFolder))
                {
                    Directory.CreateDirectory(systemDataFolder);
                }

                // 既存のinventory.jsonを読み込むか新規作成
                InventoryData inventoryData;
                if (System.IO.File.Exists(inventoryFilePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(inventoryFilePath, System.Text.Encoding.UTF8);
                    var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(InventoryData));
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    {
                        inventoryData = serializer.ReadObject(stream) as InventoryData ?? new InventoryData();
                    }
                }
                else
                {
                    inventoryData = new InventoryData
                    {
                        MetaData = new InventoryMetaData(collectionId),
                        Setting = new InventoryOperationSetting(),
                        Operations = new List<InventoryOperationRecord>()
                    };
                }

                // 新しい操作を追加
                var newOperation = new InventoryOperationRecord
                {
                    DateTime = DateTimeOffset.UtcNow.ToString("o"),
                    OperationType = request.OperationType,
                    Quantity = request.Quantity,
                    Note = request.Note ?? string.Empty
                };
                inventoryData.Operations.Add(newOperation);

                // inventory.jsonを保存
                var serializerWrite = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(InventoryData));
                using (var stream = new MemoryStream())
                {
                    serializerWrite.WriteObject(stream, inventoryData);
                    var jsonBytes = stream.ToArray();
                    var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes); // UTF-8（BOMなし）を明示的に指定
                    await System.IO.File.WriteAllTextAsync(inventoryFilePath, jsonString, System.Text.Encoding.UTF8);
                }

                // 在庫操作のログを出力
                _logger.LogInformation("Inventory operation added for collection {CollectionId}: Type={OperationType}, Quantity={Quantity}",
                    collectionId.SanitizeForLog(), request.OperationType, request.Quantity);

                // コレクションリストのキャッシュをクリア
                _crecDataService.ClearCollectionsListCache();

                // 成功を返す
                return Ok(new { message = "Inventory operation saved successfully" });
            }
            catch (Exception ex)
            {
                // エラーログを出力
                _logger.LogError(ex, "Error adding inventory operation for collection {CollectionId}", collectionId.SanitizeForLog());

                // 500エラーを返す
                return StatusCode(500, "Internal server error: " + ex.Message);
            }
        }

        /// <summary>
        /// 在庫管理設定値を保存
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="settings">在庫管理設定値</param>
        [HttpPost("Settings/{collectionId}")]
        public async Task<IActionResult> SaveInventorySettings(string collectionId, [FromBody] InventoryOperationSetting settings)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(collectionId) || settings == null)
                {
                    return BadRequest("Invalid request");
                }

                // コレクションが存在するか確認
                var collection = await _crecDataService.GetCollectionByIdAsync(collectionId);
                if (collection == null)
                {
                    return NotFound($"Collection with ID '{collectionId}' not found");
                }

                // データフォルダの取得
                var dataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
                var collectionFolder = Path.GetFullPath(Path.Combine(dataFolder, collectionId));
                var systemDataFolder = Path.Combine(collectionFolder, "SystemData");
                var inventoryFilePath = Path.Combine(systemDataFolder, "inventory.json");

                // パストラバーサル防止
                if (!collectionFolder.StartsWith(dataFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Access denied");
                }

                // SystemDataフォルダが存在しない場合は作成
                if (!Directory.Exists(systemDataFolder))
                {
                    Directory.CreateDirectory(systemDataFolder);
                }

                // 既存のinventory.jsonを読み込むか新規作成
                InventoryData inventoryData;
                if (System.IO.File.Exists(inventoryFilePath))
                {
                    var json = await System.IO.File.ReadAllTextAsync(inventoryFilePath, System.Text.Encoding.UTF8);
                    var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(InventoryData));
                    using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
                    {
                        inventoryData = serializer.ReadObject(stream) as InventoryData ?? new InventoryData();
                    }
                }
                else
                {
                    inventoryData = new InventoryData
                    {
                        MetaData = new InventoryMetaData(collectionId),
                        Setting = new InventoryOperationSetting(),
                        Operations = new List<InventoryOperationRecord>()
                    };
                }

                // 在庫管理設定値の範囲確認(範囲: -9007199254740991 ~ 9007199254740991, null許容)
                const long maxSafeInteger = 9007199254740991L;
                const long minSafeInteger = -9007199254740991L;
                if (settings.SafetyStock.HasValue &&
                    (settings.SafetyStock.Value > maxSafeInteger || settings.SafetyStock.Value < minSafeInteger))
                {
                    return BadRequest("SafetyStock is out of safe integer range");
                }
                if (settings.ReorderPoint.HasValue &&
                    (settings.ReorderPoint.Value > maxSafeInteger || settings.ReorderPoint.Value < minSafeInteger))
                {
                    return BadRequest("ReorderPoint is out of safe integer range");
                }
                if (settings.MaximumLevel.HasValue &&
                    (settings.MaximumLevel.Value > maxSafeInteger || settings.MaximumLevel.Value < minSafeInteger))
                {
                    return BadRequest("MaximumLevel is out of safe integer range");
                }

                // 在庫管理設定値を更新
                inventoryData.Setting.SafetyStock = settings.SafetyStock;
                inventoryData.Setting.ReorderPoint = settings.ReorderPoint;
                inventoryData.Setting.MaximumLevel = settings.MaximumLevel;

                // inventory.jsonを保存
                var serializerWrite = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(InventoryData));
                using (var stream = new MemoryStream())
                {
                    serializerWrite.WriteObject(stream, inventoryData);
                    var jsonBytes = stream.ToArray();
                    var jsonString = System.Text.Encoding.UTF8.GetString(jsonBytes); // UTF-8（BOMなし）を明示的に指定
                    await System.IO.File.WriteAllTextAsync(inventoryFilePath, jsonString, System.Text.Encoding.UTF8);
                }

                // 在庫管理設定値の変更ログを出力
                _logger.LogInformation("Inventory settings saved for collection {CollectionId}: SafetyStock={SafetyStock}, ReorderPoint={ReorderPoint}, MaximumLevel={MaximumLevel}",
                    collectionId.SanitizeForLog(),
                    settings.SafetyStock?.ToString() ?? "null",
                    settings.ReorderPoint?.ToString() ?? "null",
                    settings.MaximumLevel?.ToString() ?? "null");

                // コレクションリストのキャッシュをクリア
                _crecDataService.ClearCollectionsListCache();

                // 成功を返す
                return Ok(new { message = "Inventory management settings saved successfully" });
            }
            catch (Exception ex)
            {
                // エラーログを出力
                _logger.LogError(ex, "Error saving inventory settings for collection {CollectionId}", collectionId.SanitizeForLog());

                // 500エラーを返す
                return StatusCode(500, "Internal server error: " + ex.Message);
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class FilesController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<FilesController> _logger;

        public FilesController(IConfiguration configuration, ILogger<FilesController> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// サムネイル画像取得
        /// </summary>
        [HttpGet("thumbnail/{collectionId}")]
        // 呼び出し例: /api/Files/thumbnail/{collectionId}
        public IActionResult GetThumbnail(string collectionId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    collectionId.Contains("..") || collectionId.Contains("/") || collectionId.Contains("\\"))
                {
                    return BadRequest("Invalid collection ID");
                }

                // CrecDataService と同じデータルートを使用（設定優先）
                var dataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                var collectionFolder = Path.GetFullPath(Path.Combine(dataFolder, collectionId));
                var thumbnailPath = Path.GetFullPath(Path.Combine(collectionFolder, "SystemData", "Thumbnail.png"));

                // パストラバーサル防止
                if (!thumbnailPath.StartsWith(collectionFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Access denied");
                }

                if (!System.IO.File.Exists(thumbnailPath))
                {
                    return NotFound($"Thumbnail not found for collection '{collectionId}'");
                }

                Response.Headers["Cache-Control"] = "public, max-age=3600";
                // img タグでインライン表示させるため、ファイル名は付与しない
                return PhysicalFile(thumbnailPath, "image/png");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting thumbnail for collection {collectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// ファイル取得（画像やその他ファイル）
        /// </summary>
        [HttpGet("{collectionId}/{fileName}")]
        public IActionResult GetFile(string collectionId, string fileName)
        {
            try
            {
                // collectionId もバリデーション（GetThumbnail と同等）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    collectionId.Contains("..") || collectionId.Contains("/") || collectionId.Contains("\\"))
                {
                    return BadRequest("Invalid collection ID");
                }

                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName.Contains("..") || fileName.Contains("/") || fileName.Contains("\\"))
                {
                    return BadRequest("Invalid file name");
                }

                var dataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
                var dataRoot = Path.GetFullPath(dataFolder);

                var collectionFolder = Path.GetFullPath(Path.Combine(dataRoot, collectionId));
                // collectionFolder が dataRoot 配下であることを確認
                var relCollection = Path.GetRelativePath(dataRoot, collectionFolder);
                if (relCollection.StartsWith("..", StringComparison.Ordinal))
                {
                    return BadRequest("Access denied");
                }

                var filePath = Path.GetFullPath(Path.Combine(collectionFolder, fileName));
                // filePath が collectionFolder 配下であることを確認
                var relFile = Path.GetRelativePath(collectionFolder, filePath);
                if (relFile.StartsWith("..", StringComparison.Ordinal))
                {
                    return BadRequest("Access denied");
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound($"File '{fileName}' not found in collection '{collectionId}'");
                }

                // MIME 判定をプロバイダーに委譲
                var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
                if (!provider.TryGetContentType(filePath, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                Response.Headers["Cache-Control"] = "public, max-age=3600";

                // Range 対応を有効化（動画/大きな画像で有用）
                return PhysicalFile(
                    filePath,
                    contentType,
                    fileDownloadName: null,
                    lastModified: null,
                    entityTag: null,
                    enableRangeProcessing: true
                );
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting file {fileName} from collection {collectionId}",
                    Path.GetFileName(fileName).SanitizeForLog(), collectionId.SanitizeForLog());
                return StatusCode(500, "Internal server error");
            }
        }
    }

    [ApiController]
    [Route("api/[controller]")]
    public class CollectionIndexController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<CollectionIndexController> _logger;
        private readonly CrecDataService _crecDataService;

        public CollectionIndexController(IConfiguration configuration, ILogger<CollectionIndexController> logger, CrecDataService crecDataService)
        {
            _configuration = configuration;
            _logger = logger;
            _crecDataService = crecDataService;
        }

        /// <summary>
        /// コレクションインデックス更新リクエスト
        /// </summary>
        public class UpdateIndexRequest
        {
            public string Name { get; set; } = string.Empty;
            public string ManagementCode { get; set; } = string.Empty;
            public string RegistrationDate { get; set; } = string.Empty;
            public string Category { get; set; } = string.Empty;
            public string FirstTag { get; set; } = string.Empty;
            public string SecondTag { get; set; } = string.Empty;
            public string ThirdTag { get; set; } = string.Empty;
            public string Location { get; set; } = string.Empty;
        }

        /// <summary>
        /// コレクションのindex.jsonを更新
        /// </summary>
        [HttpPost("{collectionId}")]
        public async Task<IActionResult> UpdateCollectionIndex(string collectionId, [FromBody] UpdateIndexRequest request)
        {
            try
            {
                // コレクションIDの評価
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    collectionId.Contains("..") || collectionId.Contains("/") || collectionId.Contains("\\"))
                {
                    return BadRequest("Invalid collection ID");
                }

                // 名前は必須
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return BadRequest("Name is required");
                }

                // コレクションが存在するか確認
                var collection = await _crecDataService.GetCollectionByIdAsync(collectionId);
                if (collection == null)
                {
                    return NotFound($"Collection with ID '{collectionId}' not found");
                }

                var configuredDataFolder = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
                var dataFolder = Path.GetFullPath(configuredDataFolder);
                var collectionFolder = Path.GetFullPath(Path.Combine(dataFolder, collectionId));
                var systemDataFolder = Path.Combine(collectionFolder, "SystemData");
                var indexFilePath = Path.Combine(systemDataFolder, "index.json");
                var backupFilePath = Path.Combine(systemDataFolder, "backup_index.json");

                // パストラバーサル防止
                var dataFolderWithSeparator =
                    dataFolder.EndsWith(Path.DirectorySeparatorChar) || dataFolder.EndsWith(Path.AltDirectorySeparatorChar)
                        ? dataFolder
                        : dataFolder + Path.DirectorySeparatorChar;
                if (!collectionFolder.StartsWith(dataFolderWithSeparator, StringComparison.OrdinalIgnoreCase))
                {
                    return BadRequest("Access denied");
                }

                // SystemDataフォルダが存在しない場合は警告を出して作成
                if (!Directory.Exists(systemDataFolder))
                {
                    _logger.LogWarning("SystemData folder not found for collection {CollectionId}, creating it", collectionId.SanitizeForLog());
                    Directory.CreateDirectory(systemDataFolder);
                }

                // index.jsonが存在しない場合は警告を出して空のindex.jsonを作成
                IndexData indexData;
                if (!System.IO.File.Exists(indexFilePath))
                {
                    _logger.LogWarning("index.json not found for collection {CollectionId}, creating new one", collectionId.SanitizeForLog());
                    indexData = new IndexData
                    {
                        SystemData = new IndexSystemData
                        {
                            Id = collectionId,
                            SystemCreateDate = DateTimeOffset.UtcNow.ToString("o")
                        },
                        Values = new IndexValues()
                    };
                }
                else
                {
                    // 現在のindex.jsonをbackup_index.jsonとしてバックアップ
                    System.IO.File.Copy(indexFilePath, backupFilePath, overwrite: true);
                    _logger.LogInformation("Backed up index.json to backup_index.json for collection {CollectionId}", collectionId.SanitizeForLog());

                    // 既存のindex.jsonを読み込み
                    var jsonContent = await System.IO.File.ReadAllTextAsync(indexFilePath, System.Text.Encoding.UTF8);
                    indexData = System.Text.Json.JsonSerializer.Deserialize<IndexData>(jsonContent, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new IndexData();

                    if (indexData.SystemData == null)
                    {
                        indexData.SystemData = new IndexSystemData
                        {
                            Id = collectionId,
                            SystemCreateDate = DateTimeOffset.UtcNow.ToString("o")
                        };
                    }
                }

                // 値を更新
                if(indexData.Values == null)
                {
                    indexData.Values = new IndexValues();
                    _logger.LogWarning("Values section was missing in index.json for collection {CollectionId}, created new Values", collectionId.SanitizeForLog());
                }
                indexData.Values.Name = request.Name;
                indexData.Values.ManagementCode = request.ManagementCode ?? string.Empty;
                indexData.Values.RegistrationDate = request.RegistrationDate ?? string.Empty;
                indexData.Values.Category = request.Category ?? string.Empty;
                indexData.Values.FirstTag = request.FirstTag ?? string.Empty;
                indexData.Values.SecondTag = request.SecondTag ?? string.Empty;
                indexData.Values.ThirdTag = request.ThirdTag ?? string.Empty;
                indexData.Values.Location = request.Location ?? string.Empty;

                // index.jsonを保存
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                var updatedJson = System.Text.Json.JsonSerializer.Serialize(indexData, options);
                await System.IO.File.WriteAllTextAsync(indexFilePath, updatedJson, System.Text.Encoding.UTF8);

                // 更新ログを出力
                _logger.LogInformation("Index updated for collection {CollectionId}: Name={Name}",
                    collectionId.SanitizeForLog(), request.Name.SanitizeForLog());

                // コレクションリストのキャッシュをクリア
                _crecDataService.ClearCollectionsListCache();

                // 成功を返す
                return Ok(new { message = "Collection index updated successfully" });
            }
            catch (Exception ex)
            {
                // エラーログを出力
                _logger.LogError(ex, "Error updating collection index for {CollectionId}", collectionId.SanitizeForLog());

                // 500エラーを返す
                return StatusCode(500, "Internal server error: " + ex.Message);
            }
        }
    }
}
