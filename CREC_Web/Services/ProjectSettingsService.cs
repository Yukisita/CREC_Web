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

    /// <summary>
    /// プロジェクト設定サービスを初期化する。
    /// </summary>
    /// <param name="configuration">プロジェクト設定を反映するアプリケーション構成。</param>
    public ProjectSettingsService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// JSON形式のプロジェクトファイルを読み込む。
    /// </summary>
    /// <param name="crecFilePath">読み込むプロジェクトファイルのパス。</param>
    /// <returns>読み込んだプロジェクト設定。読み込みに失敗した場合はnull。</returns>
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

    /// <summary>
    /// プロジェクト設定をアプリケーションに反映する。
    /// </summary>
    /// <param name="projectSettings">アプリケーションに反映するプロジェクト設定。</param>
    /// <param name="crecFilePath">プロジェクトファイルのパス。</param>
    /// <returns>なし。</returns>
    public void ApplyProjectSettings(ProjectSettings projectSettings, string crecFilePath)
    {
        _configuration["ProjectDataPath"] = projectSettings.ProjectDataPath;
        _configuration["CrecFilePath"] = crecFilePath;
        _configuration["ProjectName"] = projectSettings.ProjectName;
        _configuration["CollectionNameLabel"] = projectSettings.CollectionNameLabel;
        _configuration["UUIDLabel"] = projectSettings.UUIDLabel;
        _configuration["ManagementCodeLabel"] = projectSettings.ManagementCodeLabel;
        _configuration["CategoryLabel"] = projectSettings.CategoryLabel;
        _configuration["FirstTagLabel"] = projectSettings.FirstTagLabel;
        _configuration["SecondTagLabel"] = projectSettings.SecondTagLabel;
        _configuration["ThirdTagLabel"] = projectSettings.ThirdTagLabel;
    }

    /// <summary>
    /// CREC Webが編集する値だけを更新し、その他の設定やフラグはそのまま保存する。
    /// </summary>
    /// <param name="request">プロジェクト設定の更新内容。</param>
    /// <param name="message">更新結果を説明するメッセージ。</param>
    /// <returns>更新に成功した場合はtrue、それ以外はfalse。</returns>
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

    /// <summary>
    /// プロジェクトファイルをJSONオブジェクトとして読み込む。
    /// </summary>
    /// <param name="path">読み込むプロジェクトファイルのパス。</param>
    /// <returns>プロジェクトファイルのルートJSONオブジェクト。</returns>
    private static JsonObject ReadProjectFile(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
            ?? throw new InvalidDataException("The project file root must be a JSON object.");
    }

    /// <summary>
    /// JSONオブジェクトからCREC Webが使用するプロジェクト設定を取得する。
    /// </summary>
    /// <param name="root">プロジェクトファイルのルートJSONオブジェクト。</param>
    /// <returns>CREC Webが使用するプロジェクト設定。</returns>
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

    /// <summary>
    /// ラベル設定から表示名を取得する。
    /// </summary>
    /// <param name="labels">ラベル設定を保持するJSONオブジェクト。</param>
    /// <param name="name">取得するラベル設定のプロパティ名。</param>
    /// <param name="defaultValue">表示名が未設定の場合に使用する既定値。</param>
    /// <returns>ラベルの表示名。</returns>
    private static string ReadLabel(JsonObject labels, string name, string defaultValue)
    {
        var displayName = GetObject(labels, name)["displayName"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(displayName) ? defaultValue : displayName;
    }

    /// <summary>
    /// 親JSONオブジェクトから指定した子JSONオブジェクトを取得する。
    /// </summary>
    /// <param name="parent">検索対象の親JSONオブジェクト。</param>
    /// <param name="name">取得する子JSONオブジェクトのプロパティ名。</param>
    /// <returns>指定した子JSONオブジェクト。</returns>
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
