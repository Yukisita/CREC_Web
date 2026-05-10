/*
CREC Web - JPEG Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    /// <summary>
    /// JPEGバイト操作のヘルパー
    /// </summary>
    public static class JpegHelper
    {
        // JPEGマーカー定数
        private const byte MarkerPrefix = 0xFF;
        private const byte MarkerSOI    = 0xD8; // Start of Image
        private const byte MarkerEOI    = 0xD9; // End of Image
        private const byte MarkerSOS    = 0xDA; // Start of Scan
        private const byte MarkerRSTMin = 0xD0; // Restart Marker 0
        private const byte MarkerRSTMax = 0xD7; // Restart Marker 7
        private const byte MarkerAPP2   = 0xE2; // Application-specific segment 2 (ICC profile)

        // ICC_PROFILE\0 シグネチャ（12バイト）
        private static ReadOnlySpan<byte> IccSignature => "ICC_PROFILE\0"u8;

        /// <summary>
        /// JPEGバイト列からICCプロファイル（APP2マーカー）を除去して返す。
        /// ICCプロファイルを除去することでブラウザがWeb標準sRGBとして扱うようになり、
        /// Android Chrome でのハードウェアカラーマネジメントによる画面点滅を防止できる。
        /// </summary>
        public static byte[] StripIccProfile(byte[] jpegData)
        {
            if (!IsValidJpeg(jpegData))
                return jpegData;

            using var output = new MemoryStream();
            output.Write(jpegData, 0, 2); // SOI書き込み

            int pos = 2;
            while (pos < jpegData.Length - 1)
            {
                if (jpegData[pos] != MarkerPrefix)
                    break; // 不正なJPEG

                // 0xFFのフィルバイトはJPEG仕様上許容される（スキップ）
                if (jpegData[pos + 1] == MarkerPrefix)
                {
                    pos++;
                    continue;
                }

                byte marker = jpegData[pos + 1];

                if (marker == MarkerEOI)
                {
                    output.Write(jpegData, pos, 2);
                    break;
                }

                // RST0-RST7: 長さフィールドなし
                if (marker is >= MarkerRSTMin and <= MarkerRSTMax)
                {
                    output.Write(jpegData, pos, 2);
                    pos += 2;
                    continue;
                }

                if (!TryReadSegmentLength(jpegData, pos, out int segLen))
                    break;

                int segEnd = pos + 2 + segLen;
                if (segEnd > jpegData.Length)
                    break; // 切り詰められたデータ

                // SOS以降は可変長の圧縮データのためそのままコピー
                if (marker == MarkerSOS)
                {
                    output.Write(jpegData, pos, jpegData.Length - pos);
                    break;
                }

                if (!IsIccProfileSegment(jpegData, pos, marker, segLen))
                    output.Write(jpegData, pos, 2 + segLen);

                pos = segEnd;
            }

            return output.ToArray();
        }

        /// <summary>有効なJPEGバイト列か（SOIマーカーで始まるか）確認する。</summary>
        private static bool IsValidJpeg(byte[] data) =>
            data.Length >= 2 && data[0] == MarkerPrefix && data[1] == MarkerSOI;

        /// <summary>指定位置のセグメント長フィールド（2バイトビッグエンディアン）を読み取る。</summary>
        private static bool TryReadSegmentLength(byte[] data, int pos, out int segLen)
        {
            if (pos + 3 >= data.Length)
            {
                segLen = 0;
                return false;
            }
            segLen = (data[pos + 2] << 8) | data[pos + 3]; // 長さフィールド自身を含む
            return segLen >= 2;
        }

        /// <summary>指定セグメントがICCプロファイルを含むAPP2セグメントかどうかを判定する。</summary>
        private static bool IsIccProfileSegment(byte[] data, int pos, byte marker, int segLen) =>
            marker == MarkerAPP2
            && segLen > IccSignature.Length + 2
            && pos + 4 + IccSignature.Length <= data.Length
            && data.AsSpan(pos + 4, IccSignature.Length).SequenceEqual(IccSignature);
    }
}
