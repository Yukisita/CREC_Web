/*
CREC Web - Frontend Application
Copyright (c) [2025 - 2026] [S.Yukisita]
This software is released under the MIT License.
*/

const translations = {};

const languageNames = Object.freeze({
    ja: '日本語',
    de: 'Deutsch',
    en: 'English'
});

/**
 * 言語ごとの翻訳辞書を登録する。
 * @param {string} language
 * @param {Record<string, string>} dictionary
 */
function registerTranslations(language, dictionary) {
    if (!Object.prototype.hasOwnProperty.call(languageNames, language)) {
        throw new Error(`Unsupported language: ${language}`);
    }

    if (translations[language]) {
        throw new Error(`Translations already registered: ${language}`);
    }

    translations[language] = dictionary;
}

