/*
CREC Web - File Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Extensions;
using CREC_Web.Helpers;
using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<FileController> _logger;
        private readonly CrecDataService _crecDataService;

        private const long MaxImageFileSizeBytes = 128L * 1024 * 1024;   // 128MB
        private const long MaxVideoFileSizeBytes = 1024L * 1024 * 1024;  // 1024MB

        public FileController(IConfiguration configuration, ILogger<FileController> logger, CrecDataService crecDataService)
        {
            _configuration = configuration;
            _logger = logger;
            _crecDataService = crecDataService;
        }

        /// <summary>
        /// 画像ファイル取得
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">画像ファイル名</param>
        /// <returns>画像ファイル</returns>
        // 呼び出し例: /api/File/{collectionId}/{fileName}
        [HttpGet("{collectionId}/{fileName}")]
        public IActionResult GetFile(string collectionId, string fileName)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                // セキュリティ: ファイル名を検証（パストラバーサル文字を禁止）
                if (!IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName ?? "").SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 設定からデータパスを取得。未設定の場合はカレントディレクトリを使用
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // pictures フォルダーへのパスを構築: dataPath\collectionId\pictures\fileName
                var filePath = Path.Combine(dataPath, collectionId, "pictures", fileName);

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                var fullPath = Path.GetFullPath(filePath);
                var allowedPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "pictures"));

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, allowedPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", fullPath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                _logger.LogInformation("Attempting to serve file: {filePath}", filePath.SanitizeForLog());

                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogWarning("File not found: {filePath}", filePath.SanitizeForLog());
                    return NotFound();
                }

                // 拡張子に基づいてコンテンツタイプを判定
                var extension = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(extension))
                {
                    _logger.LogWarning("Unsupported image format (no extension) for file: {fileName}", (fileName ?? string.Empty).SanitizeForLog());
                    return BadRequest("Unsupported image format");
                }

                extension = extension.ToLowerInvariant();

                // サポートされている画像拡張子かを検証
                if (!ImageFormats.AllowedExtensions.Contains(extension))
                {
                    _logger.LogWarning("Unsupported image format requested: {extension} for file: {fileName}", extension.SanitizeForLog(), (fileName ?? string.Empty).SanitizeForLog());
                    return BadRequest("Unsupported image format");
                }
                var contentType = ImageFormats.GetContentType(extension);

                _logger.LogInformation($"Serving file with content type: {contentType}");

                // セキュリティヘッダー
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                // 画像は閲覧用（インライン）で提供し、ダウンロードは不可
                return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serving file {CollectionId}/{FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(fileName).SanitizeForLog());

                return StatusCode(500, "Error retrieving file");
            }
        }

        /// <summary>
        /// 汎用データファイル取得（画像以外のファイル用）
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">ファイル名</param>
        /// <returns>ファイル（配信用）</returns>
        // 呼び出し例: /api/File/data/{collectionId}/{pictureFileName}
        [HttpGet("data/{collectionId}/{fileName}")]
        public IActionResult GetDataFile(string collectionId, string fileName)
        {
            try
            {
                // 入力サニタイズ: ファイル名とコレクションIDにパストラバーサルが含まれていないことを確認
                if (!IsSafePathComponent(collectionId) || !IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid path requested: {collectionId}/{fileName}", collectionId.SanitizeForLog(), fileName.SanitizeForLog());
                    return BadRequest("Invalid path component.");
                }

                // 設定からデータパスを取得。未設定の場合はカレントディレクトリを使用
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // data フォルダーへのパスを構築: dataPath\collectionId\data\fileName
                var filePath = Path.Combine(dataPath, collectionId, "data", fileName);
                var fullFilePath = Path.GetFullPath(filePath);
                var fullDataRoot = Path.GetFullPath(Path.Combine(dataPath, collectionId, "data"));

                // ファイルパスが想定ディレクトリ配下か確認（パストラバーサル防止）
                if (!IsPathWithinDirectory(fullFilePath, fullDataRoot))
                {
                    _logger.LogWarning("Attempted path traversal attack: {filePath}", fullFilePath.SanitizeForLog());
                    return BadRequest("Invalid file path.");
                }

                _logger.LogInformation("Attempting to serve data file: {filePath}", fullFilePath.SanitizeForLog());

                if (!System.IO.File.Exists(fullFilePath))
                {
                    _logger.LogWarning("Data file not found: {filePath}", fullFilePath.SanitizeForLog());
                    return NotFound();
                }

                // 拡張子に基づいてコンテンツタイプを判定
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".txt" => "text/plain",
                    ".pdf" => "application/pdf",
                    ".doc" => "application/msword",
                    ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    ".xls" => "application/vnd.ms-excel",
                    ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                    ".csv" => "text/csv",
                    ".xml" => "application/xml",
                    ".json" => "application/json",
                    _ => "application/octet-stream"
                };

                _logger.LogInformation($"Serving data file with content type: {contentType}");

                // CORS とキャッシュヘッダーを付与
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Cache-Control"] = "public, max-age=3600";

                // 直接ファイルを配信するために PhysicalFile を使用
                return PhysicalFile(fullFilePath, contentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serving data file {collectionId}/{fileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(fileName).SanitizeForLog());
                return StatusCode(500, "Error retrieving data file");
            }
        }

        /// <summary>
        /// 画像ファイルアップロード
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="image">アップロード画像ファイル</param>
        /// <returns>アップロード結果</returns>
        // 呼び出し例: POST /api/File/{collectionId}/upload/image
        [HttpPost("{collectionId}/upload/image")]
        [RequestSizeLimit(MaxImageFileSizeBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxImageFileSizeBytes)]
        public async Task<IActionResult> UploadImage(string collectionId, IFormFile image)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                if (image == null || image.Length == 0)
                {
                    return BadRequest("No image file provided");
                }

                // ファイルサイズチェック（上限: 128MB）
                if (image.Length > MaxImageFileSizeBytes)
                {
                    return BadRequest("File size exceeds the maximum allowed size (128MB)");
                }

                // 許可する画像拡張子
                var extension = Path.GetExtension(image.FileName).ToLowerInvariant();
                if (!ImageFormats.AllowedExtensions.Contains(extension))
                {
                    return BadRequest("Unsupported file format. Supported formats: JPEG, PNG, GIF, BMP, WebP");
                }

                // ファイル名を検証（パストラバーサル文字を禁止）
                var sanitizedFileName = Path.GetFileName(image.FileName);
                if (!IsSafePathComponent(sanitizedFileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", image.FileName.SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 設定からデータパスを取得
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // pictures フォルダのパスを構築
                var picturesPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "pictures"));

                // pictures フォルダが存在しない場合は作成
                if (!Directory.Exists(picturesPath))
                {
                    Directory.CreateDirectory(picturesPath);
                }

                // ファイルの保存先を決定
                var filePath = Path.GetFullPath(Path.Combine(picturesPath, sanitizedFileName));

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, picturesPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", filePath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                // 同名ファイルが存在する場合は連番を付与
                if (System.IO.File.Exists(filePath))
                {
                    const int maxDuplicateFileNameAttempts = 1000;
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(sanitizedFileName);
                    var counter = 1;
                    do
                    {
                        var newFileName = $"{fileNameWithoutExt}_{counter}{extension}";
                        filePath = Path.GetFullPath(Path.Combine(picturesPath, newFileName));
                        counter++;
                    } while (System.IO.File.Exists(filePath) && counter <= maxDuplicateFileNameAttempts);

                    if (counter > maxDuplicateFileNameAttempts && System.IO.File.Exists(filePath))
                    {
                        return BadRequest("Too many files with the same name exist in this collection");
                    }
                }

                // ファイルを保存
                using (var stream = System.IO.File.Create(filePath))
                {
                    await image.CopyToAsync(stream);
                }

                // コレクションの画像キャッシュを削除
                _crecDataService.RefreshCollectionImageFileCache(collectionId);

                _logger.LogInformation("Image uploaded for collection {CollectionId}: {FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(filePath).SanitizeForLog());

                return Ok(new { fileName = Path.GetFileName(filePath) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading image for collection {CollectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Error uploading image");
            }
        }

        /// <summary>
        /// 指定した画像をサムネイルとして設定する
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">サムネイルに設定する画像ファイル名</param>
        /// <returns>設定結果</returns>
        // 呼び出し例: POST /api/File/{collectionId}/set-thumbnail?fileName=photo.jpg
        [HttpPost("{collectionId}/set-thumbnail")]
        public async Task<IActionResult> SetThumbnail(string collectionId, [FromQuery] string fileName)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                // セキュリティ: ファイル名を検証（パストラバーサル文字を禁止）
                if (!IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName ?? "").SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // ソース画像のパスを構築
                var picturesPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "pictures"));
                var sourceFilePath = Path.GetFullPath(Path.Combine(picturesPath, fileName));

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(sourceFilePath, picturesPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", sourceFilePath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                if (!System.IO.File.Exists(sourceFilePath))
                {
                    return NotFound("Source image not found");
                }

                // SystemData フォルダのパスを構築
                var systemDataPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "SystemData"));

                // SystemData フォルダが存在しない場合は作成
                if (!Directory.Exists(systemDataPath))
                {
                    Directory.CreateDirectory(systemDataPath);
                }

                // 元画像の拡張子でサムネイルファイル名を決定（例: Thumbnail.jpg）
                var thumbnailExtension = Path.GetExtension(fileName).ToLowerInvariant();
                if (!ImageFormats.AllowedExtensions.Contains(thumbnailExtension))
                {
                    return BadRequest("Unsupported file format. Supported formats: JPEG, PNG, GIF, BMP, WebP");
                }

                var thumbnailFileName = $"Thumbnail{thumbnailExtension}";
                var thumbnailPath = Path.GetFullPath(Path.Combine(systemDataPath, thumbnailFileName));

                // セキュリティ: サムネイルパスが SystemData ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(thumbnailPath, systemDataPath))
                {
                    return BadRequest("Access denied");
                }

                // 既存のサムネイルファイル（すべての拡張子）を削除
                foreach (var ext in ImageFormats.AllowedExtensions)
                {
                    var oldThumbnailPath = Path.GetFullPath(Path.Combine(systemDataPath, $"Thumbnail{ext}"));
                    if (System.IO.File.Exists(oldThumbnailPath))
                    {
                        System.IO.File.Delete(oldThumbnailPath);
                    }
                }

                // 画像をサムネイルとしてコピー
                using var sourceStream = System.IO.File.OpenRead(sourceFilePath);
                using var destStream = System.IO.File.Create(thumbnailPath);
                await sourceStream.CopyToAsync(destStream);

                _logger.LogInformation("Thumbnail set for collection {CollectionId}: {FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(sourceFilePath).SanitizeForLog());

                return Ok(new { message = "Thumbnail set successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting thumbnail for collection {CollectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Error setting thumbnail");
            }
        }

        /// <summary>
        /// 指定した画像を削除する
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">削除する画像ファイル名</param>
        /// <returns>削除結果</returns>
        // 呼び出し例: DELETE /api/File/{collectionId}/image?fileName=photo.jpg
        [HttpDelete("{collectionId}/image")]
        public IActionResult DeleteImage(string collectionId, [FromQuery] string fileName)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                // セキュリティ: ファイル名を検証（パストラバーサル文字を禁止）
                if (!IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName ?? "").SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // pictures フォルダーへのパスを構築
                var picturesPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "pictures"));
                var filePath = Path.GetFullPath(Path.Combine(picturesPath, fileName));

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, picturesPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", filePath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("Image not found");
                }

                System.IO.File.Delete(filePath);

                // コレクションの画像キャッシュを削除（ファイルシステムとキャッシュの整合性を保つため）
                _crecDataService.RefreshCollectionImageFileCache(collectionId);

                _logger.LogInformation("Image deleted for collection {CollectionId}: {FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(filePath).SanitizeForLog());

                return Ok(new { message = "Image deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting image for collection {CollectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Error deleting image");
            }
        }

        /// <summary>
        /// 動画ファイル取得
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">動画ファイル名</param>
        /// <returns>動画ファイル</returns>
        // 呼び出し例: /api/File/{collectionId}/video/{fileName}
        [HttpGet("{collectionId}/video/{fileName}")]
        public IActionResult GetVideoFile(string collectionId, string fileName)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                // セキュリティ: ファイル名を検証（パストラバーサル文字を禁止）
                if (!IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName ?? "").SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 設定からデータパスを取得。未設定の場合はカレントディレクトリを使用
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // videos フォルダーへのパスを構築: dataPath\collectionId\videos\fileName
                var filePath = Path.Combine(dataPath, collectionId, "videos", fileName);

                // セキュリティ: 解決済みパスが videos ディレクトリ配下に留まっていることを確認
                var fullPath = Path.GetFullPath(filePath);
                var allowedPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "videos"));

                // セキュリティ: 解決済みパスが videos ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, allowedPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", fullPath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                _logger.LogInformation("Attempting to serve file: {filePath}", filePath.SanitizeForLog());

                if (!System.IO.File.Exists(filePath))
                {
                    _logger.LogWarning("Video file not found: {filePath}", filePath.SanitizeForLog());
                    return NotFound();
                }

                // 拡張子に基づいてコンテンツタイプを判定
                var extension = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(extension))
                {
                    _logger.LogWarning("Unsupported video format (no extension) for file: {fileName}", (fileName ?? string.Empty).SanitizeForLog());
                    return BadRequest("Unsupported video format");
                }

                extension = extension.ToLowerInvariant();

                // サポートされている動画拡張子かを検証
                if (!VideoFormats.AllowedExtensions.Contains(extension))
                {
                    _logger.LogWarning("Unsupported video format requested: {extension} for file: {fileName}", extension.SanitizeForLog(), (fileName ?? string.Empty).SanitizeForLog());
                    return BadRequest("Unsupported video format");
                }
                var contentType = VideoFormats.GetContentType(extension);

                _logger.LogInformation($"Serving video file with content type: {contentType}");

                // セキュリティヘッダー
                Response.Headers["Access-Control-Allow-Origin"] = "*";
                Response.Headers["Cache-Control"] = "public, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                // 動画はレンジリクエスト対応（シークバー用）
                return PhysicalFile(fullPath, contentType, enableRangeProcessing: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error serving video file {CollectionId}/{FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(fileName).SanitizeForLog());

                return StatusCode(500, "Error retrieving video file");
            }
        }

        /// <summary>
        /// 動画ファイルアップロード
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="video">アップロード動画ファイル</param>
        /// <returns>アップロード結果</returns>
        // 呼び出し例: POST /api/File/{collectionId}/upload/video
        [HttpPost("{collectionId}/upload/video")]
        [RequestSizeLimit(MaxVideoFileSizeBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxVideoFileSizeBytes)]
        public async Task<IActionResult> UploadVideo(string collectionId, IFormFile video)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                if (video == null || video.Length == 0)
                {
                    return BadRequest("No video file provided");
                }

                // ファイルサイズチェック（上限: 1024MB）
                if (video.Length > MaxVideoFileSizeBytes)
                {
                    return BadRequest("File size exceeds the maximum allowed size (1024MB)");
                }

                // 許可する動画拡張子
                var extension = Path.GetExtension(video.FileName).ToLowerInvariant();
                if (!VideoFormats.AllowedExtensions.Contains(extension))
                {
                    return BadRequest("Unsupported file format. Supported formats: MP4, MOV, AVI, MKV, WebM, WMV, FLV, M4V");
                }

                // ファイル名を検証（パストラバーサル文字を禁止）
                var sanitizedFileName = Path.GetFileName(video.FileName);
                if (!IsSafePathComponent(sanitizedFileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", video.FileName.SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 設定からデータパスを取得
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // videos フォルダのパスを構築
                var videosPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "videos"));

                // videos フォルダが存在しない場合は作成
                if (!Directory.Exists(videosPath))
                {
                    Directory.CreateDirectory(videosPath);
                }

                // ファイルの保存先を決定
                var filePath = Path.GetFullPath(Path.Combine(videosPath, sanitizedFileName));

                // セキュリティ: 解決済みパスが videos ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, videosPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", filePath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                // 同名ファイルが存在する場合は連番を付与
                if (System.IO.File.Exists(filePath))
                {
                    const int maxDuplicateFileNameAttempts = 1000;
                    var fileNameWithoutExt = Path.GetFileNameWithoutExtension(sanitizedFileName);
                    var counter = 1;
                    do
                    {
                        var newFileName = $"{fileNameWithoutExt}_{counter}{extension}";
                        filePath = Path.GetFullPath(Path.Combine(videosPath, newFileName));
                        counter++;
                    } while (System.IO.File.Exists(filePath) && counter <= maxDuplicateFileNameAttempts);

                    if (counter > maxDuplicateFileNameAttempts && System.IO.File.Exists(filePath))
                    {
                        return BadRequest("Too many files with the same name exist in this collection");
                    }
                }

                // ファイルを保存
                using (var stream = System.IO.File.Create(filePath))
                {
                    await video.CopyToAsync(stream);
                }

                // コレクションの動画キャッシュを更新
                _crecDataService.RefreshCollectionVideoFileCache(collectionId);

                _logger.LogInformation("Video uploaded for collection {CollectionId}: {FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(filePath).SanitizeForLog());

                return Ok(new { fileName = Path.GetFileName(filePath) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading video for collection {CollectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Error uploading video");
            }
        }

        /// <summary>
        /// 動画ファイル削除
        /// </summary>
        /// <param name="collectionId">コレクションID</param>
        /// <param name="fileName">削除する動画ファイル名</param>
        /// <returns>削除結果</returns>
        // 呼び出し例: DELETE /api/File/{collectionId}/video?fileName=video.mp4
        [HttpDelete("{collectionId}/video")]
        public IActionResult DeleteVideo(string collectionId, [FromQuery] string fileName)
        {
            try
            {
                // セキュリティ: コレクション ID を検証（英数字・ハイフン・アンダースコアのみ）
                if (string.IsNullOrWhiteSpace(collectionId) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") ||
                    collectionId.Length > 255)
                {
                    _logger.LogWarning("Invalid collection ID: {collectionId}", collectionId.SanitizeForLog());
                    return BadRequest("Invalid collection ID");
                }

                // セキュリティ: ファイル名を検証（パストラバーサル文字を禁止）
                if (!IsSafePathComponent(fileName))
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName ?? "").SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 許可する動画拡張子かを検証
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                if (string.IsNullOrEmpty(extension) || !VideoFormats.AllowedExtensions.Contains(extension))
                {
                    _logger.LogWarning("Unsupported video format requested for deletion: {extension} for file: {fileName}", extension.SanitizeForLog(), fileName.SanitizeForLog());
                    return BadRequest("Unsupported video format");
                }

                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // videos フォルダーへのパスを構築
                var videosPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "videos"));
                var filePath = Path.GetFullPath(Path.Combine(videosPath, fileName));

                // セキュリティ: 解決済みパスが videos ディレクトリ配下に留まっていることを確認
                if (!IsPathWithinDirectory(filePath, videosPath))
                {
                    _logger.LogWarning("Path traversal attempt detected: {fullPath}", filePath.SanitizeForLog());
                    return BadRequest("Invalid file path");
                }

                if (!System.IO.File.Exists(filePath))
                {
                    return NotFound("Video not found");
                }

                System.IO.File.Delete(filePath);

                // コレクションの動画キャッシュを更新（ファイルシステムとキャッシュの整合性を保つため）
                _crecDataService.RefreshCollectionVideoFileCache(collectionId);

                _logger.LogInformation("Video deleted for collection {CollectionId}: {FileName}",
                    collectionId.SanitizeForLog(), Path.GetFileName(filePath).SanitizeForLog());

                return Ok(new { message = "Video deleted successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting video for collection {CollectionId}", collectionId.SanitizeForLog());
                return StatusCode(500, "Error deleting video");
            }
        }

        // path セーフ判定用ヘルパー
        private static bool IsSafePathComponent(string component)
        {
            if (string.IsNullOrWhiteSpace(component) || component.Length > 255)
                return false;
            // 禁止: パス区切り文字、親ディレクトリ参照
            return
                !component.Contains("..") &&
                !component.Contains(Path.DirectorySeparatorChar) &&
                !component.Contains(Path.AltDirectorySeparatorChar) &&
                component == Path.GetFileName(component); // さらに単一要素か厳格判定
        }

        /// <summary>
        /// 指定されたパスが許可されたディレクトリ配下にあることを確認
        /// </summary>
        /// <param name="path">チェック対象のフルパス</param>
        /// <param name="allowedDirectory">許可されたディレクトリのフルパス</param>
        /// <returns>ディレクトリ配下にある場合は true、それ以外は false</returns>
        private static bool IsPathWithinDirectory(string path, string allowedDirectory)
        {
            // 両方のパスがフルパスで正規化されていることを確認
            var normalizedPath = Path.GetFullPath(path);
            var normalizedAllowedDirectory = Path.GetFullPath(allowedDirectory);

            // ルートが異なる場合は拒否
            if (!string.Equals(Path.GetPathRoot(normalizedPath), Path.GetPathRoot(normalizedAllowedDirectory),
                StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // normalizedAllowedDirectoryにディレクトリセパレータを末尾に追加
            if (!normalizedAllowedDirectory.EndsWith(Path.DirectorySeparatorChar.ToString()))
            {
                normalizedAllowedDirectory += Path.DirectorySeparatorChar;
            }

            // パスがディレクトリ配下にあるか、またはディレクトリそのものであるかを確認
            return normalizedPath.StartsWith(normalizedAllowedDirectory, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.Equals(normalizedAllowedDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
    }
}
