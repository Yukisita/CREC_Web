/*
CREC Web - WebP Converter Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using SkiaSharp;

namespace CREC_Web.Helpers
{
    /// <summary>
    /// SkiaSharp を使用した画像フォーマット変換ヘルパー
    /// </summary>
    public static class WebPConverter
    {
        private const int DefaultQuality = 90;

        /// <summary>
        /// 画像バイト列を WebP 形式に変換して返す。
        /// ICCプロファイルは Skia のデコード時に取り除かれるため、
        /// Android Chrome でのハードウェアカラーマネジメントによる画面点滅を防止できる。
        /// デコードに失敗した場合は null を返す。
        /// </summary>
        /// <param name="imageData">元画像のバイト列</param>
        /// <param name="quality">WebP 品質（0-100、デフォルト: 90）</param>
        /// <returns>WebP 形式のバイト列。変換失敗時は null。</returns>
        public static byte[]? ConvertToWebP(byte[] imageData, int quality = DefaultQuality)
        {
            using var bitmap = SKBitmap.Decode(imageData);
            if (bitmap == null)
                return null;

            using var image = SKImage.FromBitmap(bitmap);
            using var webpData = image.Encode(SKEncodedImageFormat.Webp, quality);
            return webpData?.ToArray();
        }
    }
}
