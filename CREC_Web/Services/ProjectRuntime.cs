namespace CREC_Web.Services;

/// <summary>現在のプロジェクトと世代。</summary>
/// <param name="Revision">起動・切り替えごとの世代。</param>
/// <param name="FilePath">現在の .crec の絶対パス。</param>
/// <param name="Name">表示名。</param>
public sealed record ProjectState(string Revision, string FilePath, string Name);

/// <summary>切り替え結果とプロジェクト状態。</summary>
/// <param name="Code">処理結果を表す翻訳キー。</param>
/// <param name="State">処理後の状態。</param>
public sealed record ProjectSwitchResult(string Code, ProjectState State);

/// <summary>要求の完了を待ち、プロジェクトを切り替える。</summary>
public sealed class ProjectRuntime
{
    // 要求数と状態を保護する。I/O の間は保持しない。
    private readonly object _stateLock = new();
    private readonly IConfiguration _configuration;
    private readonly ProjectSettingsService _settingsService;
    private readonly CrecDataService _dataService;
    private readonly ProjectCatalogService _catalog;
    private int _activeRequestCount;// 完了待ちの要求数。
    private bool _isSwitching;// 新規要求を停止中か。
    private TaskCompletionSource? _requestsDrained;// 要求完了の通知。
    private ProjectState _currentState;

    /// <summary>起動時のプロジェクトを登録する。</summary>
    /// <param name="configuration">現在の設定。</param>
    /// <param name="settings">設定の反映先。</param>
    /// <param name="data">データとキャッシュの管理。</param>
    /// <param name="catalog">候補の探索・検証。</param>
    public ProjectRuntime(IConfiguration configuration, ProjectSettingsService settings,
        CrecDataService data, ProjectCatalogService catalog)
    {
        _configuration = configuration;
        _settingsService = settings;
        _dataService = data;
        _catalog = catalog;
        _currentState = new(Guid.NewGuid().ToString("N"), Path.GetFullPath(configuration["CrecFilePath"]!),
            configuration["ProjectName"] ?? "CREC Project");
    }

    /// <summary>同じ時点の世代・パス・名前を返す。</summary>
    public ProjectState Current
    {
        get { lock (_stateLock) return _currentState; }
    }

    /// <summary>要求を受け付け、切り替えを待機させる。</summary>
    /// <param name="revision">要求元の世代。読み取りでは省略可。</param>
    /// <param name="requireRevision">世代を必須にするか。</param>
    /// <param name="error">拒否理由。受付可能なら null。</param>
    /// <returns>完了時に破棄するハンドル。拒否時は null。</returns>
    public IDisposable? TryEnter(string? revision, bool requireRevision, out string? error)
    {
        lock (_stateLock)
        {
            error = GetAdmissionError(revision, requireRevision);
            if (error is not null)
                return null;

            _activeRequestCount++;
            return new RequestLease(this);
        }
    }

    /// <summary>切り替え状態と世代から受付可否を判定する。</summary>
    /// <param name="revision">要求元の世代。</param>
    /// <param name="requireRevision">世代を必須にするか。</param>
    /// <returns>拒否理由。受付可能なら null。</returns>
    /// <remarks>_stateLock の内側で呼び出すこと。</remarks>
    private string? GetAdmissionError(string? revision, bool requireRevision)
    {
        if (_isSwitching)
            return "projects-busy";

        var mustCheckRevision = requireRevision || !string.IsNullOrEmpty(revision);
        if (mustCheckRevision && revision != _currentState.Revision)
            return "projects-stale";

        return null;
    }

    /// <summary>要求数を減らし、全件完了を通知する。</summary>
    /// <returns>なし。</returns>
    private void Exit()
    {
        lock (_stateLock)
        {
            _activeRequestCount--;
            if (_activeRequestCount == 0)
                _requestsDrained?.TrySetResult();
        }
    }

    /// <summary>要求の完了を待ち、選択先を再検証して切り替える。</summary>
    /// <param name="id">一覧で発行した識別子。</param>
    /// <param name="revision">操作元の世代。</param>
    /// <param name="cancellationToken">待機の中止通知。</param>
    /// <returns>結果コードと処理後の状態。</returns>
    /// <remarks>自身の要求受付ハンドルを保持したまま呼び出さないこと。</remarks>
    public async Task<ProjectSwitchResult> SwitchAsync(string id, string revision, CancellationToken cancellationToken)
    {
        Task requestsCompleted;
        lock (_stateLock)
        {
            var admissionError = GetAdmissionError(revision, requireRevision: true);
            if (admissionError is not null)
                return new(admissionError, _currentState);

            // 新規受付を止めてから、既存要求の完了を待つ。
            _isSwitching = true;
            _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeRequestCount == 0)
                _requestsDrained.SetResult();
            requestsCompleted = _requestsDrained.Task;
        }

        try
        {
            await requestsCompleted.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var target = _catalog.Resolve(id);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (target.FilePath.Equals(Current.FilePath, pathComparison))
                return new("projects-already-current", Current);

            ApplyProject(target);
            return new("projects-switched", Current);
        }
        catch (ProjectAccessException ex)
        {
            return new(ex.Code, Current);
        }
        finally
        {
            // 失敗・キャンセル時も受付を再開する。
            lock (_stateLock)
            {
                _isSwitching = false;
                _requestsDrained = null;
            }
        }
    }

    /// <summary>設定と参照先を適用し、失敗時は復元する。</summary>
    /// <param name="target">検証済みの切り替え先。</param>
    /// <returns>なし。失敗時は復元後に例外を伝える。</returns>
    private void ApplyProject(ValidatedProject target)
    {
        // 復元用の設定。ポートと公開設定は含めない。
        var previousSettings = ProjectSettingsService.ConfigurationKeys.ToDictionary(key => key, key => _configuration[key]);
        try
        {
            _settingsService.ApplyProjectSettings(target.Settings, target.FilePath);
            _dataService.ResetProject(target.Settings.ProjectDataPath);

            // 設定とキャッシュが揃ってから新しい世代を公開する。
            lock (_stateLock)
            {
                _currentState = new(Guid.NewGuid().ToString("N"), target.FilePath, target.Settings.ProjectName);
            }
        }
        catch
        {
            foreach (var setting in previousSettings)
                _configuration[setting.Key] = setting.Value;
            _dataService.ResetProject(previousSettings["ProjectDataPath"]!);
            throw;
        }
    }

    /// <summary>要求完了を一度だけ通知するハンドル。</summary>
    /// <param name="runtime">要求数の管理元。</param>
    private sealed class RequestLease(ProjectRuntime runtime) : IDisposable
    {
        private ProjectRuntime? _runtime = runtime;// null に交換することで二重解放を防ぐ。

        /// <summary>要求の完了を通知する。</summary>
        /// <returns>なし。</returns>
        public void Dispose() => Interlocked.Exchange(ref _runtime, null)?.Exit();
    }
}
