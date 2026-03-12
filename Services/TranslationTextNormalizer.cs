using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hotkey_Translator.Services;

public sealed class TranslationTextNormalizer
{
    private const double CjkRatioThreshold = 0.60;
    private static readonly char[] SentenceEndingChars = ['。', '．', '.', '!', '?', '！', '？', '…'];
    private static readonly char[] MidSentencePunctuationChars = ['、', '，', ',', '；', ';', '：', ':'];
    private static readonly char[] BulletLeadingChars = ['-', '*', '・', '•', '●', '○', '■', '□', '◆', '◇', '▶', '▷', '►', '▸'];
    private static readonly string[] EnglishSentenceMarkers =
    [
        " the ", " a ", " an ", " is ", " are ", " was ", " were ", " to ", " of ", " and ", " but ",
        " you ", " we ", " they ", " he ", " she ", " it ", " this ", " that ", " when ", " where ",
        " what ", " why ", " how ", " can ", " will ", " should ", " could ", " would "
    ];
    private static readonly string[] JapaneseSentenceMarkers =
    [
        "です", "ます", "だった", "でした", "して", "した", "する", "ない", "から", "ので", "けど", "ただ", "でも", "しかし", "それ", "あれ", "これ"
    ];
    private static readonly string[] ChineseSentenceMarkers =
    [
        "但是", "可是", "因为", "因為", "所以", "如果", "不是", "沒有", "没有", "可以", "不能",
        "這樣", "这样", "那麼", "那么", "怎麼", "怎么", "為什麼", "为什么", "你們", "你们",
        "我們", "我们", "他們", "他们"
    ];

    public string NormalizeForTranslation(string input, string? sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var lines = NormalizeLines(input);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        // WHY: Preserve overlay/original OCR formatting and only join lines for the translation path
        // when the merged box looks like prose instead of menu-like UI entries.
        var translationText = lines.Count > 1 && ShouldJoinLinesForTranslation(lines)
            ? string.Join(' ', lines)
            : string.Join('\n', lines);

        if (!ShouldApplyCjkSpacingFix(translationText, sourceLanguage))
        {
            return translationText.Trim();
        }

        return CollapseCjkAdjacentSpaces(translationText).Trim();
    }

    private static List<string> NormalizeLines(string text)
    {
        var normalizedLineBreaks = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var result = new List<string>();
        foreach (var rawLine in normalizedLineBreaks.Split('\n'))
        {
            var normalizedLine = NormalizeInlineWhitespaceToSpaces(rawLine).Trim();
            if (!string.IsNullOrWhiteSpace(normalizedLine))
            {
                result.Add(normalizedLine);
            }
        }

        return result;
    }

    private static bool ShouldJoinLinesForTranslation(IReadOnlyList<string> lines)
    {
        if (lines.Count <= 1)
        {
            return false;
        }

        if (IsMenuLikeBlock(lines))
        {
            return false;
        }

        return IsSentenceLikeBlock(lines);
    }

    private static bool IsMenuLikeBlock(IReadOnlyList<string> lines)
    {
        var count = lines.Count;
        if (count == 0)
        {
            return false;
        }

        var bulletRatio = Ratio(lines, IsBulletLikeLine);
        var shortLineRatio = Ratio(lines, line => EffectiveTextLength(line) <= 14);
        var sentenceEndingRatio = Ratio(lines, EndsWithSentencePunctuation);
        var wordLabelRatio = Ratio(lines, IsWordLabelLikeLine);
        var statusLikeRatio = Ratio(lines, IsStatusLikeLine);

        var score = 0.0;
        if (bulletRatio >= 0.34)
        {
            score += 0.45;
        }

        if (shortLineRatio >= 0.75)
        {
            score += 0.25;
        }

        if (sentenceEndingRatio <= 0.20)
        {
            score += 0.15;
        }

        if (wordLabelRatio >= 0.60)
        {
            score += 0.20;
        }

        if (statusLikeRatio >= 0.50)
        {
            score += 0.20;
        }

        return score >= 0.50;
    }

