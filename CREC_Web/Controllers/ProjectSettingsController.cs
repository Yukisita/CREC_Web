/*
CREC Web - Project Settings Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using Microsoft.AspNetCore.Mvc;

namespace CREC_Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProjectSettingsController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ProjectSettingsController> _logger;

    public ProjectSettingsController(IConfiguration configuration, ILogger<ProjectSettingsController> logger)
    {
        _configuration = configuration;
        _logger = logger;
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

    public class UpdateProjectSettingsRequest
    {
        public string? ProjectName { get; set; }
        public string? CollectionNameLabel { get; set; }
        public string? UUIDLabel { get; set; }
        public string? ManagementCodeLabel { get; set; }
        public string? CategoryLabel { get; set; }
        public string? FirstTagLabel { get; set; }
        public string? SecondTagLabel { get; set; }
        public string? ThirdTagLabel { get; set; }
    }

    // SemaphoreSlim to protect concurrent read-modify-write access to the .crec file.
    // This is intentionally static and application-lifetime scoped; it is not disposed
    // because disposing a shared static lock while other requests may still hold it
    // would cause ObjectDisposedException on subsequent calls.
    private static readonly SemaphoreSlim _fileLock = new SemaphoreSlim(1, 1);

    [HttpPut]
    public async Task<IActionResult> UpdateProjectSettings([FromBody] UpdateProjectSettingsRequest request)
    {
        if (request == null)
        {
            return BadRequest("Request body is required");
        }

        var crecFilePath = _configuration["CrecFilePath"];
        if (string.IsNullOrEmpty(crecFilePath) || !System.IO.File.Exists(crecFilePath))
        {
            return StatusCode(500, "Project file path is not configured or file does not exist");
        }

        var lockAcquired = false;
        try
        {
            await _fileLock.WaitAsync(HttpContext.RequestAborted);
            lockAcquired = true;

            // Read the current .crec file content
            var lines = await System.IO.File.ReadAllLinesAsync(crecFilePath, System.Text.Encoding.UTF8);
            var updatedLines = new List<string>();

            // Track which keys have been updated
            var updatedKeys = new HashSet<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    updatedLines.Add(line);
                    continue;
                }

                var cols = line.Split(',');
                if (cols.Length < 2)
                {
                    updatedLines.Add(line);
                    continue;
                }

                var key = cols[0].Trim();
                switch (key)
                {
                    case "projectname" when request.ProjectName != null:
                        updatedLines.Add($"projectname,{request.ProjectName}");
                        updatedKeys.Add(key);
                        break;
                    case "ShowObjectNameLabel" when request.CollectionNameLabel != null:
                        updatedLines.Add($"ShowObjectNameLabel,{request.CollectionNameLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "ShowIDLabel" when request.UUIDLabel != null:
                        updatedLines.Add($"ShowIDLabel,{request.UUIDLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "ShowMCLabel" when request.ManagementCodeLabel != null:
                        updatedLines.Add($"ShowMCLabel,{request.ManagementCodeLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "ShowCategoryLabel" when request.CategoryLabel != null:
                        updatedLines.Add($"ShowCategoryLabel,{request.CategoryLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "Tag1Name" when request.FirstTagLabel != null:
                        updatedLines.Add($"Tag1Name,{request.FirstTagLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "Tag2Name" when request.SecondTagLabel != null:
                        updatedLines.Add($"Tag2Name,{request.SecondTagLabel}");
                        updatedKeys.Add(key);
                        break;
                    case "Tag3Name" when request.ThirdTagLabel != null:
                        updatedLines.Add($"Tag3Name,{request.ThirdTagLabel}");
                        updatedKeys.Add(key);
                        break;
                    default:
                        updatedLines.Add(line);
                        break;
                }
            }

            // Append any new keys that were not present in the original file
            if (request.ProjectName != null && !updatedKeys.Contains("projectname"))
                updatedLines.Add($"projectname,{request.ProjectName}");
            if (request.CollectionNameLabel != null && !updatedKeys.Contains("ShowObjectNameLabel"))
                updatedLines.Add($"ShowObjectNameLabel,{request.CollectionNameLabel}");
            if (request.UUIDLabel != null && !updatedKeys.Contains("ShowIDLabel"))
                updatedLines.Add($"ShowIDLabel,{request.UUIDLabel}");
            if (request.ManagementCodeLabel != null && !updatedKeys.Contains("ShowMCLabel"))
                updatedLines.Add($"ShowMCLabel,{request.ManagementCodeLabel}");
            if (request.CategoryLabel != null && !updatedKeys.Contains("ShowCategoryLabel"))
                updatedLines.Add($"ShowCategoryLabel,{request.CategoryLabel}");
            if (request.FirstTagLabel != null && !updatedKeys.Contains("Tag1Name"))
                updatedLines.Add($"Tag1Name,{request.FirstTagLabel}");
            if (request.SecondTagLabel != null && !updatedKeys.Contains("Tag2Name"))
                updatedLines.Add($"Tag2Name,{request.SecondTagLabel}");
            if (request.ThirdTagLabel != null && !updatedKeys.Contains("Tag3Name"))
                updatedLines.Add($"Tag3Name,{request.ThirdTagLabel}");

            // Write the updated content back to the .crec file
            await System.IO.File.WriteAllLinesAsync(crecFilePath, updatedLines, System.Text.Encoding.UTF8);

            // Update in-memory configuration
            if (request.ProjectName != null) _configuration["ProjectName"] = request.ProjectName;
            if (request.CollectionNameLabel != null) _configuration["CollectionNameLabel"] = request.CollectionNameLabel;
            if (request.UUIDLabel != null) _configuration["UUIDLabel"] = request.UUIDLabel;
            if (request.ManagementCodeLabel != null) _configuration["ManagementCodeLabel"] = request.ManagementCodeLabel;
            if (request.CategoryLabel != null) _configuration["CategoryLabel"] = request.CategoryLabel;
            if (request.FirstTagLabel != null) _configuration["FirstTagLabel"] = request.FirstTagLabel;
            if (request.SecondTagLabel != null) _configuration["SecondTagLabel"] = request.SecondTagLabel;
            if (request.ThirdTagLabel != null) _configuration["ThirdTagLabel"] = request.ThirdTagLabel;

            _logger.LogInformation("Project settings updated successfully");
            return Ok(new { message = "Project settings updated successfully" });
        }
        catch (OperationCanceledException)
        {
            return StatusCode(499, "Request was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating project settings");
            return StatusCode(500, "Failed to update project settings");
        }
        finally
        {
            if (lockAcquired) _fileLock.Release();
        }
    }
}
