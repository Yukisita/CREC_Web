/*
CREC Web - Project Settings Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using CREC_Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectSettingsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectSettingsController> _logger;
    private readonly ProjectSettingsService _projectSettingsService;

    public ProjectSettingsController(
        IConfiguration configuration,
        ILogger<ProjectSettingsController> logger,
        ProjectSettingsService projectSettingsService)
    {
        _configuration = configuration;
        _logger = logger;
        _projectSettingsService = projectSettingsService;
    }

    [HttpGet]
    public IActionResult GetProjectSettings()
    {
        var settings = new
        {
            projectName = _configuration["ProjectName"] ?? "CREC Project",
            projectDataPath = _configuration["ProjectDataPath"] ?? "",
            objectNameLabel = _configuration["CollectionNameLabel"] ?? "Name",
            uuidName = _configuration["UUIDLabel"] ?? "ID",
            managementCodeName = _configuration["ManagementCodeLabel"] ?? "MC",
            categoryName = _configuration["CategoryLabel"] ?? "Category",
            tag1Name = _configuration["FirstTagLabel"] ?? "Tag 1",
            tag2Name = _configuration["SecondTagLabel"] ?? "Tag 2",
            tag3Name = _configuration["ThirdTagLabel"] ?? "Tag 3"
        };

        _logger.LogInformation("Returning project settings: ProjectName={ProjectName}, UUIDLabel={UUIDLabel}, MCLabel={MCLabel}, CategoryLabel={CategoryLabel}, Tag1={Tag1}, Tag2={Tag2}, Tag3={Tag3}",
            settings.projectName, settings.uuidName, settings.managementCodeName, settings.categoryName, settings.tag1Name, settings.tag2Name, settings.tag3Name);
        return Ok(settings);
    }

    [HttpPut]
    public IActionResult UpdateProjectSettings([FromBody] UpdateProjectSettingsRequest request)
    {
        if (request == null)
        {
            return BadRequest("Request body is required");
        }

        if (_projectSettingsService.UpdateProjectSettings(request, out var message))
        {
            _logger.LogInformation("Project settings updated successfully");
            return Ok(new { message });
        }

        _logger.LogError("Error updating project settings: {Message}", message);
        return CreateUpdateFailureResult(message);
    }

    private IActionResult CreateUpdateFailureResult(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            if (message.Contains("not configured", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("bad request", StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(message);
            }

            if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("missing", StringComparison.OrdinalIgnoreCase))
            {
                return NotFound(message);
            }
        }
        return StatusCode(500, message);
    }
}
