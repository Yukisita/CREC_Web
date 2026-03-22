/*
CREC Web - Video Format Helpers
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    /// <summary>
    /// サポートする動画形式の定義と関連ヘルパー
    /// </summary>
    public static class VideoFormats
    {
        /// <summary>
        /// アップロードに対応する動画拡張子（小文字、ドット付き）
        /// </summary>
        public static readonly string[] AllowedExtensions =
            [".mp4", ".mov", ".avi", ".mkv", ".webm", ".wmv", ".flv", ".m4v"];

        /// <summary>
        /// 拡張子から MIME タイプを返す。不明な場合は "application/octet-stream" を返す。
        /// </summary>
        public static string GetContentType(string? extension) => extension?.ToLowerInvariant() switch
        {
            ".mp4"  => "video/mp4",
            ".mov"  => "video/quicktime",
            ".avi"  => "video/x-msvideo",
            ".mkv"  => "video/x-matroska",
            ".webm" => "video/webm",
            ".wmv"  => "video/x-ms-wmv",
            ".flv"  => "video/x-flv",
            ".m4v"  => "video/x-m4v",
            _       => "application/octet-stream"
        };
    }
}
