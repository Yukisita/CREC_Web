/*
CREC Web - Data File Manager Service
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.IO.Compression;
using CREC_Web.Helpers;
using CREC_Web.Models;

namespace CREC_Web.Services
{
    public sealed class DataFileManagerService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DataFileManagerService> _logger;

        public DataFileManagerService(IConfiguration configuration, ILogger<DataFileManagerService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public DataDirectoryListing ListDirectory(string collectionId, string? relativePath)
        {
            var dataRoot = GetDataRoot(collectionId, createIfMissing: false);
            var normalizedPath = NormalizeRelativePath(relativePath, allowRoot: true);

            if (!Directory.Exists(dataRoot))
            {
                if (normalizedPath.Length > 0)
                {
                    throw new DataFileManagerException(404, "Directory not found.");
                }

                return new DataDirectoryListing { CurrentPath = normalizedPath };
            }

            var directoryPath = ResolvePath(dataRoot, normalizedPath);
            EnsureExistingDirectoryIsSafe(dataRoot, directoryPath);

            var entries = new List<DataFileEntry>();
            foreach (var entryPath in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogWarning("Skipped reparse point in data directory: {EntryName}", Path.GetFileName(entryPath));
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetFileName(entryPath);
                var entryRelativePath = CombineRelativePath(normalizedPath, name);
                entries.Add(new DataFileEntry
                {
                    Name = name,
                    RelativePath = entryRelativePath,
                    EntryType = isDirectory ? "directory" : "file",
                    Size = isDirectory ? null : new FileInfo(entryPath).Length,
                    LastModifiedUtc = isDirectory
                        ? new DirectoryInfo(entryPath).LastWriteTimeUtc
                        : new FileInfo(entryPath).LastWriteTimeUtc
                });
            }

            entries = entries
                .OrderBy(entry => entry.EntryType == "directory" ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.Name, StringComparer.Ordinal)
                .ToList();

            return new DataDirectoryListing
            {
                CurrentPath = normalizedPath,
                Entries = entries
            };
        }

        public DataFileEntry CreateDirectory(string collectionId, string? parentPath, string name)
        {
            ValidateEntryName(name);
            var dataRoot = GetDataRoot(collectionId, createIfMissing: true);
            var normalizedParentPath = NormalizeRelativePath(parentPath, allowRoot: true);
            var resolvedParentPath = ResolvePath(dataRoot, normalizedParentPath);
            EnsureExistingDirectoryIsSafe(dataRoot, resolvedParentPath);

            var directoryPath = ResolvePath(dataRoot, CombineRelativePath(normalizedParentPath, name));
            EnsureDestinationDoesNotExist(directoryPath);
            Directory.CreateDirectory(directoryPath);

            return CreateEntry(directoryPath, dataRoot);
        }

        public DataFileEntry RenameEntry(
            string collectionId,
            string path,
            string newName,
            bool confirmExtensionChange)
        {
            ValidateEntryName(newName);
            var dataRoot = GetDataRoot(collectionId, createIfMissing: false);
            var normalizedPath = NormalizeRelativePath(path, allowRoot: false);
            var sourcePath = ResolvePath(dataRoot, normalizedPath);
            EnsureExistingEntryIsSafe(dataRoot, sourcePath);

            var isDirectory = Directory.Exists(sourcePath);
            if (!isDirectory && HasExtensionChanged(Path.GetFileName(sourcePath), newName) && !confirmExtensionChange)
            {
                throw new DataFileManagerException(
                    409,
                    "Changing the file extension requires confirmation.",
                    "extension_change_confirmation_required");
            }

            var parentPath = Path.GetDirectoryName(sourcePath)
                ?? throw new DataFileManagerException(400, "Invalid entry path.");
            var destinationPath = ResolvePath(dataRoot, CombineRelativePath(
                GetRelativePath(dataRoot, parentPath), newName));

            if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            {
                return CreateEntry(sourcePath, dataRoot);
            }

            var isCaseOnlyRename =
                string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(sourcePath, destinationPath, StringComparison.Ordinal);
            if (!isCaseOnlyRename)
            {
                EnsureDestinationDoesNotExist(destinationPath);
            }

            if (isCaseOnlyRename)
            {
                RenameEntryCaseOnly(sourcePath, destinationPath, isDirectory);
            }
            else if (isDirectory)
            {
                Directory.Move(sourcePath, destinationPath);
            }
            else
            {
                File.Move(sourcePath, destinationPath);
            }

            return CreateEntry(destinationPath, dataRoot);
        }

        public void DeleteEntry(string collectionId, string path)
        {
            var dataRoot = GetDataRoot(collectionId, createIfMissing: false);
            var normalizedPath = NormalizeRelativePath(path, allowRoot: false);
            var entryPath = ResolvePath(dataRoot, normalizedPath);
            EnsureExistingEntryIsSafe(dataRoot, entryPath);

            if (Directory.Exists(entryPath))
            {
                DeleteDirectoryWithoutFollowingReparsePoints(entryPath);
            }
            else
            {
                File.Delete(entryPath);
            }
        }

        public async Task<DataFileEntry> UploadFileAsync(
            string collectionId,
            string? directoryPath,
            IFormFile file,
            CancellationToken cancellationToken)
        {
            if (file is null || file.Length == 0)
            {
                throw new DataFileManagerException(400, "A non-empty file is required.");
            }

            var fileName = Path.GetFileName(file.FileName);
            ValidateEntryName(fileName);
            var dataRoot = GetDataRoot(collectionId, createIfMissing: true);
            var normalizedDirectoryPath = NormalizeRelativePath(directoryPath, allowRoot: true);
            var resolvedDirectoryPath = ResolvePath(dataRoot, normalizedDirectoryPath);
            EnsureExistingDirectoryIsSafe(dataRoot, resolvedDirectoryPath);

            var destinationPath = ResolvePath(dataRoot, CombineRelativePath(normalizedDirectoryPath, fileName));
            EnsureDestinationDoesNotExist(destinationPath);

            try
            {
                await using var stream = new FileStream(
                    destinationPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                await file.CopyToAsync(stream, cancellationToken);
            }
            catch
            {
                if (File.Exists(destinationPath))
                {
                    try
                    {
                        File.Delete(destinationPath);
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(cleanupException, "Failed to remove an incomplete data upload.");
                    }
                }

                throw;
            }

            return CreateEntry(destinationPath, dataRoot);
        }

        public (string FullPath, string DownloadName) GetFileForDownload(string collectionId, string path)
        {
            var dataRoot = GetDataRoot(collectionId, createIfMissing: false);
            var normalizedPath = NormalizeRelativePath(path, allowRoot: false);
            var filePath = ResolvePath(dataRoot, normalizedPath);
            EnsureExistingEntryIsSafe(dataRoot, filePath);

            if (!File.Exists(filePath))
            {
                throw new DataFileManagerException(404, "File not found.");
            }

            return (filePath, Path.GetFileName(filePath));
        }

        public async Task<(FileStream Stream, string DownloadName)> CreateDirectoryArchiveAsync(
            string collectionId,
            string path,
            CancellationToken cancellationToken)
        {
            var dataRoot = GetDataRoot(collectionId, createIfMissing: false);
            var normalizedPath = NormalizeRelativePath(path, allowRoot: false);
            var directoryPath = ResolvePath(dataRoot, normalizedPath);
            EnsureExistingDirectoryIsSafe(dataRoot, directoryPath);

            var tempArchivePath = Path.Combine(Path.GetTempPath(), $"crec-data-{Guid.NewGuid():N}.zip");
            try
            {
                await using (var output = new FileStream(
                    tempArchivePath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true))
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: false))
                {
                    await AddDirectoryToArchiveAsync(
                        archive,
                        directoryPath,
                        Path.GetFileName(directoryPath),
                        cancellationToken);
                }

                var stream = new FileStream(
                    tempArchivePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    options: FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                return (stream, $"{Path.GetFileName(directoryPath)}.zip");
            }
            catch
            {
                if (File.Exists(tempArchivePath))
                {
                    try
                    {
                        File.Delete(tempArchivePath);
                    }
                    catch (Exception cleanupException)
                    {
                        _logger.LogWarning(cleanupException, "Failed to remove a temporary data archive.");
                    }
                }

                throw;
            }
        }

        private string GetDataRoot(string collectionId, bool createIfMissing)
        {
            if (!ValidationHelper.IsValidCollectionId(collectionId))
            {
                throw new DataFileManagerException(400, "Invalid collection ID.");
            }

            var configuredRoot = _configuration["ProjectDataPath"] ?? Directory.GetCurrentDirectory();
            var projectRoot = Path.GetFullPath(configuredRoot);
            var collectionRoot = Path.GetFullPath(Path.Combine(projectRoot, collectionId));
            if (!IsPathWithinRoot(collectionRoot, projectRoot) || !Directory.Exists(collectionRoot))
            {
                throw new DataFileManagerException(404, "Collection not found.");
            }

            EnsureNotReparsePoint(collectionRoot);
            var dataRoot = Path.GetFullPath(Path.Combine(collectionRoot, "data"));
            if (!IsPathWithinRoot(dataRoot, collectionRoot))
            {
                throw new DataFileManagerException(400, "Invalid data path.");
            }

            if (Directory.Exists(dataRoot))
            {
                EnsureNotReparsePoint(dataRoot);
            }
            else if (createIfMissing)
            {
                Directory.CreateDirectory(dataRoot);
            }

            return dataRoot;
        }

        private static string NormalizeRelativePath(string? path, bool allowRoot)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                if (allowRoot)
                {
                    return string.Empty;
                }

                throw new DataFileManagerException(400, "A path is required.");
            }

            if (path.Length > 4096 || Path.IsPathRooted(path) || path.Contains('\0'))
            {
                throw new DataFileManagerException(400, "Invalid relative path.");
            }

            var normalized = path.Replace('\\', '/').Trim('/');
            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                if (allowRoot)
                {
                    return string.Empty;
                }

                throw new DataFileManagerException(400, "A path is required.");
            }

            foreach (var segment in segments)
            {
                if (!ValidationHelper.IsValidFileSystemEntryName(segment))
                {
                    throw new DataFileManagerException(400, "Invalid relative path.");
                }
            }

            return string.Join('/', segments);
        }

        private static void ValidateEntryName(string name)
        {
            if (!ValidationHelper.IsValidFileSystemEntryName(name))
            {
                throw new DataFileManagerException(400, "Invalid file or folder name.");
            }
        }

        private static string ResolvePath(string dataRoot, string normalizedRelativePath)
        {
            var relativeOsPath = normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar);
            var resolvedPath = Path.GetFullPath(Path.Combine(dataRoot, relativeOsPath));
            if (!IsPathWithinRoot(resolvedPath, dataRoot))
            {
                throw new DataFileManagerException(400, "The path is outside the data directory.");
            }

            return resolvedPath;
        }

        private static void EnsureExistingDirectoryIsSafe(string dataRoot, string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw new DataFileManagerException(404, "Directory not found.");
            }

            EnsurePathHasNoReparsePoints(dataRoot, directoryPath);
        }

        private static void EnsureExistingEntryIsSafe(string dataRoot, string entryPath)
        {
            if (!File.Exists(entryPath) && !Directory.Exists(entryPath))
            {
                throw new DataFileManagerException(404, "File or folder not found.");
            }

            EnsurePathHasNoReparsePoints(dataRoot, entryPath);
        }

        private static void EnsurePathHasNoReparsePoints(string dataRoot, string path)
        {
            EnsureNotReparsePoint(dataRoot);
            var relativePath = Path.GetRelativePath(dataRoot, path);
            var currentPath = dataRoot;
            foreach (var segment in relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if (File.Exists(currentPath) || Directory.Exists(currentPath))
                {
                    EnsureNotReparsePoint(currentPath);
                }
            }
        }

        private static void EnsureNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DataFileManagerException(403, "Reparse points are not supported.");
            }
        }

        private static bool IsPathWithinRoot(string path, string root)
        {
            var relativePath = Path.GetRelativePath(root, path);
            return relativePath == "." ||
                (!relativePath.Equals("..", StringComparison.Ordinal) &&
                 !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !Path.IsPathRooted(relativePath));
        }

        private static string CombineRelativePath(string parentPath, string name) =>
            string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

        private static string GetRelativePath(string dataRoot, string path)
        {
            var relativePath = Path.GetRelativePath(dataRoot, path);
            return relativePath == "." ? string.Empty : relativePath.Replace('\\', '/');
        }

        private static void EnsureDestinationDoesNotExist(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new DataFileManagerException(409, "A file or folder with the same name already exists.");
            }
        }

        private static bool HasExtensionChanged(string oldName, string newName) =>
            !string.Equals(
                Path.GetExtension(oldName),
                Path.GetExtension(newName),
                StringComparison.OrdinalIgnoreCase);

        private static void RenameEntryCaseOnly(string sourcePath, string destinationPath, bool isDirectory)
        {
            var parentPath = Path.GetDirectoryName(sourcePath)
                ?? throw new DataFileManagerException(400, "Invalid entry path.");
            var temporaryPath = Path.Combine(parentPath, $".crec-rename-{Guid.NewGuid():N}");

            if (isDirectory)
            {
                Directory.Move(sourcePath, temporaryPath);
                try
                {
                    Directory.Move(temporaryPath, destinationPath);
                }
                catch
                {
                    if (Directory.Exists(temporaryPath) && !Directory.Exists(sourcePath))
                    {
                        Directory.Move(temporaryPath, sourcePath);
                    }
                    throw;
                }
                return;
            }

            File.Move(sourcePath, temporaryPath);
            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch
            {
                if (File.Exists(temporaryPath) && !File.Exists(sourcePath))
                {
                    File.Move(temporaryPath, sourcePath);
                }
                throw;
            }
        }

        private static DataFileEntry CreateEntry(string path, string dataRoot)
        {
            var isDirectory = Directory.Exists(path);
            var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
            return new DataFileEntry
            {
                Name = info.Name,
                RelativePath = GetRelativePath(dataRoot, path),
                EntryType = isDirectory ? "directory" : "file",
                Size = isDirectory ? null : ((FileInfo)info).Length,
                LastModifiedUtc = info.LastWriteTimeUtc
            };
        }

        private static void DeleteDirectoryWithoutFollowingReparsePoints(string directoryPath)
        {
            foreach (var entry in new DirectoryInfo(directoryPath).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(entry.FullName, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry.FullName);
                    }

                    continue;
                }

                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    DeleteDirectoryWithoutFollowingReparsePoints(entry.FullName);
                }
                else
                {
                    File.Delete(entry.FullName);
                }
            }

            Directory.Delete(directoryPath, recursive: false);
        }

        private static async Task AddDirectoryToArchiveAsync(
            ZipArchive archive,
            string directoryPath,
            string archivePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new DirectoryInfo(directoryPath).EnumerateFileSystemInfos().ToList();
            if (entries.Count == 0)
            {
                archive.CreateEntry($"{archivePath.TrimEnd('/')}/");
                return;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                var childArchivePath = $"{archivePath.TrimEnd('/')}/{entry.Name}";
                if ((entry.Attributes & FileAttributes.Directory) != 0)
                {
                    await AddDirectoryToArchiveAsync(
                        archive,
                        entry.FullName,
                        childArchivePath,
                        cancellationToken);
                    continue;
                }

                var archiveEntry = archive.CreateEntry(childArchivePath, CompressionLevel.Fastest);
                await using var input = new FileStream(
                    entry.FullName,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 81920,
                    useAsync: true);
                await using var output = archiveEntry.Open();
                await input.CopyToAsync(output, cancellationToken);
            }
        }
    }

    public sealed class DataFileManagerException : Exception
    {
        public int StatusCode { get; }
        public string? ErrorCode { get; }

        public DataFileManagerException(int statusCode, string message, string? errorCode = null)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }
}
