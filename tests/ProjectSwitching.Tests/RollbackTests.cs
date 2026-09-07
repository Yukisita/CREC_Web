using CREC_Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;

internal static class RollbackTests
{
    public static async Task Run(IConfiguration configuration, ProjectCatalogService catalog, string targetId)
    {
        var snapshot = ProjectSettingsService.ConfigurationKeys.ToDictionary(key => key, key => configuration[key]);
        var failing = new FailingConfiguration(configuration);
        var settings = new ProjectSettingsService(failing);
        var data = new CrecDataService(NullLogger<CrecDataService>.Instance, failing);
        var runtime = new ProjectRuntime(failing, settings, data, catalog);
        var before = runtime.Current;
        failing.FailNextApply = true;
        try { await runtime.SwitchAsync(targetId, before.Revision, default); throw new Exception("Expected injected failure"); }
        catch (IOException ex) when (ex.Message == "Injected configuration failure") { }
        if (runtime.Current != before || snapshot.Any(pair => configuration[pair.Key] != pair.Value))
            throw new Exception("Partial switch did not restore original state");
        if ((await data.GetAllCollectionsAsync()).Count != 1) throw new Exception("Rollback did not restore original data reference");
        using var lease = runtime.TryEnter(before.Revision, true, out var error);
        if (lease is null || error is not null) throw new Exception("Rollback did not resume updates");
        Console.WriteLine("PASS: failure during configuration apply rolls back settings, data, revision and request admission");
    }

    private sealed class FailingConfiguration(IConfiguration inner) : IConfiguration
    {
        public bool FailNextApply { get; set; }
        public string? this[string key]
        {
            get => inner[key];
            set
            {
                if (FailNextApply && key == "CollectionNameLabel")
                {
                    FailNextApply = false;
                    throw new IOException("Injected configuration failure");
                }
                inner[key] = value;
            }
        }
        public IEnumerable<IConfigurationSection> GetChildren() => inner.GetChildren();
        public IChangeToken GetReloadToken() => inner.GetReloadToken();
        public IConfigurationSection GetSection(string key) => inner.GetSection(key);
    }
}
