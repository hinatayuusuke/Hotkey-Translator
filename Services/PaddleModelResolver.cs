using System;
using System.Collections.Generic;

namespace Hotkey_Translator.Services;

internal static class PaddleModelResolver
{
    public const string DefaultDetectionModel = "PP-OCRv5_mobile_det";
    public const string DefaultRecognitionModel = "PP-OCRv5_server_rec";
    public const string AutoRecognitionModel = "auto";

    private static readonly Dictionary<string, string> DetectionModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PP-OCRv5_mobile_det"] = "PP-OCRv5_mobile_det",
        ["PP-OCRv5_server_det"] = "PP-OCRv5_server_det",
    };

    private static readonly Dictionary<string, string> RecognitionModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["auto"] = "auto",
        ["PP-OCRv5_server_rec"] = "PP-OCRv5_server_rec",
        ["en_PP-OCRv5_mobile_rec"] = "en_PP-OCRv5_mobile_rec",
        ["latin_PP-OCRv5_mobile_rec"] = "latin_PP-OCRv5_mobile_rec",
        ["eslav_PP-OCRv5_mobile_rec"] = "eslav_PP-OCRv5_mobile_rec",
        ["korean_PP-OCRv5_mobile_rec"] = "korean_PP-OCRv5_mobile_rec",
        ["th_PP-OCRv5_mobile_rec"] = "th_PP-OCRv5_mobile_rec",
        ["el_PP-OCRv5_mobile_rec"] = "el_PP-OCRv5_mobile_rec",
        ["arabic_PP-OCRv5_mobile_rec"] = "arabic_PP-OCRv5_mobile_rec",
        ["cyrillic_PP-OCRv5_mobile_rec"] = "cyrillic_PP-OCRv5_mobile_rec",
        ["devanagari_PP-OCRv5_mobile_rec"] = "devanagari_PP-OCRv5_mobile_rec",
        ["te_PP-OCRv5_mobile_rec"] = "te_PP-OCRv5_mobile_rec",
        ["ta_PP-OCRv5_mobile_rec"] = "ta_PP-OCRv5_mobile_rec",
    };

    private static readonly HashSet<string> LatinLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "af", "sq", "bs", "ca", "cs", "cy", "da", "de", "nl", "et", "fi", "fr", "ga", "gl",
        "hr", "hu", "id", "it", "la", "lt", "lv", "ms", "mt", "nb", "nn", "no", "pl", "pt",
        "ro", "sk", "sl", "es", "sv", "sw", "tl", "tr", "uz", "vi", "is",
    };

    private static readonly HashSet<string> EastSlavicLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ru", "uk", "be",
    };

    private static readonly HashSet<string> CyrillicLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "bg", "kk", "ky", "mk", "mn", "sr", "tg",
    };

    private static readonly HashSet<string> DevanagariLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "hi", "mr", "ne", "sa",
    };

    private static readonly HashSet<string> ArabicScriptLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ar", "fa", "ur", "ps",
    };

    public static string ResolvePaddleLanguage(string? sourceLanguage)
    {
        var normalized = NormalizeLanguageTag(sourceLanguage);
        if (HasLanguagePrefix(normalized, "ja"))
        {
            return "japan";
        }

        if (HasLanguagePrefix(normalized, "zh"))
        {
            // WHY: PaddleOCR expects language families ("ch"/"chinese_cht"), not BCP-47 script tags.
            return IsTraditionalChinese(normalized) ? "chinese_cht" : "ch";
        }

        if (HasLanguagePrefix(normalized, "ru"))
        {
            return "cyrillic";
        }

        return "en";
    }

    public static string NormalizeDetectionModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return DefaultDetectionModel;
        }

        var trimmed = modelName.Trim();
        return DetectionModels.TryGetValue(trimmed, out var canonical)
            ? canonical
            : DefaultDetectionModel;
    }

    public static string NormalizeRecognitionModelName(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return DefaultRecognitionModel;
        }

        var trimmed = modelName.Trim();
        return RecognitionModels.TryGetValue(trimmed, out var canonical)
            ? canonical
            : DefaultRecognitionModel;
    }

    public static bool IsSupportedDetectionModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return DetectionModels.ContainsKey(modelName.Trim());
    }

    public static bool IsSupportedRecognitionModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return false;
        }

        return RecognitionModels.ContainsKey(modelName.Trim());
    }

    public static string ResolveRecognitionModelForExecution(string? selectedModel, string? sourceLanguage)
    {
        var normalized = NormalizeRecognitionModelName(selectedModel);
        if (!string.Equals(normalized, AutoRecognitionModel, StringComparison.OrdinalIgnoreCase))
        {
            return normalized;
        }

        return ResolveAutoRecognitionModel(sourceLanguage);
    }

    private static string ResolveAutoRecognitionModel(string? sourceLanguage)
    {
        var normalized = NormalizeLanguageTag(sourceLanguage);
        if (string.IsNullOrEmpty(normalized))
        {
            return DefaultRecognitionModel;
        }

        if (HasLanguagePrefix(normalized, "ja") || HasLanguagePrefix(normalized, "zh"))
        {
            return DefaultRecognitionModel;
        }

        if (HasLanguagePrefix(normalized, "en"))
        {
            return "en_PP-OCRv5_mobile_rec";
        }

        if (HasLanguagePrefix(normalized, "ko"))
        {
            return "korean_PP-OCRv5_mobile_rec";
        }

        if (HasLanguagePrefix(normalized, "th"))
        {
            return "th_PP-OCRv5_mobile_rec";
        }

        if (HasLanguagePrefix(normalized, "el"))
        {
            return "el_PP-OCRv5_mobile_rec";
        }

        if (HasLanguagePrefix(normalized, "te"))
        {
            return "te_PP-OCRv5_mobile_rec";
        }

        if (HasLanguagePrefix(normalized, "ta"))
        {
            return "ta_PP-OCRv5_mobile_rec";
        }

        var primary = GetPrimaryLanguage(normalized);
        if (EastSlavicLanguages.Contains(primary))
        {
            return "eslav_PP-OCRv5_mobile_rec";
        }

        if (IsCyrillicLanguage(normalized))
        {
            return "cyrillic_PP-OCRv5_mobile_rec";
        }

        if (DevanagariLanguages.Contains(primary))
        {
            return "devanagari_PP-OCRv5_mobile_rec";
        }

        if (ArabicScriptLanguages.Contains(primary))
        {
            return "arabic_PP-OCRv5_mobile_rec";
        }

        if (IsLatinLanguage(normalized))
        {
            return "latin_PP-OCRv5_mobile_rec";
        }

        return DefaultRecognitionModel;
    }

    private static bool IsTraditionalChinese(string normalizedLanguageTag)
    {
        return normalizedLanguageTag == "zh-tw"
               || normalizedLanguageTag == "zh-hk"
               || normalizedLanguageTag == "zh-mo"
               || normalizedLanguageTag == "zh-hant"
               || normalizedLanguageTag.StartsWith("zh-hant-", StringComparison.Ordinal);
    }

    private static bool IsLatinLanguage(string normalizedLanguageTag)
    {
        if (normalizedLanguageTag.Contains("-latn", StringComparison.Ordinal))
        {
            return true;
        }

        return LatinLanguages.Contains(GetPrimaryLanguage(normalizedLanguageTag));
    }

    private static bool IsCyrillicLanguage(string normalizedLanguageTag)
    {
        if (normalizedLanguageTag.Contains("-cyrl", StringComparison.Ordinal))
        {
            return true;
        }

        return CyrillicLanguages.Contains(GetPrimaryLanguage(normalizedLanguageTag));
    }

    private static bool HasLanguagePrefix(string normalizedLanguageTag, string prefix)
    {
        return string.Equals(normalizedLanguageTag, prefix, StringComparison.OrdinalIgnoreCase)
               || normalizedLanguageTag.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLanguageTag(string? sourceLanguage)
    {
        return (sourceLanguage ?? string.Empty).Trim().ToLowerInvariant().Replace('_', '-');
    }

    private static string GetPrimaryLanguage(string normalizedLanguageTag)
    {
        var separatorIndex = normalizedLanguageTag.IndexOf('-');
        return separatorIndex <= 0 ? normalizedLanguageTag : normalizedLanguageTag[..separatorIndex];
    }
}
