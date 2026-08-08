/*
CREC Web - ThreeDData Format Helpers
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    /// <summary>
    /// サポートする3Dファイル形式の定義と関連ヘルパー
    /// </summary>
    public static class ThreeDDataFormats
    {
        /// <summary>
        /// アップロードに対応する3D拡張子（小文字、ドット付き）
        /// </summary>
        public static readonly string[] AllowedExtensions = [".stl"];

        /// <summary>
        /// 拡張子から MIME タイプを返す。不明な場合は "application/octet-stream" を返す。
        /// </summary>
        public static string GetContentType(string? extension) => extension?.ToLowerInvariant() switch
        {
            ".stl" => "model/stl",
            _      => "application/octet-stream"
        };
    }
}
