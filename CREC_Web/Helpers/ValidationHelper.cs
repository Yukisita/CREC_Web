/*
CREC Web - Validation Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    public static class ValidationHelper
    {
        // コレクションIDの最大長を定義（255文字以下に制限）
        private const int MaxCollectionIdLength = 255;

        // Windows無効ファイル名文字を明示的に定義（クロスプラットフォーム対応）
        private static readonly char[] WindowsInvalidFileNameChars = new[]
        {
            '<', '>', ':', '"', '/', '\\', '|', '?', '*'
        };

        // Windows予約デバイス名
        private static readonly HashSet<string> WindowsReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>
        /// コレクションIDの妥当性検証
        /// </summary>
        /// <param name="collectionId">検証するコレクションID</param>
        /// <returns>True: 有効なコレクションID / False: 無効なコレクションID</returns>
        public static bool IsValidCollectionId(string collectionId)
        {
            if (string.IsNullOrWhiteSpace(collectionId)) return false;
            if (collectionId.Length > MaxCollectionIdLength) return false;

            // "." や ".." など、ドットのみで構成されるIDは無効（パストラバーサル・ルートアクセス防止）
            if (collectionId.All(c => c == '.')) return false;

            // パス区切り文字を禁止
            if (collectionId.Contains('/') || collectionId.Contains('\\')) return false;

            // Windows無効ファイル名文字を禁止（クロスプラットフォーム対応）
            if (collectionId.IndexOfAny(WindowsInvalidFileNameChars) >= 0) return false;

            // OS固有の無効文字を追加で禁止
            if (collectionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            // 制御文字を禁止
            if (collectionId.Any(c => char.IsControl(c))) return false;

            // 末尾のドット・スペースを禁止（Windowsでのファイル名正規化によるID衝突防止）
            if (collectionId[^1] == '.' || collectionId[^1] == ' ') return false;

            // IDをフォルダ名として使用するためWindows予約デバイス名を禁止（拡張子を除いて判定）
            var collectionIdWithoutExtension = Path.GetFileNameWithoutExtension(collectionId);
            if (WindowsReservedDeviceNames.Contains(collectionIdWithoutExtension)) return false;

            // 予約済みシステムIDを禁止
            if (string.Equals(collectionId, "$SystemData", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
