/*
CREC Web - Validation Helper
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    public static class ValidationHelper
    {
        private const int MaxCollectionIdLength = 255;

        /// <summary>
        /// コレクションIDの妥当性検証
        /// </summary>
        /// <param name="collectionId">検証するコレクションID</param>
        /// <returns>True: 有効なコレクションID / False: 無効なコレクションID</returns>
        public static bool IsValidCollectionId(string collectionId)
        {
            if (string.IsNullOrWhiteSpace(collectionId)) return false;
            if (collectionId.Length > MaxCollectionIdLength) return false;

            // ".." のみ、または "." のみで構成されるIDは無効（パストラバーサル・ルートアクセス防止）
            if (collectionId.All(c => c == '.')) return false;

            // パス区切り文字を禁止
            if (collectionId.Contains('/') || collectionId.Contains('\\')) return false;

            // Windows無効ファイル名文字を禁止（':'など）
            if (collectionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;

            // 末尾のドット・スペースを禁止（Windowsでのファイル名正規化によるID衝突防止）
            if (collectionId[^1] == '.' || collectionId[^1] == ' ') return false;

            // 予約済みシステムIDを禁止
            if (string.Equals(collectionId, "$SystemData", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
