using System.Text;
using CREC_Web.Helpers;
using CREC_Web.Models;
using Microsoft.AspNetCore.StaticFiles;

namespace CREC_Web.Services;

/// <summary>保存済み添付ファイルの読み取り。サムネイルの変換・保存やフォルダ作成は行わない。</summary>
public sealed class CollectionFileReader(IConfiguration configuration, DataFileManagerService dataFiles)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public DataDirectoryListing List(CollectionData collection, CollectionFileArea area, string? path)
    {
        var (root, relative) = Resolve(collection, area, path, allowRoot: true);
        if (area == CollectionFileArea.Data)
            return dataFiles.ListDirectory(Path.GetFileName(collection.CollectionFolderPath), relative);
        if (!Directory.Exists(root))
        {
            if (relative.Length > 0) throw new CrecReadException("directory-not-found");
            return new() { CurrentPath = relative };
        }
        var folder = Path.Combine(root, relative);
        if (!Directory.Exists(folder)) throw new CrecReadException("directory-not-found");
        var entries = new DirectoryInfo(folder).EnumerateFileSystemInfos()
            .Where(info => !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Where(info => area != CollectionFileArea.Thumbnail || info is FileInfo && IsThumbnail(info.Name))
            .Select(info => new DataFileEntry
            {
                Name = info.Name, RelativePath = Path.GetRelativePath(root, info.FullName).Replace('\\', '/'),
                EntryType = info is DirectoryInfo ? "directory" : "file",
                Size = info is FileInfo file ? file.Length : null, LastModifiedUtc = info.LastWriteTimeUtc
            }).OrderBy(entry => entry.EntryType == "directory" ? 0 : 1)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToList();
        return new() { CurrentPath = relative, Entries = entries };
    }

    public async Task<FileContentView> ReadAsync(CollectionData collection, CollectionFileArea area, string path,
        long offset, int maxBytes, FileContentEncoding encoding, string? version, CancellationToken token)
    {
        if (offset is < 0 or > 9_007_199_254_740_991 || maxBytes is < 4 or > 262144 || !Enum.IsDefined(encoding))
            throw new CrecReadException("invalid-file-range");
        if (offset > 0 && string.IsNullOrWhiteSpace(version)) throw new CrecReadException("file-version-required");
        var (root, relative) = Resolve(collection, area, path, allowRoot: false);
        var fullPath = area == CollectionFileArea.Data
            ? dataFiles.GetFileForDownload(Path.GetFileName(collection.CollectionFolderPath), relative).FullPath
            : Path.Combine(root, relative);
        if (!File.Exists(fullPath)) throw new CrecReadException("file-not-found");
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous);
        var total = stream.Length;
        var currentVersion = $"{total}:{File.GetLastWriteTimeUtc(fullPath).Ticks}";
        if (version is not null && version != currentVersion) throw new CrecReadException("file-changed");
        if (offset > total) throw new CrecReadException("invalid-file-range");
        stream.Position = offset;
        var buffer = new byte[(int)Math.Min(maxBytes, total - offset)];
        var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, token);
        if (!ContentTypes.TryGetContentType(relative, out var mediaType)) mediaType = "application/octet-stream";
        var asText = encoding == FileContentEncoding.Utf8 || (encoding == FileContentEncoding.Auto &&
            (mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType.Contains("json", StringComparison.Ordinal)
            || mediaType.Contains("xml", StringComparison.Ordinal) || new[] { ".md", ".yaml", ".yml", ".log", ".ini", ".obj", ".mtl", ".gltf" }
                .Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)));
        string content;
        if (asText)
        {
            // UTF-8の文字境界まで返し、次回も同じバイト位置から続けられるようにする。
            var originalCount = count;
            while (true)
            {
                try { content = Utf8.GetString(buffer, 0, count); break; }
                catch (DecoderFallbackException ex) when (offset + originalCount < total
                    && originalCount - count < 3 && ex.Index >= count - 3) { count--; }
                catch (DecoderFallbackException) { throw new CrecReadException("file-not-utf8-use-base64"); }
            }
            if (content.Contains('\0')) throw new CrecReadException("file-not-utf8-use-base64");
        }
        else content = Convert.ToBase64String(buffer, 0, count);
        token.ThrowIfCancellationRequested();
        if (currentVersion != $"{stream.Length}:{File.GetLastWriteTimeUtc(fullPath).Ticks}")
            throw new CrecReadException("file-changed");
        return new(collection.IndexData.SystemData.Id, area, relative, mediaType, currentVersion, total, offset,
            count, offset + count < total ? offset + count : null, asText ? "utf8" : "base64", content);
    }

    private (string Root, string Relative) Resolve(CollectionData collection, CollectionFileArea area, string? path, bool allowRoot)
    {
        if (!Enum.IsDefined(area) || path?.Length > 4096) throw new CrecReadException("invalid-file-path");
        var projectRoot = Path.GetFullPath(configuration["ProjectDataPath"] ?? throw new CrecReadException("projects-not-selected"));
        var collectionRoot = Path.GetFullPath(collection.CollectionFolderPath);
        var collectionName = Path.GetRelativePath(projectRoot, collectionRoot);
        if (!ValidationHelper.IsValidCollectionId(collectionName)) throw new CrecReadException("invalid-collection-id");
        var root = Path.Combine(collectionRoot, area switch
        {
            CollectionFileArea.Data => "data", CollectionFileArea.Pictures => "pictures",
            CollectionFileArea.Videos => "videos", CollectionFileArea.ThreeD => "3DData", _ => "SystemData"
        });
        var relative = (path ?? "").Replace('\\', '/');
        if ((!allowRoot && relative.Length == 0) || (relative.Length > 0 &&
            relative.Split('/').Any(segment => !ValidationHelper.IsValidFileSystemEntryName(segment))))
            throw new CrecReadException("invalid-file-path");
        if (area == CollectionFileArea.Thumbnail && relative.Length > 0 && !IsThumbnail(relative))
            throw new CrecReadException("invalid-file-path");
        // 添付ルートだけでなく、プロジェクトまでの祖先もリンクを経由させない。
        for (var current = Path.Combine(root, relative); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new CrecReadException("file-link-denied");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return (root, relative);
    }

    private static bool IsThumbnail(string name) =>
        Path.GetFileNameWithoutExtension(name).Equals("Thumbnail", StringComparison.OrdinalIgnoreCase)
        && ImageFormats.AllowedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
        && !name.Contains('/');
}
