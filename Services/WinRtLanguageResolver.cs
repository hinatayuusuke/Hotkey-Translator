using System;

namespace Hotkey_Translator.Services;

internal static class WinRtLanguageResolver
{
    public static string? ResolveOcrLocale(string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return null;
        }

        var normalized = sourceLanguage.Trim().Replace('_', '-');
        if (normalized.Length == 0)
        {
            return null;
        }

        if (HasPrefix(normalized, "en"))
        {
            return "en-US";
        }

        if (HasPrefix(normalized, "ja"))
        {
            return "ja-JP";
        }

        if (HasPrefix(normalized, "ko"))
        {
            return "ko-KR";
        }

        if (HasPrefix(normalized, "ru"))
        {
            return "ru-RU";
        }

        if (HasPrefix(normalized, "zh-Hant") ||
            HasPrefix(normalized, "zh-TW") ||
            HasPrefix(normalized, "zh-HK") ||
            HasPrefix(normalized, "zh-MO"))
        {
            return "zh-TW";
        }

        if (HasPrefix(normalized, "zh-Hans") ||
            HasPrefix(normalized, "zh-CN") ||
            HasPrefix(normalized, "zh-SG") ||
            HasPrefix(normalized, "zh"))
        {
            return "zh-CN";
        }

        return normalized;
    }

    private static bool HasPrefix(string value, string prefix)
    {
        return value.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
    }
}
