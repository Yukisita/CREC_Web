using System.Security.Cryptography;
using System.Text;

namespace CREC_Web.Services;

/// <summary>候補一覧の項目。</summary>
/// <param name="Id">選択用識別子。</param>
/// <param name="Name">表示名。</param>
/// <param name="Location">Projects からの相対パス。</param>
/// <param name="IsCurrent">現在のプロジェクトかどうか。</param>
/// <param name="ErrorCode">選択不可の理由。選択可能なら null。</param>
public sealed record ProjectCandidate(string Id, string Name, string Location, bool IsCurrent, string? ErrorCode);

/// <summary>候補一覧の取得結果。</summary>
/// <param name="ErrorCode">取得失敗の理由。成功なら null。</param>
/// <param name="Projects">選択不可の項目を含む一覧。</param>
public sealed record ProjectListing(string? ErrorCode, IReadOnlyList<ProjectCandidate> Projects);

/// <summary>検証済みのプロジェクト。</summary>
/// <param name="FilePath">.crec の絶対パス。</param>
/// <param name="Settings">実データのパスを解決済みの設定。</param>
public sealed record ValidatedProject(string FilePath, ProjectSettings Settings);

/// <summary>プロジェクトを選択できない理由。</summary>
/// <param name="code">理由を表す翻訳キー。</param>
public sealed class ProjectAccessException(string code) : Exception(code)
{
    /// <summary>理由の翻訳キー。</summary>
    public string Code { get; } = code;
}

/// <summary>Projects 内の候補を探索・検証する。</summary>
public sealed class ProjectCatalogService
{
    private Dictionary<string, string> _listedLocations = new();// 一覧で発行した識別子とパスの対応。

    /// <summary>.crec の探索元。実データの保存先は限定しない。</summary>
    public string ProjectsRoot { get; }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Web 実行ファイルの隣の Projects を探索元にする。</summary>
    public ProjectCatalogService() : this(Path.Combine(AppContext.BaseDirectory, "Projects")) { }

    /// <summary>探索元を指定する。</summary>
    /// <param name="projectsRoot">.crec の配置フォルダ。</param>
    public ProjectCatalogService(string projectsRoot) => ProjectsRoot = Path.GetFullPath(projectsRoot);

