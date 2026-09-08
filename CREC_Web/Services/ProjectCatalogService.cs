using System.Security.Cryptography;
using System.Text;

namespace CREC_Web.Services;

/// <summary>一覧に表示するプロジェクトと、選択できない場合の理由を保持する。</summary>
/// <param name="Id">サーバーが相対パスから生成する選択用識別子。</param>
/// <param name="Name">表示名。検証できない場合はファイル名。</param>
/// <param name="Location">Projects からの相対パス。</param>
/// <param name="IsCurrent">現在開いているプロジェクトかどうか。</param>
/// <param name="ErrorCode">選択不可の理由を表す翻訳キー。選択可能な場合は null。</param>
public sealed record ProjectCandidate(string Id, string Name, string Location, bool IsCurrent, string? ErrorCode);

/// <summary>一覧の取得結果を保持する。</summary>
/// <param name="ErrorCode">一覧全体の取得失敗を表す翻訳キー。取得成功時は null。</param>
/// <param name="Projects">選択不可の項目を含む候補一覧。</param>
public sealed record ProjectListing(string? ErrorCode, IReadOnlyList<ProjectCandidate> Projects);

/// <summary>切り替え直前の検証を通過したファイルと設定を保持する。</summary>
/// <param name="FilePath">プロジェクトファイルの絶対パス。</param>
/// <param name="Settings">実データのパスを絶対パスへ解決済みの設定。</param>
public sealed record ValidatedProject(string FilePath, ProjectSettings Settings);

/// <summary>プロジェクトを選択できない理由を呼び出し元へ伝える。</summary>
/// <param name="code">画面で理由を表示するための翻訳キー。</param>
public sealed class ProjectAccessException(string code) : Exception(code)
{
    /// <summary>選択不可の理由を表す翻訳キー。</summary>
    public string Code { get; } = code;
}

/// <summary>Projects 内の候補を探索し、サーバー側でパスと読み込み可否を検証する。</summary>
public sealed class ProjectCatalogService
{
    // 一覧取得ごとに辞書全体を交換する。検証結果は保存せず、切り替え直前に読み直す。
    private Dictionary<string, string> _listedLocations = new();

    /// <summary>選択可能な .crec ファイルの探索元。実データの保存先はここに限定しない。</summary>
    public string ProjectsRoot { get; }

    // Windows では大文字・小文字だけが違うパスも同じファイルとして扱う。
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Web 実行ファイルの隣にある Projects を探索元として初期化する。</summary>
    public ProjectCatalogService() : this(Path.Combine(AppContext.BaseDirectory, "Projects")) { }

    /// <summary>指定されたフォルダを探索元として初期化する。</summary>
    /// <param name="projectsRoot">.crec ファイルの配置を許可するフォルダ。</param>
    public ProjectCatalogService(string projectsRoot)
    {
        ProjectsRoot = Path.GetFullPath(projectsRoot);
    }