    private static bool IsSentenceLikeBlock(IReadOnlyList<string> lines)
    {
        var count = lines.Count;
        if (count == 0)
        {
            return false;
        }

        var averageLength = lines.Average(EffectiveTextLength);
        var longLineRatio = Ratio(lines, line => EffectiveTextLength(line) >= 18);
        var sentenceEndingRatio = Ratio(lines, EndsWithSentencePunctuation);
        var midSentencePunctuationRatio = Ratio(lines, ContainsMidSentencePunctuation);
        var sentenceMarkerRatio = Ratio(lines, ContainsSentenceMarker);

        var score = 0.0;
        if (averageLength >= 16)
        {
            score += 0.25;
        }

        if (longLineRatio >= 0.40)
        {
            score += 0.15;
        }

        if (sentenceEndingRatio >= 0.20)
        {
            score += 0.25;
        }

        if (midSentencePunctuationRatio >= 0.20)
        {
            score += 0.15;
        }

        if (sentenceMarkerRatio >= 0.34)
        {
            score += 0.20;
        }

        return score >= 0.35;
    }

    private static string CollapseCjkAdjacentSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != ' ')
            {
                builder.Append(ch);
                continue;
            }

            var previousIndex = FindPreviousNonSpaceIndex(text, i - 1);
            var nextIndex = FindNextNonSpaceIndex(text, i + 1);
            if (previousIndex < 0 || nextIndex < 0)
            {
                continue;
            }

            var previous = text[previousIndex];
            var next = text[nextIndex];

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

        return builder.ToString();
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
        return normalized.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInlineWhitespaceToSpaces(string text)
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

    private static double Ratio(IReadOnlyList<string> lines, Func<string, bool> predicate)
    {
        if (lines.Count == 0)
        {
            return 0;
        }

        var matches = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (predicate(lines[i]))
            {
                matches++;
            }
        }

        return matches / (double)lines.Count;
    }

    private static bool IsBulletLikeLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (Array.IndexOf(BulletLeadingChars, trimmed[0]) >= 0)
        {
            return true;
        }

        if (trimmed.Length >= 2 && char.IsDigit(trimmed[0]))
        {
            var second = trimmed[1];
            return second is '.' or ')' or ':' or '、' or '．' or '）';
        }

        return false;
    }

    private static bool EndsWithSentencePunctuation(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.TrimEnd();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var last = trimmed[^1];
        if (Array.IndexOf(SentenceEndingChars, last) >= 0)
        {
            return true;
        }

        if (trimmed.Length >= 2)
        {
            var previous = trimmed[^2];
            return Array.IndexOf(SentenceEndingChars, previous) >= 0 && last is '"' or '\'' or ')' or '」' or '』';
        }

        return false;
    }

    private static bool ContainsMidSentencePunctuation(string line)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (Array.IndexOf(MidSentencePunctuationChars, line[i]) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSentenceMarker(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var padded = $" {line.Trim().ToLowerInvariant()} ";
        for (var i = 0; i < EnglishSentenceMarkers.Length; i++)
        {
            if (padded.Contains(EnglishSentenceMarkers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (var i = 0; i < JapaneseSentenceMarkers.Length; i++)
        {
            if (line.Contains(JapaneseSentenceMarkers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        for (var i = 0; i < ChineseSentenceMarkers.Length; i++)
        {
            if (line.Contains(ChineseSentenceMarkers[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordLabelLikeLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || EndsWithSentencePunctuation(trimmed))
        {
            return false;
        }

        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0 || words.Length > 3)
        {
            return false;
        }

        foreach (var word in words)
        {
            if (word.Length > 12)
            {
                return false;
            }

            var hasLetter = false;
            foreach (var ch in word)
            {
                if (char.IsLetter(ch))
                {
                    hasLetter = true;
                    continue;
                }

                if (!char.IsDigit(ch) && !IsSimpleLabelSymbol(ch))
                {
                    return false;
                }
            }

            if (!hasLetter && !word.Any(char.IsDigit))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsStatusLikeLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || EndsWithSentencePunctuation(trimmed))
        {
            return false;
        }

        var alnumCount = 0;
        var symbolCount = 0;
        foreach (var ch in trimmed)
        {
            if (char.IsLetterOrDigit(ch))
            {
                alnumCount++;
            }
            else if (!char.IsWhiteSpace(ch))
            {
                symbolCount++;
            }
        }

        if (alnumCount == 0)
        {
            return false;
        }

        var effectiveLength = EffectiveTextLength(trimmed);
        return effectiveLength <= 16 && symbolCount >= 1 && symbolCount >= (effectiveLength / 4.0);
    }

    private static bool IsSimpleLabelSymbol(char ch)
    {
        return ch is '-' or '_' or '/' or '\\' or '&' or '+' or ':';
    }

    private static int EffectiveTextLength(string line)
    {
        var count = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (!char.IsWhiteSpace(line[i]))
            {
                count++;
            }
        }

        return count;
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
