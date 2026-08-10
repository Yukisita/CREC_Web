/*
CREC Web - Project Settings Service
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CREC_Web.Services;

public class ProjectSettingsService
{
    private readonly IConfiguration _configuration;
    private static readonly object _fileLock = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ProjectSettingsService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>JSON形式のプロジェクトファイルを読み込む。</summary>
    public ProjectSettings? LoadProjectSettings(string crecFilePath)
    {
        lock (_fileLock)
        {
            try
            {
                if (!File.Exists(crecFilePath))
                {
                    Console.WriteLine($"Error: .crec file not found: {crecFilePath}");
                    return null;
                }

                var settings = ReadSettings(ReadProjectFile(crecFilePath));
                Console.WriteLine($"Loaded project settings: {settings.ProjectName}");

                if (!Directory.Exists(settings.ProjectDataPath))
                {
                    Console.WriteLine($"Warning: Project data folder does not exist: {settings.ProjectDataPath}");
                }

                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading .crec JSON file: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>プロジェクト設定をアプリケーションに反映する。</summary>
    public void ApplyProjectSettings(ProjectSettings settings, string crecFilePath)
    {
        _configuration["ProjectDataPath"] = settings.ProjectDataPath;
        _configuration["CrecFilePath"] = crecFilePath;
        _configuration["ProjectName"] = settings.ProjectName;
        _configuration["CollectionNameLabel"] = settings.CollectionNameLabel;
        _configuration["UUIDLabel"] = settings.UUIDLabel;
        _configuration["ManagementCodeLabel"] = settings.ManagementCodeLabel;
        _configuration["CategoryLabel"] = settings.CategoryLabel;
        _configuration["FirstTagLabel"] = settings.FirstTagLabel;
        _configuration["SecondTagLabel"] = settings.SecondTagLabel;
        _configuration["ThirdTagLabel"] = settings.ThirdTagLabel;
    }

    /// <summary>
    /// CREC Webが編集する値だけを更新し、その他の設定やフラグはそのまま保存する。
    /// </summary>
    public bool UpdateProjectSettings(UpdateProjectSettingsRequest request, out string message)
    {
        var crecFilePath = _configuration["CrecFilePath"];
        if (string.IsNullOrEmpty(crecFilePath) || !File.Exists(crecFilePath))
        {
            message = "Project file path is not configured or file does not exist";
            return false;
        }

        var labelUpdates = new (string JsonName, string? Value)[]
        {
            ("objectName", request.CollectionNameLabel),
            ("id", request.UUIDLabel),
            ("mc", request.ManagementCodeLabel),
            ("category", request.CategoryLabel),
            ("tag1", request.FirstTagLabel),
            ("tag2", request.SecondTagLabel),
            ("tag3", request.ThirdTagLabel)
        };

        try
        {
            lock (_fileLock)
            {
                var root = ReadProjectFile(crecFilePath);
                var project = GetObject(root, "projectSettings");
                var labels = GetObject(root, "labelSettings");

                if (request.ProjectName is not null)
                {
                    project["projectName"] = request.ProjectName;
                }

                foreach (var update in labelUpdates)
                {
                    if (update.Value is not null)
                    {
                        // displayNameだけを変更し、enabledフラグには触れない。
                        GetObject(labels, update.JsonName)["displayName"] = update.Value;
                    }
                }

                var updatedSettings = ReadSettings(root);
                File.WriteAllText(
                    crecFilePath,
                    root.ToJsonString(_jsonOptions),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                ApplyProjectSettings(updatedSettings, crecFilePath);
            }

            message = "Project settings updated successfully";
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException)
        {
            message = "Invalid project file format: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            message = "Failed to update project settings: " + ex.Message;
            return false;
        }
    }

    private static JsonObject ReadProjectFile(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("The project file root must be a JSON object.");
    }

    private static ProjectSettings ReadSettings(JsonObject root)
    {
        var defaults = new ProjectSettings();
        var project = GetObject(root, "projectSettings");
        var labels = GetObject(root, "labelSettings");
        var projectDataPath = project["projectLocation"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(projectDataPath))
        {
            throw new InvalidDataException("projectSettings.projectLocation is required.");
        }

        return new ProjectSettings
        {
            ProjectName = project["projectName"]?.GetValue<string>() ?? defaults.ProjectName,
            ProjectDataPath = projectDataPath,
            CollectionNameLabel = ReadLabel(labels, "objectName", defaults.CollectionNameLabel),
            UUIDLabel = ReadLabel(labels, "id", defaults.UUIDLabel),
            ManagementCodeLabel = ReadLabel(labels, "mc", defaults.ManagementCodeLabel),
            CategoryLabel = ReadLabel(labels, "category", defaults.CategoryLabel),
            FirstTagLabel = ReadLabel(labels, "tag1", defaults.FirstTagLabel),
            SecondTagLabel = ReadLabel(labels, "tag2", defaults.SecondTagLabel),
            ThirdTagLabel = ReadLabel(labels, "tag3", defaults.ThirdTagLabel)
        };
    }

    private static string ReadLabel(JsonObject labels, string name, string defaultValue)
    {
        var displayName = GetObject(labels, name)["displayName"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(displayName) ? defaultValue : displayName;
    }

    private static JsonObject GetObject(JsonObject parent, string name)
    {
        return parent[name] as JsonObject
            ?? throw new InvalidDataException($"{name} must be a JSON object.");
    }

}

public class ProjectSettings
{
    public string ProjectName { get; set; } = "CREC Project";
    public string ProjectDataPath { get; set; } = "";
    public string CollectionNameLabel { get; set; } = "Name";
    public string UUIDLabel { get; set; } = "UUID";
    public string ManagementCodeLabel { get; set; } = "MC";
    public string CategoryLabel { get; set; } = "Category";
    public string FirstTagLabel { get; set; } = "Tag 1";
    public string SecondTagLabel { get; set; } = "Tag 2";
    public string ThirdTagLabel { get; set; } = "Tag 3";
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
