/*
CREC Web - File Controller
Copyright (c) [2025] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FileController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<FileController> _logger;

        public FileController(IConfiguration configuration, ILogger<FileController> logger)
        {
            _configuration = configuration;
            _logger = logger;
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
                if (string.IsNullOrWhiteSpace(fileName) ||
                    fileName.Contains("..") ||
                    fileName.Contains("/") ||
                    fileName.Contains("\\") ||
                    fileName.Length > 255)
                {
                    _logger.LogWarning("Invalid file name: {fileName}", Path.GetFileName(fileName).SanitizeForLog());
                    return BadRequest("Invalid file name");
                }

                // 設定からデータパスを取得。未設定の場合はカレントディレクトリを使用
                var dataPath = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();

                // pictures フォルダーへのパスを構築: dataPath\collectionId\pictures\fileName
                var filePath = Path.Combine(dataPath, collectionId, "pictures", fileName);

                // セキュリティ: 解決済みパスが pictures ディレクトリ配下に留まっていることを確認
                var fullPath = Path.GetFullPath(filePath);
                var allowedPath = Path.GetFullPath(Path.Combine(dataPath, collectionId, "pictures"));

                if (!fullPath.StartsWith(allowedPath, StringComparison.OrdinalIgnoreCase))
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
                var extension = Path.GetExtension(fileName).ToLowerInvariant();
                var contentType = extension switch
                {
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".bmp" => "image/bmp",
                    ".webp" => "image/webp",
                    ".svg" => "image/svg+xml",
                    _ => "application/octet-stream"
                };

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
                if (!fullFilePath.StartsWith(fullDataRoot + Path.DirectorySeparatorChar) && fullFilePath != fullDataRoot)
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

        // path セーフ判定用ヘルパー
        private static bool IsSafePathComponent(string component)
        {
            if (string.IsNullOrEmpty(component))
                return false;
            // 禁止: パス区切り文字、親ディレクトリ参照
            return
                !component.Contains("..") &&
                !component.Contains(Path.DirectorySeparatorChar) &&
                !component.Contains(Path.AltDirectorySeparatorChar) &&
                component == Path.GetFileName(component); // さらに単一要素か厳格判定
        }
    }
}
