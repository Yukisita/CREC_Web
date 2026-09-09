namespace CREC_Web.Services;

/// <summary>現在のプロジェクトと世代。</summary>
/// <param name="Revision">起動・切り替えごとに更新する世代識別子。</param>
/// <param name="FilePath">現在の .crec ファイルの絶対パス。</param>
/// <param name="Name">画面やデスクトップのタイトルに表示する名前。</param>
public sealed record ProjectState(string Revision, string FilePath, string Name);

/// <summary>切り替え結果とプロジェクト状態。</summary>
/// <param name="Code">処理結果を表す翻訳キー。</param>
/// <param name="State">成功時は切り替え先、失敗時は元のプロジェクト状態。</param>
public sealed record ProjectSwitchResult(string Code, ProjectState State);

/// <summary>要求の完了を待ち、プロジェクトの設定とキャッシュを切り替える。</summary>
public sealed class ProjectRuntime
{
    // 要求数と状態を保護する。I/O の間は保持しない。
    private readonly object _stateLock = new();
    private readonly IConfiguration _configuration;
    private readonly ProjectSettingsService _settingsService;
    private readonly CrecDataService _dataService;
    private readonly ProjectCatalogService _catalog;
    private int _activeRequestCount;// ファイル送信を含め、完了待ちが必要な要求の数。
    private bool _isSwitching;// 検証から反映完了まで、新規要求を止める。
    private TaskCompletionSource? _requestsDrained;// 最後の要求が完了したときに切り替え処理を再開する。
    private ProjectState _currentState;

    /// <summary>起動時に読み込んだ設定を、最初のプロジェクト状態として登録する。</summary>
    /// <param name="configuration">現在のプロジェクト設定を保持する構成。</param>
    /// <param name="settings">プロジェクト設定を構成へ反映するサービス。</param>
    /// <param name="data">参照先とコレクションキャッシュを管理するサービス。</param>
    /// <param name="catalog">選択用識別子の解決と再検証を行うサービス。</param>
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
        get
        {
            lock (_stateLock)
            {
                return _currentState;
            }
        }
    }

    /// <summary>要求を受け付け、完了まで切り替えを待機させる。</summary>
    /// <param name="revision">要求元の画面が保持する世代。読み取り要求では省略可能。</param>
    /// <param name="requireRevision">更新要求など、世代の指定を必須にする場合は true。</param>
    /// <param name="error">拒否理由の翻訳キー。受け付けた場合は null。</param>
    /// <returns>要求完了時に破棄する受付ハンドル。要求を拒否した場合は null。</returns>
    public IDisposable? TryEnter(string? revision, bool requireRevision, out string? error)
    {
        lock (_stateLock)
        {
            error = GetAdmissionError(revision, requireRevision);
            if (error is not null)
            {
                return null;
            }

            _activeRequestCount++;
            return new RequestLease(this);
        }
    }

    /// <summary>切り替え状態と世代から受付可否を判定する。</summary>
    /// <param name="revision">要求元の世代。</param>
    /// <param name="requireRevision">世代を必須にする場合は true。</param>
    /// <returns>拒否理由の翻訳キー。受付可能な場合は null。</returns>
    /// <remarks>_stateLock の内側で呼び出すこと。</remarks>
    private string? GetAdmissionError(string? revision, bool requireRevision)
    {
        if (_isSwitching)
        {
            return "projects-busy";
        }

        var mustCheckRevision = requireRevision || !string.IsNullOrEmpty(revision);
        if (mustCheckRevision && revision != _currentState.Revision)
        {
            return "projects-stale";
        }

        return null;
    }

    /// <summary>要求の受付数を減らし、最後の要求なら切り替えの待機を解除する。</summary>
    /// <returns>なし。</returns>
    private void Exit()
    {
        lock (_stateLock)
        {
            _activeRequestCount--;
            if (_activeRequestCount == 0)
            {
                _requestsDrained?.TrySetResult();
            }
        }
    }

    /// <summary>要求の完了を待ち、選択先を再検証して切り替える。</summary>
    /// <param name="id">候補一覧で取得した切り替え先の識別子。</param>
    /// <param name="revision">操作元の画面が保持する世代。</param>
    /// <param name="cancellationToken">要求元の切断などで待機を中止するトークン。</param>
    /// <returns>処理結果の翻訳キーと、処理後のプロジェクト状態。</returns>
    /// <remarks>自身の要求受付ハンドルを保持したまま呼び出さないこと。</remarks>
    public async Task<ProjectSwitchResult> SwitchAsync(string id, string revision, CancellationToken cancellationToken)
    {
        Task requestsCompleted;
        lock (_stateLock)
        {
            var admissionError = GetAdmissionError(revision, requireRevision: true);
            if (admissionError is not null)
            {
                return new(admissionError, _currentState);
            }

            // 新規受付を先に停止し、既存要求が追加されない状態で完了を待つ。
            _isSwitching = true;
            _requestsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_activeRequestCount == 0)
            {
                _requestsDrained.SetResult();
            }
            requestsCompleted = _requestsDrained.Task;
        }

        try
        {
            await requestsCompleted.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var target = _catalog.Resolve(id);
            var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (target.FilePath.Equals(Current.FilePath, pathComparison))
            {
                return new("projects-already-current", Current);
            }

            ApplyProject(target);
            return new("projects-switched", Current);
        }
        catch (ProjectAccessException ex)
        {
            return new(ex.Code, Current);
        }
        finally
        {
            // 検証失敗やキャンセルの場合も、元のプロジェクトで要求の受付を再開する。
            lock (_stateLock)
            {
                _isSwitching = false;
                _requestsDrained = null;
            }
        }
    }

    /// <summary>設定と参照先を適用し、失敗時は復元する。</summary>
    /// <param name="target">切り替え直前の検証を通過したプロジェクト。</param>
    /// <returns>なし。適用時の例外は復元後に呼び出し元へ伝える。</returns>
    private void ApplyProject(ValidatedProject target)
    {
        // 復元対象はプロジェクトの設定だけとし、ポートや公開設定には触れない。
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
            {
                _configuration[setting.Key] = setting.Value;
            }
            _dataService.ResetProject(previousSettings["ProjectDataPath"]!);
            throw;
        }
    }

    /// <summary>要求完了時に、受付数を一度だけ減らすためのハンドル。</summary>
    /// <param name="runtime">要求を受け付けたプロジェクト実行状態。</param>
    private sealed class RequestLease(ProjectRuntime runtime) : IDisposable
    {
        private ProjectRuntime? _runtime = runtime;// null に交換することで二重解放を防ぐ。

        /// <summary>要求の完了を通知する。複数回呼ばれても受付数は一度だけ減らす。</summary>
        /// <returns>なし。</returns>
        public void Dispose()
        {
            Interlocked.Exchange(ref _runtime, null)?.Exit();
        }
    }
}
