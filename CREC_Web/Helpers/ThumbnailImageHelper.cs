/*
CREC Web - Thumbnail Image Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace CREC_Web.Helpers
{
    public static class ThumbnailImageHelper
    {
        private const int MaxWidth = 1920;
        private const int MaxHeight = 1080;

        public static async Task ConvertToPngWithHdResizeAsync(string sourcePath, string destinationPngPath)
        {
            await using var sourceStream = System.IO.File.OpenRead(sourcePath);
            using var image = await Image.LoadAsync(sourceStream);

            if (image.Width > MaxWidth || image.Height > MaxHeight)
            {
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(MaxWidth, MaxHeight),
                    Mode = ResizeMode.Max
                }));
            }

            await image.SaveAsPngAsync(destinationPngPath, new PngEncoder
            {
                CompressionLevel = PngCompressionLevel.BestCompression
            });
        }
    }
}
