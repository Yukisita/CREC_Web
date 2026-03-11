/*
CREC Web - Image Format Helpers
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    /// <summary>
    /// サポートする画像形式の定義と関連ヘルパー
    /// </summary>
    public static class ImageFormats
    {
        /// <summary>
        /// アップロード・サムネイルに対応する画像拡張子（小文字、ドット付き）
        /// </summary>
        public static readonly string[] AllowedExtensions =
            [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"];

        /// <summary>
        /// 拡張子から MIME タイプを返す。不明な場合は "application/octet-stream" を返す。
        /// </summary>
        public static string GetContentType(string? extension) => extension?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png"            => "image/png",
            ".gif"            => "image/gif",
            ".bmp"            => "image/bmp",
            ".webp"           => "image/webp",
            _                 => "application/octet-stream"
        };
    }
}
