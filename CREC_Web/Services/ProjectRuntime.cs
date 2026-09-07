namespace CREC_Web.Services;

public sealed record ProjectState(string Revision, string FilePath, string Name);
public sealed record ProjectSwitchResult(string Code, ProjectState State);

/// <summary>Drains whole requests (including uploads and streamed results) before switching.</summary>
public sealed class ProjectRuntime
{
    private readonly object _sync = new();
    private readonly IConfiguration _configuration;
    private readonly ProjectSettingsService _settings;
    private readonly CrecDataService _data;
    private readonly ProjectCatalogService _catalog;
    private int _requests;
    private bool _switching;
    private TaskCompletionSource? _drained;
    private ProjectState _current;

    public ProjectRuntime(IConfiguration configuration, ProjectSettingsService settings,
        CrecDataService data, ProjectCatalogService catalog)
    {
        _configuration = configuration;
        _settings = settings;
        _data = data;
        _catalog = catalog;
        _current = new(Guid.NewGuid().ToString("N"), Path.GetFullPath(configuration["CrecFilePath"]!),
            configuration["ProjectName"] ?? "CREC Project");
    }

    public ProjectState Current { get { lock (_sync) return _current; } }

    public IDisposable? TryEnter(string? revision, bool requireRevision, out string? error)
    {
        lock (_sync)
        {
            error = _switching ? "projects-busy"
                : ((requireRevision || !string.IsNullOrEmpty(revision)) && revision != _current.Revision)
                    ? "projects-stale" : null;
            if (error is not null) return null;
            _requests++;
            return new RequestLease(this);
        }
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (--_requests == 0) _drained?.TrySetResult();
        }
    }

    /// <summary>Reusable by project creation (#196): pass a catalog ID after saving within Projects.</summary>
    public async Task<ProjectSwitchResult> SwitchAsync(string id, string revision, CancellationToken cancellationToken)
    {
        Task wait;
        lock (_sync)
        {
            if (_switching) return new("projects-busy", _current);
            if (revision != _current.Revision) return new("projects-stale", _current);
            _switching = true;
            _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_requests == 0) _drained.SetResult();
            wait = _drained.Task;
        }
        try
        {
            await wait.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var target = _catalog.Resolve(id, Current.FilePath);
            if (target.FilePath.Equals(Current.FilePath, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new("projects-already-current", Current);

            // Only project keys change. Listener ports and publication settings are untouched.
            var keys = ProjectSettingsService.ConfigurationKeys;
            var previous = keys.ToDictionary(key => key, key => _configuration[key]);
            try
            {
                _settings.ApplyProjectSettings(target.Settings, target.FilePath);
                _data.ResetProject(target.Settings.ProjectDataPath);
                lock (_sync)
                    _current = new(Guid.NewGuid().ToString("N"), target.FilePath, target.Settings.ProjectName);
            }
            catch
            {
                foreach (var pair in previous) _configuration[pair.Key] = pair.Value;
                _data.ResetProject(previous["ProjectDataPath"]!);
                throw;
            }
            return new("projects-switched", Current);
        }
        catch (ProjectAccessException ex) { return new(ex.Code, Current); }
        finally
        {
            lock (_sync) { _switching = false; _drained = null; }
        }
    }

    private sealed class RequestLease(ProjectRuntime runtime) : IDisposable
    {
        private ProjectRuntime? _runtime = runtime;
        public void Dispose() => Interlocked.Exchange(ref _runtime, null)?.Exit();
    }
}
