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

        public static async Task ConvertToPngWithHdResizeAsync(string sourcePath, string destinationPngPath)
        {
            await using var sourceStream = System.IO.File.OpenRead(sourcePath);
            using var managedStream = new SKManagedStream(sourceStream);
            using var codec = SKCodec.Create(managedStream) ?? throw new InvalidOperationException("Unsupported image format");
            using var sourceBitmap = SKBitmap.Decode(codec) ?? throw new InvalidOperationException("Failed to decode image");

            var targetWidth = sourceBitmap.Width;
            var targetHeight = sourceBitmap.Height;
            if (sourceBitmap.Width > MaxWidth || sourceBitmap.Height > MaxHeight)
            {
                var scale = Math.Min((double)MaxWidth / sourceBitmap.Width, (double)MaxHeight / sourceBitmap.Height);
                targetWidth = Math.Max(1, (int)Math.Round(sourceBitmap.Width * scale));
                targetHeight = Math.Max(1, (int)Math.Round(sourceBitmap.Height * scale));
            }

            using var outputBitmap = targetWidth == sourceBitmap.Width && targetHeight == sourceBitmap.Height
                ? sourceBitmap.Copy()
                : sourceBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), SKSamplingOptions.Default)
                    ?? throw new InvalidOperationException("Failed to resize image");
            using var image = SKImage.FromBitmap(outputBitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded == null)
            {
                throw new InvalidOperationException("Failed to encode PNG image");
            }

            await using var destinationStream = System.IO.File.Create(destinationPngPath);
            encoded.SaveTo(destinationStream);
        }
    }
}
