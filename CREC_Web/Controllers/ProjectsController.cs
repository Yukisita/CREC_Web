using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(ProjectRuntime runtime, ProjectCatalogService catalog,
    ProjectAdminService admin, ILogger<ProjectsController> logger) : ControllerBase
{
    [HttpGet("status")]
    public IActionResult Status()
    {
        var state = runtime.Current;
        return Ok(new { state.Revision, state.Name });
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] ProjectLoginRequest request)
    {
        if (!admin.ValidateToken(request.Token)) return Unauthorized(new { code = "projects-unauthorized" });
        admin.SignIn(HttpContext);
        return NoContent();
    }

    [HttpGet]
    public IActionResult List() => Ok(catalog.List(runtime.Current.FilePath));

    [HttpPost("switch")]
    public async Task<IActionResult> Switch([FromBody] SwitchProjectRequest request)
    {
        try
        {
            var result = await runtime.SwitchAsync(request.Id, request.Revision, HttpContext.RequestAborted);
            var payload = new { code = result.Code, revision = result.State.Revision, name = result.State.Name };
            return result.Code switch
            {
                "projects-switched" or "projects-already-current" => Ok(payload),
                "projects-busy" or "projects-stale" => Conflict(payload),
                _ => BadRequest(payload)
            };
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new EmptyResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Project switch failed; previous project restored");
            return StatusCode(500, new { code = "projects-switch-failed" });
        }
    }
}

public sealed record ProjectLoginRequest(string Token);
public sealed record SwitchProjectRequest(string Id, string Revision);
