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
    /// <summary>
    /// コレクションの data フォルダに対するファイル・フォルダ操作を提供します。
    /// </summary>
    public sealed class DataFileManagerService
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<DataFileManagerService> _logger;

        /// <summary>
        /// データファイル管理サービスを初期化します。
        /// </summary>
        /// <param name="configuration">プロジェクトデータフォルダなどのアプリケーション設定。</param>
        /// <param name="logger">ファイル操作の警告やエラーを記録するロガー。</param>
        public DataFileManagerService(IConfiguration configuration, ILogger<DataFileManagerService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// 指定した data フォルダ内のファイルとフォルダを一覧で取得します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="relativePath">data フォルダを基準とした対象フォルダの相対パス。ルートの場合は null または空文字。</param>
        /// <returns>現在の相対パスと、直下に存在する項目の一覧。</returns>
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

        /// <summary>
        /// 指定した親フォルダ内に新しいフォルダを作成します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="parentPath">data フォルダを基準とした親フォルダの相対パス。</param>
        /// <param name="name">作成するフォルダ名。</param>
        /// <returns>作成したフォルダの情報。</returns>
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

        /// <summary>
        /// ファイルまたはフォルダの名前を変更します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした変更対象の相対パス。</param>
        /// <param name="newName">変更後のファイル名またはフォルダ名。</param>
        /// <param name="confirmExtensionChange">ファイルの拡張子変更を利用者が確認済みの場合は true。</param>
        /// <returns>名前変更後の項目情報。</returns>
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

        /// <summary>
        /// ファイルまたはフォルダを削除します。フォルダの場合は配下の項目も再帰的に削除します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした削除対象の相対パス。</param>
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

        /// <summary>
        /// 指定したフォルダへファイルをアップロードします。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="directoryPath">data フォルダを基準としたアップロード先フォルダの相対パス。</param>
        /// <param name="file">アップロードするファイル。</param>
        /// <param name="cancellationToken">アップロード処理のキャンセルを通知するトークン。</param>
        /// <returns>アップロードしたファイルの情報。</returns>
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

        /// <summary>
        /// ダウンロード対象ファイルの実パスとダウンロード名を取得します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準としたファイルの相対パス。</param>
        /// <returns>検証済みの実パスとダウンロード時のファイル名。</returns>
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

        /// <summary>
        /// 指定したフォルダと配下の項目を含む一時ZIPアーカイブを作成します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="path">data フォルダを基準とした対象フォルダの相対パス。</param>
        /// <param name="cancellationToken">ZIP作成処理のキャンセルを通知するトークン。</param>
        /// <returns>読み取り後に自動削除されるZIPストリームとダウンロード名。</returns>
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

        /// <summary>
        /// コレクションの存在とパスの安全性を検証し、data フォルダの実パスを取得します。
        /// </summary>
        /// <param name="collectionId">対象コレクションのID。</param>
        /// <param name="createIfMissing">data フォルダが存在しない場合に作成するかどうか。</param>
        /// <returns>検証済みの data フォルダ実パス。</returns>
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

        /// <summary>
        /// 相対パスを検証し、区切り文字をスラッシュに統一します。
        /// </summary>
        /// <param name="path">検証対象の相対パス。</param>
        /// <param name="allowRoot">ルートを表す null または空文字を許可するかどうか。</param>
        /// <returns>正規化した相対パス。</returns>
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

        /// <summary>
        /// ファイル名またはフォルダ名として使用できる文字列か検証します。
        /// </summary>
        /// <param name="name">検証する項目名。</param>
        private static void ValidateEntryName(string name)
        {
            if (!ValidationHelper.IsValidFileSystemEntryName(name))
            {
                throw new DataFileManagerException(400, "Invalid file or folder name.");
            }
        }

        /// <summary>
        /// 正規化済み相対パスを実パスへ変換し、data フォルダ外を指していないことを検証します。
        /// </summary>
        /// <param name="dataRoot">基準となる data フォルダの実パス。</param>
        /// <param name="normalizedRelativePath">正規化済みの相対パス。</param>
        /// <returns>data フォルダ内であることを確認した実パス。</returns>
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

        /// <summary>
        /// フォルダが存在し、パス上にリパースポイントが含まれないことを検証します。
        /// </summary>
        /// <param name="dataRoot">基準となる data フォルダの実パス。</param>
        /// <param name="directoryPath">検証するフォルダの実パス。</param>
        private static void EnsureExistingDirectoryIsSafe(string dataRoot, string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                throw new DataFileManagerException(404, "Directory not found.");
            }

            EnsurePathHasNoReparsePoints(dataRoot, directoryPath);
        }

        /// <summary>
        /// ファイルまたはフォルダが存在し、パス上にリパースポイントが含まれないことを検証します。
        /// </summary>
        /// <param name="dataRoot">基準となる data フォルダの実パス。</param>
        /// <param name="entryPath">検証する項目の実パス。</param>
        private static void EnsureExistingEntryIsSafe(string dataRoot, string entryPath)
        {
            if (!File.Exists(entryPath) && !Directory.Exists(entryPath))
            {
                throw new DataFileManagerException(404, "File or folder not found.");
            }

            EnsurePathHasNoReparsePoints(dataRoot, entryPath);
        }

        /// <summary>
        /// data フォルダから対象までの各階層にリパースポイントがないことを検証します。
        /// </summary>
        /// <param name="dataRoot">基準となる data フォルダの実パス。</param>
        /// <param name="path">検証する対象の実パス。</param>
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

        /// <summary>
        /// 指定パスがシンボリックリンクなどのリパースポイントでないことを検証します。
        /// </summary>
        /// <param name="path">検証する実パス。</param>
        private static void EnsureNotReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DataFileManagerException(403, "Reparse points are not supported.");
            }
        }

        /// <summary>
        /// 指定パスが基準フォルダ自身またはその配下にあるか判定します。
        /// </summary>
        /// <param name="path">判定対象の実パス。</param>
        /// <param name="root">基準フォルダの実パス。</param>
        /// <returns>基準フォルダ内であれば true。</returns>
        private static bool IsPathWithinRoot(string path, string root)
        {
            var relativePath = Path.GetRelativePath(root, path);
            return relativePath == "." ||
                (!relativePath.Equals("..", StringComparison.Ordinal) &&
                 !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                 !Path.IsPathRooted(relativePath));
        }

        /// <summary>
        /// 親の相対パスと項目名をスラッシュ区切りで結合します。
        /// </summary>
        /// <param name="parentPath">親フォルダの相対パス。</param>
        /// <param name="name">結合するファイル名またはフォルダ名。</param>
        /// <returns>結合後の相対パス。</returns>
        private static string CombineRelativePath(string parentPath, string name) =>
            string.IsNullOrEmpty(parentPath) ? name : $"{parentPath}/{name}";

        /// <summary>
        /// 実パスを data フォルダ基準の相対パスへ変換します。
        /// </summary>
        /// <param name="dataRoot">基準となる data フォルダの実パス。</param>
        /// <param name="path">変換対象の実パス。</param>
        /// <returns>スラッシュ区切りの相対パス。</returns>
        private static string GetRelativePath(string dataRoot, string path)
        {
            var relativePath = Path.GetRelativePath(dataRoot, path);
            return relativePath == "." ? string.Empty : relativePath.Replace('\\', '/');
        }

        /// <summary>
        /// 作成・名前変更先に同名のファイルまたはフォルダが存在しないことを検証します。
        /// </summary>
        /// <param name="path">作成・名前変更先の実パス。</param>
        private static void EnsureDestinationDoesNotExist(string path)
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                throw new DataFileManagerException(409, "A file or folder with the same name already exists.");
            }
        }

        /// <summary>
        /// ファイル名の拡張子が大文字・小文字の違いを除いて変更されるか判定します。
        /// </summary>
        /// <param name="oldName">変更前のファイル名。</param>
        /// <param name="newName">変更後のファイル名。</param>
        /// <returns>拡張子が変更される場合は true。</returns>
        private static bool HasExtensionChanged(string oldName, string newName) =>
            !string.Equals(
                Path.GetExtension(oldName),
                Path.GetExtension(newName),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Windows上でも大文字・小文字だけの名前変更を反映できるよう、一時名を経由して変更します。
        /// </summary>
        /// <param name="sourcePath">変更前の実パス。</param>
        /// <param name="destinationPath">変更後の実パス。</param>
        /// <param name="isDirectory">変更対象がフォルダの場合は true。</param>
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

        /// <summary>
        /// ファイルシステム上の項目からAPIレスポンス用の項目情報を生成します。
        /// </summary>
        /// <param name="path">対象項目の実パス。</param>
        /// <param name="dataRoot">相対パスの基準となる data フォルダの実パス。</param>
        /// <returns>項目名、相対パス、種類、サイズ、更新日時を含む情報。</returns>
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

        /// <summary>
        /// リパースポイントのリンク先をたどらず、指定フォルダを配下から再帰的に削除します。
        /// </summary>
        /// <param name="directoryPath">削除するフォルダの実パス。</param>
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

        /// <summary>
        /// フォルダ配下の項目を再帰的にZIPアーカイブへ追加します。
        /// </summary>
        /// <param name="archive">追加先のZIPアーカイブ。</param>
        /// <param name="directoryPath">追加するフォルダの実パス。</param>
        /// <param name="archivePath">ZIP内で使用するフォルダパス。</param>
        /// <param name="cancellationToken">処理のキャンセルを通知するトークン。</param>
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

    /// <summary>
    /// データファイル操作でHTTPステータスとエラーコードを呼び出し元へ伝えるための例外です。
    /// </summary>
    public sealed class DataFileManagerException : Exception
    {
        public int StatusCode { get; }
        public string? ErrorCode { get; }

        /// <summary>
        /// データファイル操作例外を生成します。
        /// </summary>
        /// <param name="statusCode">APIレスポンスに使用するHTTPステータスコード。</param>
        /// <param name="message">利用者へ返すエラーメッセージ。</param>
        /// <param name="errorCode">フロントエンドで処理を分岐するための任意のエラーコード。</param>
        public DataFileManagerException(int statusCode, string message, string? errorCode = null)
            : base(message)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }
}
