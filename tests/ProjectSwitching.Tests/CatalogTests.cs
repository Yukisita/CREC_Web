using System.Text.Json.Nodes;
using CREC_Web.Services;

internal static class CatalogTests
{
    public static void Run(string fixture)
    {
        var root = Path.Combine(fixture, "CatalogProjects");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        var path = Path.Combine(root, "Selected.crec");
        var labels = new JsonObject();
        foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
            labels[key] = new JsonObject { ["displayName"] = key };
        var json = new JsonObject {
            ["projectSettings"] = new JsonObject { ["projectName"] = "Selected", ["projectLocation"] = Path.Combine(root, "data") },
            ["labelSettings"] = labels
        };
        File.WriteAllText(path, json.ToJsonString());
        var catalog = new ProjectCatalogService(root);
        var id = catalog.List(path).Projects.Single().Id;

        var addedPath = Path.Combine(root, "Unlisted.crec");
        File.WriteAllText(addedPath, json.ToJsonString());
        var addedId = new ProjectCatalogService(root).List(path).Projects.Single(p => p.Location == "Unlisted.crec").Id;
        ExpectRejected(() => catalog.Resolve(addedId), "projects-not-found");
        if (catalog.Resolve(id).FilePath != path) throw new Exception("Original selection lost");
        Console.WriteLine("PASS: resolving a selection does not rediscover unrelated candidates");
        var externalData = Path.Combine(fixture, "CatalogExternalData");
        Directory.CreateDirectory(externalData);
        json["projectSettings"]!["projectLocation"] = externalData;
        File.WriteAllText(path, json.ToJsonString());
        if (catalog.Resolve(id).Settings.ProjectDataPath != externalData) throw new Exception("Changed data path was not reloaded");
        Directory.Delete(externalData);
        ExpectRejected(() => catalog.Resolve(id), "projects-data-unavailable");
        Console.WriteLine("PASS: external data location and availability revalidated after listing");
        File.Delete(path);
        catalog.List(path);
        ExpectRejected(() => catalog.Resolve(id), "projects-not-found");
        Console.WriteLine("PASS: refreshed listing forgets removed candidates");
        Directory.Delete(root, true);
        catalog.List(path);
        ExpectRejected(() => catalog.Resolve(addedId), "projects-not-found");
        Console.WriteLine("PASS: failed listing clears known identifiers");
    }

    internal static void ExpectRejected(Action action, string code)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (ProjectAccessException ex) when (ex.Code == code) { }
    }
}
