using System.Text.Json.Nodes;
using CREC_Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

// Dependency-free executable regression tests: dotnet run --project tests/ProjectSwitching.Tests
var fixture = Path.Combine(Path.GetTempPath(), "crec-switch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
try
{
    var root = Path.Combine(fixture, "Projects");
    var catalog = new ProjectCatalogService(root);
    Check(catalog.List(Path.Combine(fixture, "old.crec")).ErrorCode == "projects-missing", "missing Projects");
    Directory.CreateDirectory(root);
    Check(catalog.List(Path.Combine(fixture, "old.crec")).Projects.Count == 0, "empty Projects");
    string CreateProject(string name, string location)
    {
        var labels = new JsonObject();
        foreach (var key in new[] { "objectName", "id", "mc", "category", "tag1", "tag2", "tag3" })
            labels[key] = new JsonObject { ["displayName"] = name + key };
        var json = new JsonObject {
            ["projectSettings"] = new JsonObject { ["projectName"] = name, ["projectLocation"] = location },
            ["labelSettings"] = labels
        };
        var path = Path.Combine(root, name + ".crec");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }
    Directory.CreateDirectory(Path.Combine(root, "a"));
    Directory.CreateDirectory(Path.Combine(root, "b"));
    Directory.CreateDirectory(Path.Combine(root, "a", "same-id"));
    var a = CreateProject("A", Path.Combine(root, "a"));
    var b = CreateProject("B", Path.Combine(root, "b"));
    var externalData = Path.Combine(fixture, "ExternalData");
    Directory.CreateDirectory(externalData);
    var outside = CreateProject("Outside", externalData);
    File.WriteAllText(Path.Combine(root, "invalid.crec"), "{}");
    var initialBytes = File.ReadAllBytes(a);
    var listing = catalog.List(a);
    Check(listing.Projects.Count == 4, "invalid projects remain visible");
    Check(listing.Projects.Single(p => p.Name == "A").IsCurrent, "current project marker");
    Check(listing.Projects.Single(p => p.Location == "Outside.crec").ErrorCode is null, "external data allowed for a project file inside Projects");
    Check(listing.Projects.Single(p => p.Location == "invalid.crec").ErrorCode == "projects-invalid", "format rejected");
    foreach (var path in new[] { Path.Combine(root, "..", "escape.crec"), Path.Combine(fixture, "Projects-evil", "x.crec") })
    {
        try { catalog.EnsureSafePath(path); throw new Exception("Expected containment rejection"); }
        catch (ProjectAccessException ex) { Check(ex.Code == "projects-outside-root", "normalized containment"); }
    }
    var configuration = new ConfigurationManager();
    configuration["urls"] = "http://127.0.0.1:4567";
    var settings = new ProjectSettingsService(configuration);
    settings.ApplyProjectSettings(ProjectSettingsService.ReadValidatedSettings(a), a);
    var data = new CrecDataService(NullLogger<CrecDataService>.Instance, configuration);
    var runtime = new ProjectRuntime(configuration, settings, data, catalog);
    var original = runtime.Current;
    var aId = listing.Projects.Single(p => p.Name == "A").Id;
    var bId = listing.Projects.Single(p => p.Name == "B").Id;
    try { new ProjectCatalogService(root).Resolve(bId); throw new Exception("Expected unlisted ID rejection"); }
    catch (ProjectAccessException ex) { Check(ex.Code == "projects-not-found", "only server-listed identifiers accepted"); }
    Check((await data.GetAllCollectionsAsync()).Count == 1, "A cache primed");
    using (var request = runtime.TryEnter(original.Revision, true, out var error))
    {
        Check(request is not null && error is null, "request admitted");
        var switching = runtime.SwitchAsync(bId, original.Revision, default);
        Check(!switching.IsCompleted, "switch drains active requests");
        Check(runtime.TryEnter(original.Revision, true, out error) is null && error == "projects-busy", "new requests blocked");
        Check((await runtime.SwitchAsync(aId, original.Revision, default)).Code == "projects-busy", "concurrent switch refused");
        request!.Dispose();
        Check((await switching).Code == "projects-switched", "switch completed after drain");
    }
    Check((await data.GetAllCollectionsAsync()).Count == 0, "empty B does not reuse A cache");
    Check(configuration["CollectionNameLabel"] == "BobjectName", "labels switched");
    Check(configuration["urls"] == "http://127.0.0.1:4567", "listener settings preserved");
    Check(runtime.TryEnter(original.Revision, true, out var stale) is null && stale == "projects-stale", "stale writes refused");
    Check(runtime.TryEnter(null, true, out stale) is null && stale == "projects-stale", "unversioned writes refused");
    Check((await runtime.SwitchAsync(bId, runtime.Current.Revision, default)).Code == "projects-already-current", "same project no-op");
    Check((await runtime.SwitchAsync("../A.crec", runtime.Current.Revision, default)).Code == "projects-not-found", "client paths refused");
    Check((await runtime.SwitchAsync(aId, runtime.Current.Revision, default)).Code == "projects-switched", "B to A");
    Check((await data.GetAllCollectionsAsync()).Count == 1, "A cache refreshed on return");
    Check(File.ReadAllBytes(a).SequenceEqual(initialBytes), "switch leaves original project unchanged");
    var outsideId = listing.Projects.Single(p => p.Location == "Outside.crec").Id;
    var outsideBytes = File.ReadAllBytes(outside);
    Check((await runtime.SwitchAsync(outsideId, runtime.Current.Revision, default)).Code == "projects-switched"
        && configuration["ProjectDataPath"] == externalData, "switch to external data folder");
    Check((await data.GetAllCollectionsAsync()).Count == 0 && File.ReadAllBytes(outside).SequenceEqual(outsideBytes),
        "external data switch clears cache without rewriting project path");
    Check((await runtime.SwitchAsync(aId, runtime.Current.Revision, default)).Code == "projects-switched", "return from external data project");
    Directory.Delete(externalData);
    Check((await runtime.SwitchAsync(outsideId, runtime.Current.Revision, default)).Code == "projects-data-unavailable"
        && runtime.Current.FilePath == a, "missing external data preserves current project");
    await LinkTests.Run(fixture, root, a);
    CatalogTests.Run(fixture);
    await SettingsCompatibilityTests.Run(fixture);
    await RollbackTests.Run(configuration, catalog, bId);
    File.WriteAllText(b, "{}");
    Check((await runtime.SwitchAsync(bId, runtime.Current.Revision, default)).Code == "projects-invalid", "selection revalidated after file changed");
    File.Delete(b);
    Check((await runtime.SwitchAsync(bId, runtime.Current.Revision, default)).Code == "projects-not-found", "deleted selection refused");
    Check(runtime.Current.FilePath == a, "failed switch preserves active project");
    using (var held = runtime.TryEnter(runtime.Current.Revision, true, out _))
    using (var cancellation = new CancellationTokenSource())
    {
        var pending = runtime.SwitchAsync(aId, runtime.Current.Revision, cancellation.Token);
        cancellation.Cancel();
        try { await pending; throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }
    using var resumed = runtime.TryEnter(runtime.Current.Revision, true, out var resumedError);
    Check(resumed is not null && resumedError is null, "cancellation resumes request admission");
    await RequestTests.Run(runtime);
    await HttpTests.Run(fixture, args.Contains("--serve"));
    if (!args.Contains("--serve")) await DesktopHostTests.Run(fixture);
    Console.WriteLine("All project switching regression tests passed.");
}
finally
{
    Directory.Delete(fixture, recursive: true);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
}
