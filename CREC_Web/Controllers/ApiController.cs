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
                _logger.LogInformation($"Search returned {result.Collections.Count} collections out of {result.TotalCount} total");
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
                _logger.LogError(ex, $"Error getting file {fileName} from collection {collectionId}");
                return StatusCode(500, "Internal server error");
            }
        }
    }
}