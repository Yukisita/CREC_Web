using System.Security.Cryptography;
using System.Text;

namespace CREC_Web.Services;

public sealed record ProjectCandidate(string Id, string Name, string Location, bool IsCurrent, string? ErrorCode);
public sealed record ProjectListing(string? ErrorCode, IReadOnlyList<ProjectCandidate> Projects);
public sealed record ValidatedProject(string FilePath, ProjectSettings Settings);

public sealed class ProjectAccessException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>Lists server-owned project identifiers without accepting paths from clients.</summary>
public sealed class ProjectCatalogService
{
    public string ProjectsRoot { get; }
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public ProjectCatalogService() : this(Path.Combine(AppContext.BaseDirectory, "Projects")) { }

    public ProjectCatalogService(string projectsRoot) => ProjectsRoot = Path.GetFullPath(projectsRoot);

    public ProjectListing List(string currentPath)
    {
        try
        {
            EnsureSafePath(ProjectsRoot);
            if (!Directory.Exists(ProjectsRoot)) return new("projects-missing", []);
            var candidates = new List<ProjectCandidate>();
            Visit(ProjectsRoot, currentPath, candidates);
            return new(null, candidates.OrderBy(p => p.Location, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (ProjectAccessException ex) { return new(ex.Code, []); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new("projects-list-failed", []);
        }
    }

    private void Visit(string directory, string currentPath, List<ProjectCandidate> candidates)
    {
        EnsureSafePath(directory);
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var isLink = attributes.HasFlag(FileAttributes.ReparsePoint);
            if (isDirectory && !isLink)
            {
                Visit(entry, currentPath, candidates);
                continue;
            }
            if (!isDirectory && !entry.EndsWith(".crec", StringComparison.OrdinalIgnoreCase)) continue;
            var location = Path.GetRelativePath(ProjectsRoot, entry).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(entry);
            string? error = isLink ? "projects-link" : null;
            if (error is null)
            {
                try { name = Validate(entry).Settings.ProjectName; }
                catch (ProjectAccessException ex) { error = ex.Code; }
            }
            candidates.Add(new(GetId(location), name, location,
                Path.GetFullPath(entry).Equals(Path.GetFullPath(currentPath), PathComparison), error));
        }
    }

    public ValidatedProject Resolve(string id, string currentPath)
    {
        var listing = List(currentPath);
        if (listing.ErrorCode is not null) throw new ProjectAccessException(listing.ErrorCode);
        var candidate = listing.Projects.SingleOrDefault(p => p.Id == id)
            ?? throw new ProjectAccessException("projects-not-found");
        if (candidate.ErrorCode is not null) throw new ProjectAccessException(candidate.ErrorCode);
        // Reopen and validate after selection; the listing is never a validation cache.
        return Validate(Path.Combine(ProjectsRoot, candidate.Location));
    }

    public ValidatedProject Validate(string filePath)
    {
        try
        {
            filePath = Path.GetFullPath(filePath);
            EnsureSafePath(filePath);
            if (!filePath.EndsWith(".crec", StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                throw new ProjectAccessException("projects-not-found");
            var settings = ProjectSettingsService.ReadValidatedSettings(filePath);
            settings.ProjectDataPath = Path.GetFullPath(settings.ProjectDataPath, Path.GetDirectoryName(filePath)!);
            EnsureSafePath(settings.ProjectDataPath);
            if (!Directory.Exists(settings.ProjectDataPath)) throw new ProjectAccessException("projects-data-unavailable");
            ValidateDataTree(settings.ProjectDataPath);
            return new(filePath, settings);
        }
        catch (ProjectAccessException) { throw; }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new ProjectAccessException("projects-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectAccessException("projects-data-unavailable");
        }
    }

    private void ValidateDataTree(string directory)
    {
        // Existing collection/file endpoints may traverse descendants: do not admit linked data.
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ProjectAccessException("projects-link");
            if (attributes.HasFlag(FileAttributes.Directory)) ValidateDataTree(entry);
        }
    }

    public void EnsureSafePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(ProjectsRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ProjectAccessException("projects-outside-root");
        // Inspect all existing ancestors, including Projects itself, without following links.
        for (var current = fullPath; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new ProjectAccessException("projects-link");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static string GetId(string location) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(location)));
}
