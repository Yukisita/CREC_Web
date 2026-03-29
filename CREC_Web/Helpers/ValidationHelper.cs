/*
CREC Web - File Controller
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

namespace CREC_Web.Helpers
{
    public static class ValidationHelper
    {
        private const int MaxCollectionIdLength = 255;

        public static bool IsValidCollectionId(string collectionId)
        {
            return !string.IsNullOrWhiteSpace(collectionId) &&
                   System.Text.RegularExpressions.Regex.IsMatch(collectionId, @"^[a-zA-Z0-9_-]+$") &&
                   collectionId.Length <= MaxCollectionIdLength;
        }
    }
}