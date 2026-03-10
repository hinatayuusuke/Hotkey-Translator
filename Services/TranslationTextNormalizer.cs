using System.Text;

namespace Hotkey_Translator.Services;

public sealed class TranslationTextNormalizer
{
    private const double CjkRatioThreshold = 0.60;

    public string NormalizeForTranslation(string input, string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var normalizedWhitespace = NormalizeWhitespaceToSpaces(input);
        if (!ShouldApplyCjkSpacingFix(normalizedWhitespace, sourceLanguage))
        {
            return normalizedWhitespace.Trim();
        }

        var builder = new StringBuilder(normalizedWhitespace.Length);
        for (var i = 0; i < normalizedWhitespace.Length; i++)
        {
            var ch = normalizedWhitespace[i];
            if (ch != ' ')
            {
                builder.Append(ch);
                continue;
            }

            var previousIndex = FindPreviousNonSpaceIndex(normalizedWhitespace, i - 1);
            var nextIndex = FindNextNonSpaceIndex(normalizedWhitespace, i + 1);
            if (previousIndex < 0 || nextIndex < 0)
            {
                continue;
            }

            var previous = normalizedWhitespace[previousIndex];
            var next = normalizedWhitespace[nextIndex];

            // WHY: VisionLLM and synthetic hybrid text can inject readability spaces that are harmful
            // for CJK translation quality. Keep Latin spacing intact and only collapse CJK-adjacent artifacts.
            if (char.IsPunctuation(next) ||
                (IsCjkCharacter(previous) && IsCjkCharacter(next)) ||
                (IsCjkCharacter(previous) && char.IsDigit(next)) ||
                (char.IsDigit(previous) && IsCjkCharacter(next)))
            {
                continue;
            }

            if (builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }
        }

        return builder.ToString().Trim();
    }

    private static bool ShouldApplyCjkSpacingFix(string text, string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (IsLikelyCjkLanguage(sourceLanguage))
        {
            return true;
        }

        var nonWhitespaceCount = 0;
        var cjkCount = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000')
            {
                continue;
            }

            nonWhitespaceCount++;
            if (IsCjkCharacter(ch))
            {
                cjkCount++;
            }
        }

        if (nonWhitespaceCount == 0)
        {
            return false;
        }

        return (cjkCount / (double)nonWhitespaceCount) >= CjkRatioThreshold;
    }

    private static bool IsLikelyCjkLanguage(string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return false;
        }

        var normalized = sourceLanguage.Trim().Replace('_', '-');
        return normalized.StartsWith("ja", System.StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWhitespaceToSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000')
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = false;
        }

        return builder.ToString();
    }

    private static int FindPreviousNonSpaceIndex(string text, int startIndex)
    {
        for (var i = startIndex; i >= 0; i--)
        {
            if (text[i] != ' ')
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextNonSpaceIndex(string text, int startIndex)
    {
        for (var i = startIndex; i < text.Length; i++)
        {
            if (text[i] != ' ')
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsCjkCharacter(char value)
    {
        return value is >= '\u3040' and <= '\u30FF' or
               >= '\u3400' and <= '\u4DBF' or
               >= '\u4E00' and <= '\u9FFF' or
               >= '\uF900' and <= '\uFAFF';
    }
}
