/*
CREC Web - Collection Data File Manager Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Extensions;
using CREC_Web.Models;
using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace CREC_Web.Controllers
{
    /// <summary>
    /// コレクションの data フォルダを操作するAPIを提供します。
    /// </summary>
    [ApiController]
    [Route("api/collections/{collectionId}/data")]
    public sealed class CollectionDataFilesController : ControllerBase
    {
        private const long MaxDataFileSizeBytes = 1024L * 1024 * 1024;

        private readonly DataFileManagerService _dataFileManager;
        private readonly CrecDataService _crecDataService;
        private readonly ILogger<CollectionDataFilesController> _logger;
        private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

        /// <summary>
        /// データファイル管理APIを初期化します。
        /// </summary>
        /// <param name="dataFileManager">ファイルシステム操作を担当するサービス。</param>
        /// <param name="crecDataService">操作後にコレクションキャッシュを更新するためのサービス。</param>
        /// <param name="logger">API処理の警告やエラーを記録するロガー。</param>
        public CollectionDataFilesController(
            DataFileManagerService dataFileManager,
            CrecDataService crecDataService,
            ILogger<CollectionDataFilesController> logger)
        {
            _dataFileManager = dataFileManager;
            _crecDataService = crecDataService;
            _logger = logger;
        }

        /// <summary>
        /// 指定した data フォルダ直下のファイルとフォルダを取得します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした対象フォルダの相対パス。</param>
        /// <returns>現在のパスと直下の項目一覧。</returns>
        [HttpGet]
        public ActionResult<DataDirectoryListing> ListDirectory(string collectionId, [FromQuery] string? path = null)
        {
            try
            {
                return Ok(_dataFileManager.ListDirectory(collectionId, path));
            }
            catch (Exception exception)
            {
                return HandleException(exception, "listing data directory", collectionId);
            }
        }

        /// <summary>
        /// 指定した data フォルダ内に新しいフォルダを作成します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="request">親フォルダの相対パスと作成するフォルダ名。</param>
        /// <returns>作成したフォルダの情報。</returns>
        [HttpPost("folders")]
        public ActionResult<DataFileEntry> CreateDirectory(
            string collectionId,
            [FromBody] CreateDataDirectoryRequest request)
        {
            try
            {
                var entry = _dataFileManager.CreateDirectory(collectionId, request.ParentPath, request.Name);
                ClearCollectionCache();
                return StatusCode(StatusCodes.Status201Created, entry);
            }
            catch (Exception exception)
            {
                return HandleException(exception, "creating data directory", collectionId);
            }
        }

        /// <summary>
        /// data フォルダ内のファイルまたはフォルダの名前を変更します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="request">変更対象の相対パス、新しい名前、拡張子変更の確認状態。</param>
        /// <returns>名前変更後の項目情報。</returns>
        [HttpPatch("entries")]
        public ActionResult<DataFileEntry> RenameEntry(
            string collectionId,
            [FromBody] RenameDataEntryRequest request)
        {
            try
            {
                var entry = _dataFileManager.RenameEntry(
                    collectionId,
                    request.Path,
                    request.NewName,
                    request.ConfirmExtensionChange);
                ClearCollectionCache();
                return Ok(entry);
            }
            catch (Exception exception)
            {
                return HandleException(exception, "renaming data entry", collectionId);
            }
        }

        /// <summary>
        /// data フォルダ内のファイルまたはフォルダを削除します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした削除対象の相対パス。</param>
        /// <returns>削除成功時はレスポンス本文なし。</returns>
        [HttpDelete("entries")]
        public IActionResult DeleteEntry(string collectionId, [FromQuery] string path)
        {
            try
            {
                _dataFileManager.DeleteEntry(collectionId, path);
                ClearCollectionCache();
                return NoContent();
            }
            catch (Exception exception)
            {
                return HandleException(exception, "deleting data entry", collectionId);
            }
        }

        /// <summary>
        /// 指定した data フォルダへファイルをアップロードします。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準としたアップロード先の相対パス。</param>
        /// <param name="file">アップロードするファイル。</param>
        /// <returns>アップロードしたファイルの情報。</returns>
        [HttpPost("files")]
        [RequestSizeLimit(MaxDataFileSizeBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxDataFileSizeBytes)]
        public async Task<ActionResult<DataFileEntry>> UploadFile(
            string collectionId,
            [FromQuery] string? path,
            IFormFile file)
        {
            try
            {
                var entry = await _dataFileManager.UploadFileAsync(
                    collectionId,
                    path,
                    file,
                    HttpContext.RequestAborted);
                ClearCollectionCache();
                return StatusCode(StatusCodes.Status201Created, entry);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                return new EmptyResult();
            }
            catch (Exception exception)
            {
                return HandleException(exception, "uploading data file", collectionId);
            }
        }

        /// <summary>
        /// data フォルダ内のファイルをダウンロードします。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした対象ファイルの相対パス。</param>
        /// <returns>対象ファイルのダウンロードレスポンス。</returns>
        [HttpGet("files")]
        public IActionResult DownloadFile(string collectionId, [FromQuery] string path)
        {
            try
            {
                var (fullPath, downloadName) = _dataFileManager.GetFileForDownload(collectionId, path);
                if (!_contentTypeProvider.TryGetContentType(downloadName, out var contentType))
                {
                    contentType = "application/octet-stream";
                }

                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return PhysicalFile(
                    fullPath,
                    contentType,
                    downloadName,
                    enableRangeProcessing: true);
            }
            catch (Exception exception)
            {
                return HandleException(exception, "downloading data file", collectionId);
            }
        }

        /// <summary>
        /// data フォルダ内の指定フォルダを、配下の項目を含むZIPとしてダウンロードします。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした対象フォルダの相対パス。</param>
        /// <returns>作成したZIPファイルのダウンロードレスポンス。</returns>
        [HttpGet("folders/archive")]
        public async Task<IActionResult> DownloadDirectory(string collectionId, [FromQuery] string path)
        {
            try
            {
                var (stream, downloadName) = await _dataFileManager.CreateDirectoryArchiveAsync(
                    collectionId,
                    path,
                    HttpContext.RequestAborted);
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(stream, "application/zip", downloadName, enableRangeProcessing: false);
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                return new EmptyResult();
            }
            catch (Exception exception)
            {
                return HandleException(exception, "downloading data directory", collectionId);
            }
        }

        /// <summary>
        /// ファイル操作後に、コレクション一覧のキャッシュを破棄します。
        /// </summary>
        private void ClearCollectionCache() => _crecDataService.ClearCollectionsListCache();

        /// <summary>
        /// ファイル操作中の例外を、ログ出力と適切なHTTPエラーレスポンスへ変換します。
        /// </summary>
        /// <param name="exception">変換対象の例外。</param>
        /// <param name="operation">ログに記録する実行中の操作名。</param>
        /// <param name="collectionId">操作対象のコレクションID。</param>
        /// <returns>例外の種類に対応したエラーレスポンス。</returns>
        private ObjectResult HandleException(Exception exception, string operation, string collectionId)
        {
            if (exception is DataFileManagerException dataException)
            {
                _logger.LogWarning(
                    "Failed {Operation} for collection {CollectionId}: {Message}",
                    operation,
                    collectionId.SanitizeForLog(),
                    dataException.Message.SanitizeForLog());
                var problemDetails = new ProblemDetails
                {
                    Status = dataException.StatusCode,
                    Title = dataException.Message
                };
                if (!string.IsNullOrEmpty(dataException.ErrorCode))
                {
                    problemDetails.Extensions["code"] = dataException.ErrorCode;
                }
                return new ObjectResult(problemDetails) { StatusCode = dataException.StatusCode };
            }

            if (exception is UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Access denied while {Operation} for collection {CollectionId}",
                    operation,
                    collectionId.SanitizeForLog());
                return Problem(statusCode: StatusCodes.Status403Forbidden, title: "Access denied.");
            }

            if (exception is IOException)
            {
                _logger.LogWarning(
                    exception,
                    "File system conflict while {Operation} for collection {CollectionId}",
                    operation,
                    collectionId.SanitizeForLog());
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The file system operation could not be completed.");
            }

            _logger.LogError(
                exception,
                "Unexpected error while {Operation} for collection {CollectionId}",
                operation,
                collectionId.SanitizeForLog());
            return Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "An unexpected error occurred.");
        }
    }
}
