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
    [ApiController]
    [Route("api/collections/{collectionId}/data")]
    public sealed class CollectionDataFilesController : ControllerBase
    {
        private const long MaxDataFileSizeBytes = 1024L * 1024 * 1024;

        private readonly DataFileManagerService _dataFileManager;
        private readonly CrecDataService _crecDataService;
        private readonly ILogger<CollectionDataFilesController> _logger;
        private readonly FileExtensionContentTypeProvider _contentTypeProvider = new();

        public CollectionDataFilesController(
            DataFileManagerService dataFileManager,
            CrecDataService crecDataService,
            ILogger<CollectionDataFilesController> logger)
        {
            _dataFileManager = dataFileManager;
            _crecDataService = crecDataService;
            _logger = logger;
        }

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

        private void ClearCollectionCache() => _crecDataService.ClearCollectionsListCache();

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
