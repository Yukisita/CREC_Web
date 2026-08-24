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
        private const long MaxInputPixels = 200_000_000;
        private const int MaxInputDimension = 32_768;

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
            ValidateInputDimensions(codec.Info.Width, codec.Info.Height);

            // 向き補正後に最大サイズへ収まる縮小率を、デコード前の座標系で算出する。
            var swapsDimensions = SwapsDimensions(codec.EncodedOrigin);
            var decodeMaxWidth = swapsDimensions ? MaxHeight : MaxWidth;
            var decodeMaxHeight = swapsDimensions ? MaxWidth : MaxHeight;
            var decodeTargetSize = CalculateTargetSize(
                codec.Info.Width,
                codec.Info.Height,
                decodeMaxWidth,
                decodeMaxHeight);
            var desiredDecodeScale = Math.Min(
                (float)decodeTargetSize.Width / codec.Info.Width,
                (float)decodeTargetSize.Height / codec.Info.Height);
            var scaledDecodeSize = codec.GetScaledDimensions(desiredDecodeScale);
            if (scaledDecodeSize.Width <= 0 || scaledDecodeSize.Height <= 0)
            {
                throw new InvalidOperationException("Invalid scaled image size");
            }

            // コーデックが対応する縮小サイズで直接デコードし、フルサイズの画素バッファ確保を可能な限り避ける。
            var scaledDecodeInfo = new SKImageInfo(
                scaledDecodeSize.Width,
                scaledDecodeSize.Height,
                codec.Info.ColorType,
                codec.Info.AlphaType,
                codec.Info.ColorSpace);
            using var decodedBitmap = SKBitmap.Decode(codec, scaledDecodeInfo)
                ?? throw new InvalidOperationException("Failed to decode image");

            // 縮小デコードの粒度が粗い形式や、縮小デコード非対応の形式は、回転用バッファを作る前に縮小する。
            var preOrientationTargetSize = CalculateTargetSize(
                decodedBitmap.Width,
                decodedBitmap.Height,
                decodeMaxWidth,
                decodeMaxHeight);
            using var preOrientationResizedBitmap =
                preOrientationTargetSize.Width == decodedBitmap.Width && preOrientationTargetSize.Height == decodedBitmap.Height
                    ? null
                    : decodedBitmap.Resize(
                        new SKImageInfo(
                            preOrientationTargetSize.Width,
                            preOrientationTargetSize.Height,
                            decodedBitmap.ColorType,
                            decodedBitmap.AlphaType,
                            decodedBitmap.ColorSpace),
                        SKSamplingOptions.Default)
                        ?? throw new InvalidOperationException("Failed to resize image before orientation correction");
            var orientationSourceBitmap = preOrientationResizedBitmap ?? decodedBitmap;
            using var orientedBitmap = ApplyEncodedOrigin(orientationSourceBitmap, codec.EncodedOrigin);
            var processingSourceBitmap = orientedBitmap ?? orientationSourceBitmap;

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
        /// デコード前に画像寸法を検証し、過大な画素バッファの確保を防止する。
        /// </summary>
        private static void ValidateInputDimensions(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new InvalidOperationException("Invalid image size");
            }

            var pixelCount = checked((long)width * height);
            if (width > MaxInputDimension || height > MaxInputDimension || pixelCount > MaxInputPixels)
            {
                throw new InvalidOperationException("Image dimensions are too large");
            }
        }

        /// <summary>
        /// アスペクト比を維持し、指定された最大サイズに収まる寸法を算出する。
        /// </summary>
        private static SKSizeI CalculateTargetSize(int width, int height, int maxWidth, int maxHeight)
        {
            if (width <= maxWidth && height <= maxHeight)
            {
                return new SKSizeI(width, height);
            }

            var scale = Math.Min((double)maxWidth / width, (double)maxHeight / height);
            return new SKSizeI(
                Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
        }

        /// <summary>
        /// 向き補正によって幅と高さが入れ替わるかを判定する。
        /// </summary>
        private static bool SwapsDimensions(SKEncodedOrigin encodedOrigin)
        {
            return encodedOrigin is SKEncodedOrigin.LeftTop
                or SKEncodedOrigin.RightTop
                or SKEncodedOrigin.RightBottom
                or SKEncodedOrigin.LeftBottom;
        }

        /// <summary>
        /// EXIF Orientation に相当する向きを画素へ適用する。
        /// </summary>
        /// <param name="sourceBitmap">画像ファイルからデコードした、向き補正前のビットマップ。</param>
        /// <param name="encodedOrigin">画像ファイルのEXIF Orientationに対応する向き情報。</param>
        /// <returns>補正が不要な場合は <see langword="null"/>。</returns>
        private static SKBitmap? ApplyEncodedOrigin(SKBitmap sourceBitmap, SKEncodedOrigin encodedOrigin)
        {
            // TopLeft は画素が既に正しい向きで格納されているため、コピーを作成せず元画像を使用する。
            if (encodedOrigin == SKEncodedOrigin.TopLeft)
            {
                return null;
            }

            // 90度または270度回転する向きでは、補正後の画像サイズの幅と高さが入れ替わる。
            var swapsDimensions = SwapsDimensions(encodedOrigin);
            var orientedWidth = swapsDimensions ? sourceBitmap.Height : sourceBitmap.Width;
            var orientedHeight = swapsDimensions ? sourceBitmap.Width : sourceBitmap.Height;

            // 元画像の色形式、透明度、色空間を維持した補正後の描画先を作成する。
            var orientedBitmap = new SKBitmap(new SKImageInfo(
                orientedWidth,
                orientedHeight,
                sourceBitmap.ColorType,
                sourceBitmap.AlphaType,
                sourceBitmap.ColorSpace));

            // EXIF Orientation に対応する回転・反転の座標変換を設定し、 元の画素を変換後の位置へ描画することで、向きを画素データへ確定させる。
            using var canvas = new SKCanvas(orientedBitmap);
            canvas.SetMatrix(CreateEncodedOriginMatrix(
                encodedOrigin,
                sourceBitmap.Width,
                sourceBitmap.Height));
            canvas.DrawBitmap(sourceBitmap, 0, 0);
            canvas.Flush();

            return orientedBitmap;
        }

        /// <summary>
        /// EXIF Orientation が表す回転・反転を、元画像から補正後画像への座標変換行列に変換する。
        /// </summary>
        /// <param name="encodedOrigin">画像ファイルのEXIF Orientationに対応する向き情報。</param>
        /// <param name="sourceWidth">向き補正前の画像の幅。</param>
        /// <param name="sourceHeight">向き補正前の画像の高さ。</param>
        /// <returns>元画像の座標を補正後画像の座標へ変換する行列。</returns>
        private static SKMatrix CreateEncodedOriginMatrix(
            SKEncodedOrigin encodedOrigin,
            int sourceWidth,
            int sourceHeight)
        {
            return encodedOrigin switch
            {
                // 左右反転
                SKEncodedOrigin.TopRight => CreateMatrix(-1, 0, sourceWidth, 0, 1, 0),
                // 180度回転
                SKEncodedOrigin.BottomRight => CreateMatrix(-1, 0, sourceWidth, 0, -1, sourceHeight),
                // 上下反転
                SKEncodedOrigin.BottomLeft => CreateMatrix(1, 0, 0, 0, -1, sourceHeight),
                // 左上・右下を結ぶ軸で反転
                SKEncodedOrigin.LeftTop => CreateMatrix(0, 1, 0, 1, 0, 0),
                // 時計回りに90度回転
                SKEncodedOrigin.RightTop => CreateMatrix(0, -1, sourceHeight, 1, 0, 0),
                // 右上・左下を結ぶ軸で反転
                SKEncodedOrigin.RightBottom => CreateMatrix(0, -1, sourceHeight, -1, 0, sourceWidth),
                // 反時計回りに90度回転
                SKEncodedOrigin.LeftBottom => CreateMatrix(0, 1, 0, -1, 0, sourceWidth),
                _ => SKMatrix.Identity
            };
        }

        /// <summary>
        /// アフィン変換に使用する3×3行列を作成する。
        /// </summary>
        /// <param name="scaleX">入力X座標を出力X座標へ反映する係数。</param>
        /// <param name="skewX">入力Y座標を出力X座標へ反映する係数。</param>
        /// <param name="transX">出力X座標の移動量。</param>
        /// <param name="skewY">入力X座標を出力Y座標へ反映する係数。</param>
        /// <param name="scaleY">入力Y座標を出力Y座標へ反映する係数。</param>
        /// <param name="transY">出力Y座標の移動量。</param>
        /// <returns>指定された係数と移動量を持つ3×3行列。</returns>
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