    /// <summary>候補を検証し、一覧と識別子を更新する。</summary>
    /// <param name="currentPath">現在の .crec のパス。</param>
    /// <returns>保存場所順の一覧。失敗時は理由と空の一覧。</returns>
    public ProjectListing List(string currentPath)
    {
        try
        {
            EnsureSafePath(ProjectsRoot);
            if (!Directory.Exists(ProjectsRoot))
                return FailedListing("projects-missing");

            var candidates = new List<ProjectCandidate>();
            VisitDirectory(ProjectsRoot, currentPath, candidates);
            // 並行する要求へは完成した対応表だけを公開する。
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

    /// <summary>一覧取得失敗時に、以前の候補を無効にする。</summary>
    /// <param name="code">失敗理由の翻訳キー。</param>
    /// <returns>理由と空の一覧。</returns>
    private ProjectListing FailedListing(string code)
    {
        Volatile.Write(ref _listedLocations, new());
        return new(code, []);
    }

    /// <summary>リンクを辿らず、配下の候補を列挙する。</summary>
    /// <param name="directory">探索対象。</param>
    /// <param name="currentPath">現在の .crec のパス。</param>
    /// <param name="candidates">結果の追加先。</param>
    /// <returns>なし。</returns>
    private void VisitDirectory(string directory, string currentPath, List<ProjectCandidate> candidates)
    {
        try
        {
            EnsureSafePath(directory);
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entryPath);
                var isDirectory = attributes.HasFlag(FileAttributes.Directory);
                var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isDirectory && !isLink)
                    VisitDirectory(entryPath, currentPath, candidates);
                else if (isDirectory || entryPath.EndsWith(".crec", StringComparison.OrdinalIgnoreCase))
                    candidates.Add(CreateCandidate(entryPath, currentPath, isLink));
            }
        }
        catch (Exception ex) when (directory != ProjectsRoot && (ex is IOException or UnauthorizedAccessException))
        {
            // 子フォルダの失敗は項目として残す。探索元の失敗は List で扱う。
            var location = Path.GetRelativePath(ProjectsRoot, directory).Replace('\\', '/');
            candidates.Add(new(GetId(location), Path.GetFileName(directory), location, false, "projects-data-unavailable"));
        }
    }

    /// <summary>候補の表示名と選択可否を調べる。</summary>
    /// <param name="entryPath">候補のパス。</param>
    /// <param name="currentPath">現在の .crec のパス。</param>
    /// <param name="isLink">探索時にリンクと判定されたか。</param>
    /// <returns>検証結果を含む候補。</returns>
    private ProjectCandidate CreateCandidate(string entryPath, string currentPath, bool isLink)
    {
        var location = Path.GetRelativePath(ProjectsRoot, entryPath).Replace('\\', '/');
        var name = Path.GetFileNameWithoutExtension(entryPath);
        var isCurrent = Path.GetFullPath(entryPath).Equals(Path.GetFullPath(currentPath), PathComparison);
        var errorCode = isLink ? "projects-link" : null;
        try
        {
            if (!isLink)
                name = Validate(entryPath).Settings.ProjectName;
        }
        catch (ProjectAccessException ex)
        {
            errorCode = ex.Code;
        }
        return new(GetId(location), name, location, isCurrent, errorCode);
    }

    /// <summary>選択された候補だけを再検証する。</summary>
    /// <param name="id">一覧で発行した識別子。</param>
    /// <returns>検証済みプロジェクト。選択不可なら例外。</returns>
    public ValidatedProject Resolve(string id)
    {
        if (!Volatile.Read(ref _listedLocations).TryGetValue(id, out var location))
            throw new ProjectAccessException("projects-not-found");
        return Validate(Path.Combine(ProjectsRoot, location));
    }

    /// <summary>.crec と実データを検証する。</summary>
    /// <param name="filePath">.crec のパス。</param>
    /// <returns>絶対パスへ解決した設定。検証失敗なら例外。</returns>
    public ValidatedProject Validate(string filePath)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);
            EnsureSafePath(filePath);
            if (!filePath.EndsWith(".crec", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                throw new ProjectAccessException("projects-not-found");

            var settings = ProjectSettingsService.ReadValidatedSettings(filePath);
            // 実データは Projects 外も許可する。相対パスは従来どおり作業ディレクトリ基準。
            settings.ProjectDataPath = Path.GetFullPath(settings.ProjectDataPath);
            EnsureNoLinks(settings.ProjectDataPath);
            if (!Directory.Exists(settings.ProjectDataPath))
                throw new ProjectAccessException("projects-data-unavailable");

            ValidateDataTree(settings.ProjectDataPath);
            return new(filePath, settings);
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

    /// <summary>実データ配下のアクセス可否とリンクを調べる。</summary>
    /// <param name="directory">検証対象のフォルダ。</param>
    /// <returns>なし。アクセス不可・リンクありなら例外。</returns>
    private static void ValidateDataTree(string directory)
    {
        foreach (var entryPath in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entryPath);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ProjectAccessException("projects-link");
            if (attributes.HasFlag(FileAttributes.Directory))
                ValidateDataTree(entryPath);
        }
    }

    /// <summary>パスが Projects 内に収まり、リンクを通らないか確認する。</summary>
    /// <param name="path">探索対象のパス。</param>
    /// <returns>なし。範囲外・リンクありなら例外。</returns>
    public void EnsureSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(ProjectsRoot, fullPath);
        if (Path.IsPathRooted(relativePath) || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ProjectAccessException("projects-outside-root");
        EnsureNoLinks(fullPath);
    }

    /// <summary>対象と親ディレクトリのリンクを拒否する。</summary>
    /// <param name="fullPath">対象の絶対パス。</param>
    /// <returns>なし。リンクありなら例外。</returns>
    private static void EnsureNoLinks(string fullPath)
    {
        for (var ancestorPath = fullPath; ancestorPath is not null; ancestorPath = Path.GetDirectoryName(ancestorPath))
        {
            try
            {
                if (File.GetAttributes(ancestorPath).HasFlag(FileAttributes.ReparsePoint))
                    throw new ProjectAccessException("projects-link");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // 未作成の対象でも、存在する親まで確認を続ける。
            }
        }
    }

    /// <summary>相対パスから選択用識別子を作る。</summary>
    /// <param name="location">Projects からの相対パス。</param>
    /// <returns>SHA-256（16進数）。</returns>
    private static string GetId(string location) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location)));
}
