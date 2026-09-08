using System.Text.Json.Nodes;
using CREC_Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

internal static class SettingsCompatibilityTests
{
    public static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "CompatibilityProjects");
        var dataPath = Path.Combine(root, "data");
        Directory.CreateDirectory(Path.Combine(dataPath, "collection"));
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, dataPath);
        var projectPath = Path.Combine(root, "Relative.crec");
        var labels = new JsonObject();
        foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
            labels[key] = new JsonObject { ["displayName"] = key };
        File.WriteAllText(projectPath, new JsonObject {
            ["projectSettings"] = new JsonObject { ["projectName"] = "Relative", ["projectLocation"] = relativePath },
            ["labelSettings"] = labels,
            ["existingFlag"] = true
        }.ToJsonString());
        var configuration = new ConfigurationManager();
        var settings = new ProjectSettingsService(configuration);
        var loaded = settings.LoadProjectSettings(projectPath)!;
        Check(loaded.ProjectDataPath == relativePath, "startup preserves legacy relative path value");
        settings.ApplyProjectSettings(loaded, projectPath);
        var data = new CrecDataService(NullLogger<CrecDataService>.Instance, configuration);
        Check((await data.GetAllCollectionsAsync()).Count == 1, "startup reads relative data from the working directory");
        var catalog = new ProjectCatalogService(root);
        var runtime = new ProjectRuntime(configuration, settings, data, catalog);
        var id = catalog.List(projectPath).Projects.Single().Id;
        Check(catalog.Resolve(id).Settings.ProjectDataPath == dataPath, "switch validation uses the same working directory");
        Check(settings.UpdateProjectSettings(new() { ProjectName = "Renamed" }, out _), "legacy project settings remain editable");
        var saved = JsonNode.Parse(File.ReadAllText(projectPath))!;
        Check(saved["projectSettings"]!["projectLocation"]!.GetValue<string>() == relativePath
            && saved["existingFlag"]!.GetValue<bool>(), "settings save preserves path spelling and unrelated flags");
        Check(configuration["ProjectDataPath"] == relativePath && (await data.GetAllCollectionsAsync()).Count == 1,
            "settings save preserves the active data location");
        var originalBytes = File.ReadAllBytes(projectPath);
        Check((await runtime.SwitchAsync(id, runtime.Current.Revision, default)).Code == "projects-already-current"
            && File.ReadAllBytes(projectPath).SequenceEqual(originalBytes), "same-project switch keeps the existing relative path file unchanged");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAIL: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
