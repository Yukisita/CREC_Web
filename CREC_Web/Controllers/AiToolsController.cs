using System.Text.Json;
using CREC_Web.Services;
using CREC_Web.Services.AiTools;
using Microsoft.AspNetCore.Mvc;
using ModelContextProtocol;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/ai-tools")]
public sealed class AiToolsController(CrecToolCatalog tools, ILogger<AiToolsController> logger) : ControllerBase
{
    [HttpGet]
    public IActionResult List() => new JsonResult(tools.Definitions, CrecToolCatalog.JsonOptions);

    [HttpPost("{name}")]
    [RequestSizeLimit(16384)]
    public async Task<IActionResult> Invoke(string name, [FromBody] Dictionary<string, JsonElement> arguments)
    {
        try
        {
            var revision = Request.Headers["X-CREC-Project"].ToString();
            if (revision.Length == 0) revision = Request.Query["projectRevision"].ToString();
            return new JsonResult(await tools.InvokeAsync(name, arguments, revision, HttpContext.RequestAborted), CrecToolCatalog.JsonOptions);
        }
        catch (Exception ex) when (ex is CrecReadException or McpException)
        {
            var code = ex.Message;
            return StatusCode(code switch
            {
                "projects-busy" => 503, "projects-stale" or "projects-not-selected" or "file-changed" => 409,
                "tool-not-found" or "collection-not-found" or "file-not-found" or "directory-not-found" => 404,
                "file-access-denied" or "file-link-denied" => 403, _ => 400
            }, new { code });
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException) { return BadRequest(new { code = "invalid-arguments" }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "AI read tool failed: {Tool}", name);
            return StatusCode(500, new { code = "read-failed" });
        }
    }
}
