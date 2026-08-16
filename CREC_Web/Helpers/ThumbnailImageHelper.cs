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
            using var orientedBitmap = ApplyEncodedOrigin(sourceBitmap, codec.EncodedOrigin);
            var processingSourceBitmap = orientedBitmap ?? sourceBitmap;

            // 元画像が指定された最大サイズを超える場合は、アスペクト比を維持できるリサイズを算出する
            if (processingSourceBitmap.Width <= 0 || processingSourceBitmap.Height <= 0)
            {
                throw new InvalidOperationException("Invalid image size");
            }
            var targetWidth = processingSourceBitmap.Width;
            var targetHeight = processingSourceBitmap.Height;
            if (processingSourceBitmap.Width > MaxWidth || processingSourceBitmap.Height > MaxHeight)
            {
                var scale = Math.Min((double)MaxWidth / processingSourceBitmap.Width, (double)MaxHeight / processingSourceBitmap.Height);
                targetWidth = Math.Max(1, (int)Math.Round(processingSourceBitmap.Width * scale));
                targetHeight = Math.Max(1, (int)Math.Round(processingSourceBitmap.Height * scale));
            }

            // リサイズが必要な場合はリサイズしてからPNGエンコードする。リサイズが不要な場合は元のビットマップをそのまま使用する。
            using var resizedBitmap = targetWidth == processingSourceBitmap.Width && targetHeight == processingSourceBitmap.Height
                ? null
                : processingSourceBitmap.Resize(
                    new SKImageInfo(
                        targetWidth,
                        targetHeight,
                        processingSourceBitmap.ColorType,
                        processingSourceBitmap.AlphaType,
                        processingSourceBitmap.ColorSpace),
                    SKSamplingOptions.Default)
                    ?? throw new InvalidOperationException("Failed to resize image");
            using var image = SKImage.FromBitmap(resizedBitmap ?? processingSourceBitmap)
                ?? throw new InvalidOperationException("Failed to create image from bitmap");

            // PNG形式でエンコードする
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("Failed to encode PNG image");

            // エンコードされたPNGデータをファイルに保存する
            await using var destinationStream = System.IO.File.Create(destinationPngPath);
            encoded.SaveTo(destinationStream);
        }

        /// <summary>
        /// EXIF Orientation に相当する向きを画素へ適用する。
        /// </summary>
        /// <returns>補正が不要な場合は <see langword="null"/>。</returns>
        private static SKBitmap? ApplyEncodedOrigin(SKBitmap sourceBitmap, SKEncodedOrigin encodedOrigin)
        {
            if (encodedOrigin == SKEncodedOrigin.TopLeft)
            {
                return null;
            }

            var swapsDimensions = encodedOrigin is SKEncodedOrigin.LeftTop
                or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom
                or SKEncodedOrigin.LeftBottom;
            var orientedWidth = swapsDimensions ? sourceBitmap.Height : sourceBitmap.Width;
            var orientedHeight = swapsDimensions ? sourceBitmap.Width : sourceBitmap.Height;

            var orientedBitmap = new SKBitmap(new SKImageInfo(
                orientedWidth,
                orientedHeight,
                sourceBitmap.ColorType,
                sourceBitmap.AlphaType,
                sourceBitmap.ColorSpace));

            using var canvas = new SKCanvas(orientedBitmap);
            canvas.SetMatrix(CreateEncodedOriginMatrix(
                encodedOrigin,
                sourceBitmap.Width,
                sourceBitmap.Height));
            canvas.DrawBitmap(sourceBitmap, 0, 0);
            canvas.Flush();

            return orientedBitmap;
        }

        private static SKMatrix CreateEncodedOriginMatrix(
            SKEncodedOrigin encodedOrigin,
            int sourceWidth,
            int sourceHeight)
        {
            return encodedOrigin switch
            {
                SKEncodedOrigin.TopRight => CreateMatrix(-1, 0, sourceWidth, 0, 1, 0),
                SKEncodedOrigin.BottomRight => CreateMatrix(-1, 0, sourceWidth, 0, -1, sourceHeight),
                SKEncodedOrigin.BottomLeft => CreateMatrix(1, 0, 0, 0, -1, sourceHeight),
                SKEncodedOrigin.LeftTop => CreateMatrix(0, 1, 0, 1, 0, 0),
                SKEncodedOrigin.RightTop => CreateMatrix(0, -1, sourceHeight, 1, 0, 0),
                SKEncodedOrigin.RightBottom => CreateMatrix(0, -1, sourceHeight, -1, 0, sourceWidth),
                SKEncodedOrigin.LeftBottom => CreateMatrix(0, 1, 0, -1, 0, sourceWidth),
                _ => SKMatrix.Identity
            };
        }

        private static SKMatrix CreateMatrix(
            float scaleX,
            float skewX,
            float transX,
            float skewY,
            float scaleY,
            float transY)
        {
            return new SKMatrix
            {
                ScaleX = scaleX,
                SkewX = skewX,
                TransX = transX,
                SkewY = skewY,
                ScaleY = scaleY,
                TransY = transY,
                Persp0 = 0,
                Persp1 = 0,
                Persp2 = 1
            };
        }
    }
}
