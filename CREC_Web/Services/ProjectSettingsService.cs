/*
CREC Web - Project Settings Service
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Text;

namespace CREC_Web.Services;

public class ProjectSettingsService
{
    private readonly IConfiguration _configuration;

    // ファイルの読み込み・保存をスレッドセーフに行うためのロックオブジェクト
    private static readonly object _fileLock = new object();

    public ProjectSettingsService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// プロジェクト設定ファイル読み込む
    /// </summary>
    /// <param name="crecFilePath">プロジェクト設定ファイルのパス</param>
    /// <returns>プロジェクト設定値</returns>
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

                var settings = new ProjectSettings();
                var lines = File.ReadAllLines(crecFilePath, Encoding.GetEncoding("UTF-8"));

                Console.WriteLine($"Parsing .crec file with {lines.Length} lines...");

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cols = line.Split(',', 2);
                    if (cols.Length < 2) continue;

                    var key = cols[0].Trim();
                    var value = cols[1].Trim();

                    switch (key)
                    {
                        case "projectname":
                            settings.ProjectName = value;
                            Console.WriteLine($"  - Found projectname: {value}");
                            break;
                        case "projectlocation":
                            settings.ProjectDataPath = value;
                            Console.WriteLine($"  - Found projectlocation: {value}");
                            break;
                        case "ShowObjectNameLabel":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.CollectionNameLabel = value;
                                Console.WriteLine($"  - Found ShowObjectNameLabel: {value}");
                            }
                            break;
                        case "ShowIDLabel":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.UUIDLabel = value;
                                Console.WriteLine($"  - Found ShowIDLabel: {value}");
                            }
                            break;
                        case "ShowMCLabel":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.ManagementCodeLabel = value;
                                Console.WriteLine($"  - Found ShowMCLabel: {value}");
                            }
                            break;
                        case "ShowCategoryLabel":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.CategoryLabel = value;
                                Console.WriteLine($"  - Found ShowCategoryLabel: {value}");
                            }
                            break;
                        case "Tag1Name":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.FirstTagLabel = value;
                                Console.WriteLine($"  - Found Tag1Name: {value}");
                            }
                            break;
                        case "Tag2Name":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.SecondTagLabel = value;
                                Console.WriteLine($"  - Found Tag2Name: {value}");
                            }
                            break;
                        case "Tag3Name":
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                settings.ThirdTagLabel = value;
                                Console.WriteLine($"  - Found Tag3Name: {value}");
                            }
                            break;
                    }
                }

                Console.WriteLine("Finished parsing .crec file");

                if (string.IsNullOrEmpty(settings.ProjectDataPath))
                {
                    Console.WriteLine("Error: 'projectlocation' not found in .crec file");
                    return null;
                }

                if (!Directory.Exists(settings.ProjectDataPath))
                {
                    Console.WriteLine($"Warning: Project data folder does not exist: {settings.ProjectDataPath}");
                }

                return settings;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing .crec file: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>
    /// アプリケーションのプロジェクト設定値を更新
    /// </summary>
    /// <param name="projectSettings">プロジェクト設定値</param>
    /// <param name="crecFilePath">プロジェクト設定ファイルのパス</param>
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
    /// プロジェクト設定ファイルを更新する
    /// </summary>
    /// <param name="request">更新リクエスト（設定値）</param>
    /// <param name="message">更新時のメッセージ</param>
    /// <returns>成功: true / 失敗: false</returns>
    public bool UpdateProjectSettings(UpdateProjectSettingsRequest request, out string message)
    {
        var crecFilePath = _configuration["CrecFilePath"];
        if (string.IsNullOrEmpty(crecFilePath) || !File.Exists(crecFilePath))
        {
            message = "Project file path is not configured or file does not exist";
            return false;
        }

        try
        {
            lock (_fileLock)
            {
                var lines = File.ReadAllLines(crecFilePath, Encoding.UTF8);
                var updatedLines = new List<string>();
                var updatedKeys = new HashSet<string>();

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        updatedLines.Add(line);
                        continue;
                    }

                    var cols = line.Split(',', 2);
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

                File.WriteAllLines(crecFilePath, updatedLines, Encoding.UTF8);

                // アプリケーションのプロジェクト設定値を更新
                if (request.ProjectName != null) _configuration["ProjectName"] = request.ProjectName;
                if (request.CollectionNameLabel != null) _configuration["CollectionNameLabel"] = request.CollectionNameLabel;
                if (request.UUIDLabel != null) _configuration["UUIDLabel"] = request.UUIDLabel;
                if (request.ManagementCodeLabel != null) _configuration["ManagementCodeLabel"] = request.ManagementCodeLabel;
                if (request.CategoryLabel != null) _configuration["CategoryLabel"] = request.CategoryLabel;
                if (request.FirstTagLabel != null) _configuration["FirstTagLabel"] = request.FirstTagLabel;
                if (request.SecondTagLabel != null) _configuration["SecondTagLabel"] = request.SecondTagLabel;
                if (request.ThirdTagLabel != null) _configuration["ThirdTagLabel"] = request.ThirdTagLabel;
            }

            message = "Project settings updated successfully";
            return true;
        }
        catch
        {
            message = "Failed to update project settings";
            return false;
        }
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
