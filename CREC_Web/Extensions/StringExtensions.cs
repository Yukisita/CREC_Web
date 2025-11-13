/*
CREC Web - Extensions
Copyright (c) [2025] [S.Yukisita]
This software is released under the MIT License.
*/

using System;
using System.Linq;

namespace CREC_Web.Extensions
{
    public static class StringExtensions
    {
        /// <summary>
        /// ログ出力用に制御文字を除去し、長さを制限するメソッド
        /// </summary>
        /// <param name="input">入力文字列</param>
        /// <param name="maxLength">長さ制限値</param>
        /// <returns></returns>
        public static string SanitizeForLog(this string? input, int maxLength = 200)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            var cleaned = new string(input.Where(c => !char.IsControl(c)).ToArray());
            return cleaned.Length <= maxLength ? cleaned : cleaned.Substring(0, maxLength);
        }
    }
}