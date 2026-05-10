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
        /// <summary>
        /// JPEGバイト列からICCプロファイル（APP2マーカー）を除去して返す。
        /// ICCプロファイルを除去することでブラウザがWeb標準sRGBとして扱うようになり、
        /// Android Chrome でのハードウェアカラーマネジメントによる画面点滅を防止できる。
        /// </summary>
        public static byte[] StripIccProfile(byte[] jpegData)
        {
            // SOI確認（有効なJPEGでなければそのまま返す）
            if (jpegData.Length < 2 || jpegData[0] != 0xFF || jpegData[1] != 0xD8)
                return jpegData;

            // 出力はICCプロファイル分小さくなるためデフォルトキャパシティで十分
            using var output = new MemoryStream();
            output.Write(jpegData, 0, 2); // SOI書き込み

            int pos = 2;
            while (pos < jpegData.Length - 1)
            {
                if (jpegData[pos] != 0xFF)
                    break; // 不正なJPEG

                // JPEGでは0xFFのフィルバイトがマーカー前に許容される（1バイトずつスキップ）
                if (jpegData[pos + 1] == 0xFF)
                {
                    pos++;
                    continue;
                }

                byte marker = jpegData[pos + 1];

                // EOI（画像終端）
                if (marker == 0xD9)
                {
                    output.Write(jpegData, pos, 2);
                    break;
                }

                // RST0-RST7: 長さフィールドなし
                if (marker is >= 0xD0 and <= 0xD7)
                {
                    output.Write(jpegData, pos, 2);
                    pos += 2;
                    continue;
                }

                if (pos + 3 >= jpegData.Length)
                    break; // 長さフィールドの読み取りに必要なバイト数が不足

                int segLen = (jpegData[pos + 2] << 8) | jpegData[pos + 3]; // 長さフィールド自身を含む
                if (segLen < 2)
                    break; // 不正なJPEG

                int segEnd = pos + 2 + segLen;
                if (segEnd > jpegData.Length)
                    break; // 切り詰められたデータ

                // SOS（スキャン開始）以降は圧縮データのためそのままコピー
                if (marker == 0xDA)
                {
                    output.Write(jpegData, pos, jpegData.Length - pos);
                    break;
                }

                // APP2マーカーにICCプロファイルシグネチャが含まれる場合はスキップ
                // ICC_PROFILE\0 は12バイト: データはpos+4から始まりpos+15まで読む必要がある
                bool isIcc = marker == 0xE2
                    && segLen > 14
                    && pos + 16 <= jpegData.Length  // pos+4 から12バイト（pos+4〜pos+15）が確実に存在する
                    && jpegData.AsSpan(pos + 4, 12).SequenceEqual("ICC_PROFILE\0"u8);

                if (!isIcc)
                {
                    output.Write(jpegData, pos, 2 + segLen);
                }

                pos = segEnd;
            }

            return output.ToArray();
        }
    }
}
