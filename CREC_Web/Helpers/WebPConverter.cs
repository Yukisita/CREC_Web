/*
CREC Web - WebP Converter
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

using System.Runtime.InteropServices;

namespace CREC_Web.Helpers
{
    /// <summary>
    /// libturbojpeg（JPEG デコード）と libwebp（WebP エンコード）を
    /// P/Invoke で呼び出して JPEG を WebP に変換するヘルパー。
    /// サードパーティ NuGet パッケージは不要。
    /// 動作には libturbojpeg0 および libwebp7 のシステムライブラリが必要。
    /// </summary>
    public static class WebPConverter
    {
        // TurboJPEG の TJPF_RGB ピクセルフォーマット定数
        private const int TjpfRgb = 2;

        // デフォルト WebP 品質（0-100）
        private const float DefaultQuality = 85f;

        /// <summary>
        /// システムライブラリが利用可能かどうか（静的初期化時に決定）
        /// </summary>
        public static readonly bool IsAvailable;

        static WebPConverter()
        {
            // カスタム DLL リゾルバを設定: バージョン付きの .so 名を優先的に試みる
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(WebPConverter).Assembly,
                    static (libraryName, assembly, searchPath) =>
                    {
                        string[]? candidates = libraryName switch
                        {
                            "libturbojpeg" => ["libturbojpeg", "libturbojpeg.so.0", "turbojpeg"],
                            "libwebp"      => ["libwebp", "libwebp.so.7", "libwebp.so.6", "webp"],
                            _              => null
                        };

                        if (candidates is not null)
                        {
                            foreach (var name in candidates)
                            {
                                if (NativeLibrary.TryLoad(name, out var handle))
                                    return handle;
                            }
                        }

                        return NativeLibrary.Load(libraryName, assembly, searchPath);
                    });
            }
            catch
            {
                // リゾルバ設定失敗時はデフォルト解決にフォールバック
            }

            IsAvailable = CheckAvailable();
        }

        private static bool CheckAvailable()
        {
            try
            {
                var handle = TjInitDecompress();
                if (handle == IntPtr.Zero) return false;
                TjDestroy(handle);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// JPEG バイト列を WebP バイト列に変換する。
        /// 変換に失敗した場合は null を返す（呼び出し側がフォールバック処理を行う）。
        /// </summary>
        /// <param name="jpegData">入力 JPEG バイト列</param>
        /// <param name="quality">WebP 品質 (0-100、デフォルト 85)</param>
        public static byte[]? ConvertToWebP(byte[] jpegData, float quality = DefaultQuality)
        {
            if (!IsAvailable || jpegData is null || jpegData.Length == 0)
                return null;

            var decompHandle = IntPtr.Zero;
            try
            {
                decompHandle = TjInitDecompress();
                if (decompHandle == IntPtr.Zero) return null;

                int ret = TjDecompressHeader3(
                    decompHandle, jpegData, (ulong)jpegData.Length,
                    out int width, out int height, out _, out _);

                if (ret != 0 || width <= 0 || height <= 0) return null;

                var rgb = new byte[width * height * 3];

                ret = TjDecompress2(
                    decompHandle, jpegData, (ulong)jpegData.Length,
                    rgb, width, 0 /* auto stride */, height, TjpfRgb, 0);

                if (ret != 0) return null;

                nuint encodedSize = WebPEncodeRGB(
                    rgb, width, height, width * 3, quality, out IntPtr webpPtr);

                if (encodedSize == 0 || webpPtr == IntPtr.Zero) return null;
                if (encodedSize > (nuint)Array.MaxLength) return null; // 実用上起こり得ないが安全のため

                try
                {
                    var webpData = new byte[(int)encodedSize];
                    Marshal.Copy(webpPtr, webpData, 0, (int)encodedSize);
                    return webpData;
                }
                finally
                {
                    WebPFree(webpPtr);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (decompHandle != IntPtr.Zero)
                    TjDestroy(decompHandle);
            }
        }

        // --- libturbojpeg P/Invoke ---
        // unsigned long は Linux 64-bit (LP64) では 8 バイト = ulong

        [DllImport("libturbojpeg", EntryPoint = "tjInitDecompress")]
        private static extern IntPtr TjInitDecompress();

        [DllImport("libturbojpeg", EntryPoint = "tjDecompressHeader3")]
        private static extern int TjDecompressHeader3(
            IntPtr handle,
            [In] byte[] jpegBuf, ulong jpegSize,
            out int width, out int height,
            out int jpegSubsamp, out int jpegColorspace);

        [DllImport("libturbojpeg", EntryPoint = "tjDecompress2")]
        private static extern int TjDecompress2(
            IntPtr handle,
            [In] byte[] jpegBuf, ulong jpegSize,
            [Out] byte[] dstBuf, int width, int pitch, int height,
            int pixelFormat, int flags);

        [DllImport("libturbojpeg", EntryPoint = "tjDestroy")]
        private static extern int TjDestroy(IntPtr handle);

        // --- libwebp P/Invoke ---
        // 戻り値は size_t = nuint

        [DllImport("libwebp", EntryPoint = "WebPEncodeRGB")]
        private static extern nuint WebPEncodeRGB(
            [In] byte[] rgb, int width, int height, int stride,
            float qualityFactor, out IntPtr output);

        [DllImport("libwebp", EntryPoint = "WebPFree")]
        private static extern void WebPFree(IntPtr pointer);
    }
}
