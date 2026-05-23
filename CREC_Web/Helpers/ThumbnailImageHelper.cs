/*
CREC Web - Thumbnail Image Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using SkiaSharp;

namespace CREC_Web.Helpers
{
    public static class ThumbnailImageHelper
    {
        private const int MaxWidth = 1920;
        private const int MaxHeight = 1080;

        /// <summary>
        /// サムネイル画像への変換用ヘルパー
        /// </summary>
        /// <param name="sourcePath">元画像のパス</param>
        /// <param name="destinationPngPath">変換後のPNG画像のパス</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        public static async Task ConvertToPngWithHdResizeAsync(string sourcePath, string destinationPngPath)
        {
            await using var sourceStream = System.IO.File.OpenRead(sourcePath);
            using var managedStream = new SKManagedStream(sourceStream);
            using var codec = SKCodec.Create(managedStream) ?? throw new InvalidOperationException("Unsupported image format");
            using var sourceBitmap = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("Failed to decode image");

            // 元画像が指定された最大サイズを超える場合は、アスペクト比を維持できるリサイズを算出する
            if (sourceBitmap.Width <= 0 || sourceBitmap.Height <= 0)
            {
                throw new InvalidOperationException("Invalid image size");
            }
            var targetWidth = sourceBitmap.Width;
            var targetHeight = sourceBitmap.Height;
            if (sourceBitmap.Width > MaxWidth || sourceBitmap.Height > MaxHeight)
            {
                var scale = Math.Min((double)MaxWidth / sourceBitmap.Width, (double)MaxHeight / sourceBitmap.Height);
                targetWidth = Math.Max(1, (int)Math.Round(sourceBitmap.Width * scale));
                targetHeight = Math.Max(1, (int)Math.Round(sourceBitmap.Height * scale));
            }

            // リサイズが必要な場合はリサイズしてからPNGエンコードする。リサイズが不要な場合は元のビットマップをそのまま使用する。
            using var resizedBitmap = targetWidth == sourceBitmap.Width && targetHeight == sourceBitmap.Height
                ? null
                : sourceBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
                    ?? throw new InvalidOperationException("Failed to resize image");
            using var image = SKImage.FromBitmap(resizedBitmap ?? sourceBitmap)
                ?? throw new InvalidOperationException("Failed to create image from bitmap");

            // PNG形式でエンコードする
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Failed to encode PNG image");

            // エンコードされたPNGデータをファイルに保存する
            await using var destinationStream = System.IO.File.Create(destinationPngPath);
            encoded.SaveTo(destinationStream);
        }
    }
}
