using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

/// <summary>WebMCP向けに、MCPと同じ検索・詳細取得処理を提供する。</summary>
[ApiController]
[Route("api/collection-queries")]
public sealed class CollectionQueriesController(CollectionQueryService queries,
    ILogger<CollectionQueriesController> logger) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> Search([FromQuery] string projectRevision,
        [FromQuery] string? query = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 20) =>
        ReadAsync(async () => await queries.SearchAsync(projectRevision, query, page, pageSize, HttpContext.RequestAborted));

    [HttpGet("{collectionId}")]
    public Task<IActionResult> Get(string collectionId, [FromQuery] string projectRevision) =>
        ReadAsync(async () => await queries.GetAsync(projectRevision, collectionId, HttpContext.RequestAborted));

    private async Task<IActionResult> ReadAsync(Func<Task<object>> read)
    {
        try
        {
            return Ok(await read());
        }
        catch (CollectionQueryException ex)
        {
            var status = ex.Code switch
            {
                "projects-busy" => StatusCodes.Status503ServiceUnavailable,
                "projects-stale" or "projects-not-selected" => StatusCodes.Status409Conflict,
                "collection-not-found" => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest
            };
            return StatusCode(status, new { code = ex.Code });
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Collection query failed");
            return StatusCode(500, new { code = "collection-query-failed" });
        }
    }
}
