/*
CREC Web - Data File Manager Models
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Models
{
    /// <summary>
    /// data フォルダ内の一覧取得結果を表します。
    /// </summary>
    public sealed class DataDirectoryListing
    {
        /// <summary>data フォルダを基準とした現在の相対パス。</summary>
        public string CurrentPath { get; set; } = string.Empty;

        /// <summary>現在のフォルダ直下に存在するファイルとフォルダ。</summary>
        public List<DataFileEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// data フォルダ内のファイルまたはフォルダを表します。
    /// </summary>
    public sealed class DataFileEntry
    {
        /// <summary>ファイル名またはフォルダ名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>data フォルダを基準とした相対パス。</summary>
        public string RelativePath { get; set; } = string.Empty;

        /// <summary>項目種別。ファイルは file、フォルダは directory。</summary>
        public string EntryType { get; set; } = string.Empty;

        /// <summary>ファイルサイズ。フォルダの場合は null。</summary>
        public long? Size { get; set; }

        /// <summary>最終更新日時（UTC）。</summary>
        public DateTime LastModifiedUtc { get; set; }
    }

    /// <summary>
    /// フォルダ作成APIの入力値を表します。
    /// </summary>
    public sealed class CreateDataDirectoryRequest
    {
        /// <summary>data フォルダを基準とした作成先の相対パス。</summary>
        public string ParentPath { get; set; } = string.Empty;

        /// <summary>作成するフォルダ名。</summary>
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// ファイルまたはフォルダの名前変更APIの入力値を表します。
    /// </summary>
    public sealed class RenameDataEntryRequest
    {
        /// <summary>data フォルダを基準とした変更対象の相対パス。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>変更後のファイル名またはフォルダ名。</summary>
        public string NewName { get; set; } = string.Empty;

        /// <summary>ファイルの拡張子変更を利用者が確認済みの場合は true。</summary>
        public bool ConfirmExtensionChange { get; set; }
    }
}