    /// <summary>候補を検証して一覧を作り、選択用識別子と相対パスの対応を保持する。</summary>
    /// <param name="currentPath">現在開いている .crec ファイルのパス。</param>
    /// <returns>保存場所順の候補一覧。取得できない場合はエラー理由と空の一覧。</returns>
    public ProjectListing List(string currentPath)
    {
        try
        {
            EnsureSafePath(ProjectsRoot);
            if (!Directory.Exists(ProjectsRoot))
            {
                return FailedListing("projects-missing");
            }

            var candidates = new List<ProjectCandidate>();
            VisitDirectory(ProjectsRoot, currentPath, candidates);

            // 読み取り中の辞書は変更せず、並行する一覧要求にも完成した対応表だけを公開する。
            Volatile.Write(ref _listedLocations, candidates.ToDictionary(project => project.Id, project => project.Location));
            return new(null, candidates.OrderBy(project => project.Location, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (ProjectAccessException ex)
        {
            return FailedListing(ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FailedListing("projects-list-failed");
        }
    }

    /// <summary>一覧取得失敗時に以前の候補を無効にする。</summary>
    /// <param name="code">一覧取得に失敗した理由の翻訳キー。</param>
    /// <returns>エラー理由と空の候補一覧。</returns>
    private ProjectListing FailedListing(string code)
    {
        Volatile.Write(ref _listedLocations, new());
        return new(code, []);
    }

    /// <summary>リンクを辿らずにディレクトリを探索し、見つかった候補を追加する。</summary>
    /// <param name="directory">今回列挙するディレクトリ。</param>
    /// <param name="currentPath">現在開いている .crec ファイルのパス。</param>
    /// <param name="candidates">探索結果を追加する一覧。</param>
    /// <returns>なし。</returns>
    private void VisitDirectory(string directory, string currentPath, List<ProjectCandidate> candidates)
    {
        EnsureSafePath(directory);
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entryPath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);

            if (isDirectory && !isLink)
            {
                VisitChildDirectory(entryPath, currentPath, candidates);
                continue;
            }

            if (!isDirectory && !entryPath.EndsWith(".crec", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // リンクのディレクトリも選択不可の候補として表示し、辿れない理由を伝える。
            candidates.Add(CreateCandidate(entryPath, currentPath, isLink));
        }
    }

    /// <summary>子ディレクトリを探索し、アクセスできない場合は理由付きの項目を追加する。</summary>
    /// <param name="directory">探索対象の子ディレクトリ。</param>
    /// <param name="currentPath">現在開いている .crec ファイルのパス。</param>
    /// <param name="candidates">探索結果を追加する一覧。</param>
    /// <returns>なし。</returns>
    private void VisitChildDirectory(string directory, string currentPath, List<ProjectCandidate> candidates)
    {
        try
        {
            VisitDirectory(directory, currentPath, candidates);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 一部のフォルダを読めなくても、他の候補の探索は継続する。
            var location = Path.GetRelativePath(ProjectsRoot, directory).Replace('\\', '/');
            candidates.Add(new(GetId(location), Path.GetFileName(directory), location,
                false, "projects-data-unavailable"));
        }
    }

    /// <summary>候補の表示名と選択可否を検証結果から決定する。</summary>
    /// <param name="entryPath">候補となるファイルまたはリンクのパス。</param>
    /// <param name="currentPath">現在開いている .crec ファイルのパス。</param>
    /// <param name="isLink">探索時点でリンクと判定されたかどうか。</param>
    /// <returns>表示名、相対パス、現在の選択状態、エラー理由を含む候補。</returns>
    private ProjectCandidate CreateCandidate(string entryPath, string currentPath, bool isLink)
    {
        var location = Path.GetRelativePath(ProjectsRoot, entryPath).Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(entryPath);
        var isCurrent = Path.GetFullPath(entryPath).Equals(Path.GetFullPath(currentPath), PathComparison);
        if (isLink)
        {
            return new(GetId(location), name, location, isCurrent, "projects-link");
        }

        try
        {
            name = Validate(entryPath).Settings.ProjectName;
            return new(GetId(location), name, location, isCurrent, null);
        }
        catch (ProjectAccessException ex)
        {
            return new(GetId(location), name, location, isCurrent, ex.Code);
        }
    }

    /// <summary>一覧で発行した識別子から選択先を特定し、その候補だけを再検証する。</summary>
    /// <param name="id">候補一覧で発行した識別子。ファイルパスは受け付けない。</param>
    /// <returns>切り替え直前の検証を通過したファイルと設定。</returns>
    /// <exception cref="ProjectAccessException">識別子が一覧外、または候補を読み込めない場合。</exception>
    public ValidatedProject Resolve(string id)
    {
        if (!Volatile.Read(ref _listedLocations).TryGetValue(id, out var location))
        {
            throw new ProjectAccessException("projects-not-found");
        }

        return Validate(Path.Combine(ProjectsRoot, location));
    }

    /// <summary>プロジェクトファイルの配置・形式と、実データのアクセス可否を検証する。</summary>
    /// <param name="filePath">読み込む .crec ファイルのパス。</param>
    /// <returns>絶対パスへ解決したファイルと設定。ファイル内容は変更しない。</returns>
    /// <exception cref="ProjectAccessException">配置・形式・リンク・アクセス可否の検証に失敗した場合。</exception>
    public ValidatedProject Validate(string filePath)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);
            EnsureSafePath(filePath);
            if (!filePath.EndsWith(".crec", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
            {
                throw new ProjectAccessException("projects-not-found");
            }

            var settings = ProjectSettingsService.ReadValidatedSettings(filePath);

            // 実データは Projects 外も許可する。相対パスの基準は既存の起動処理と同じ作業ディレクトリ。
            settings.ProjectDataPath = Path.GetFullPath(settings.ProjectDataPath);
            EnsureNoLinks(settings.ProjectDataPath);
            if (!Directory.Exists(settings.ProjectDataPath))
            {
                throw new ProjectAccessException("projects-data-unavailable");
            }

            ValidateDataTree(settings.ProjectDataPath);
            return new(filePath, settings);
        }
        catch (ProjectAccessException)
        {
            throw;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new ProjectAccessException("projects-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectAccessException("projects-data-unavailable");
        }
    }

    /// <summary>実データ配下を列挙し、アクセスできない項目やリンクを検出する。</summary>
    /// <param name="directory">検証対象の実データディレクトリ。</param>
    /// <returns>なし。</returns>
    private static void ValidateDataTree(string directory)
    {
        // 既存のファイル API が子孫を参照するため、データ内から別の場所へ辿るリンクも拒否する。
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ProjectAccessException("projects-link");
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                ValidateDataTree(entryPath);
            }
        }
    }

    /// <summary>.crec の探索経路が Projects 内に収まり、リンクを含まないことを確認する。</summary>
    /// <param name="path">探索元、候補ディレクトリ、または .crec のパス。</param>
    /// <returns>なし。</returns>
    /// <exception cref="ProjectAccessException">Projects の外部、またはリンクを通る場合。</exception>
    public void EnsureSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(ProjectsRoot, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new ProjectAccessException("projects-outside-root");
        }

        EnsureNoLinks(fullPath);
    }

    /// <summary>対象とその親ディレクトリを調べ、リンク経由の参照を拒否する。</summary>
    /// <param name="fullPath">検証する対象の絶対パス。</param>
    /// <returns>なし。存在しない対象の判定は呼び出し元で行う。</returns>
    private static void EnsureNoLinks(string fullPath)
    {
        for (var ancestorPath = fullPath; ancestorPath is not null; ancestorPath = Path.GetDirectoryName(ancestorPath))
        {
            try
            {
                if (File.GetAttributes(ancestorPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ProjectAccessException("projects-link");
                }
            }
            catch (FileNotFoundException)
            {
                // 存在判定の前でも、存在している親まで確認を続ける。
            }
            catch (DirectoryNotFoundException)
            {
                // 対象が未作成でも、親ディレクトリのリンクは見逃さない。
            }
        }
    }

    /// <summary>候補の相対パスから安定した選択用識別子を生成する。</summary>
    /// <param name="location">Projects からの相対パス。</param>
    /// <returns>相対パスの SHA-256 を16進数で表した識別子。認証用トークンではない。</returns>
    private static string GetId(string location)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location)));
    }
}
