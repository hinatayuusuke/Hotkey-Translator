using System.Globalization;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrCandidateScorer
{
    private const double CharWeight = 10.0;
    private const double LineWeight = 1.0;
    private const double SymbolPenaltyThreshold = 0.45;
    private const double SymbolPenaltyScale = 0.7;

    public OcrCandidateStats GetStats(OcrResultModel result)
    {
        var lineCount = result.Lines.Count;
        var charCount = 0;
        var symbolCount = 0;
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrEmpty(line.Text))
            {
                continue;
            }

            foreach (var ch in line.Text)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                charCount++;
                if (IsSymbolOrPunctuation(ch))
                {
                    symbolCount++;
                }
            }
        }

        var ratio = charCount > 0 ? symbolCount / (double)charCount : 0.0;
        var score = (charCount * CharWeight) + (lineCount * LineWeight);
        if (ratio >= SymbolPenaltyThreshold)
        {
            // WHY: Penalize symbol-heavy OCR output to avoid picking noisy candidates.
            score *= SymbolPenaltyScale;
        }

        return new OcrCandidateStats(lineCount, charCount, symbolCount, score, ratio);
    }

    public bool IsBetter(OcrCandidateStats candidate, OcrCandidateStats current)
    {
        if (candidate.Score != current.Score)
        {
            return candidate.Score > current.Score;
        }

        if (candidate.CharCount != current.CharCount)
        {
            return candidate.CharCount > current.CharCount;
        }

        return candidate.LineCount > current.LineCount;
    }

    private static bool IsSymbolOrPunctuation(char ch)
    {
        var category = char.GetUnicodeCategory(ch);
        return category == UnicodeCategory.MathSymbol ||
               category == UnicodeCategory.CurrencySymbol ||
               category == UnicodeCategory.ModifierSymbol ||
               category == UnicodeCategory.OtherSymbol ||
               category == UnicodeCategory.ConnectorPunctuation ||
               category == UnicodeCategory.DashPunctuation ||
               category == UnicodeCategory.OpenPunctuation ||
               category == UnicodeCategory.ClosePunctuation ||
               category == UnicodeCategory.InitialQuotePunctuation ||
               category == UnicodeCategory.FinalQuotePunctuation ||
               category == UnicodeCategory.OtherPunctuation;
    }
}

public readonly record struct OcrCandidateStats(
    int LineCount,
    int CharCount,
    int SymbolCount,
    double Score,
    double SymbolRatio);
