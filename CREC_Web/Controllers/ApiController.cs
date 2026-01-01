/*
CREC Web - API Controller
Copyright (c) [2025] [S.Yukisita]
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

        public CollectionsController(CrecDataService crecDataService, ILogger<CollectionsController> logger)
        {
            _crecDataService = crecDataService;
            _logger = logger;
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

                // 在庫操作数の評価: longオーバフローを確認
                if (request.Quantity < long.MinValue || request.Quantity > long.MaxValue)
                {
                    return BadRequest("Quantity is out of range");
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
}