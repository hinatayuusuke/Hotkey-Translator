using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;
using ModelOcrLine = Hotkey_Translator.Models.OcrLine;
using WinOcrLine = Windows.Media.Ocr.OcrLine;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Hotkey_Translator.Services;

public sealed class WinRtOcrProvider : IOcrProvider
{
    private const double CjkRatioThreshold = 0.60;
    private const int LogPreviewMaxChars = 120;
    private readonly ConcurrentDictionary<string, Windows.Media.Ocr.OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Windows.Media.Ocr.OcrEngine _fallbackEngine;
    private readonly AppLogger? _logger;

    public WinRtOcrProvider(AppLogger? logger = null)
    {
        _logger = logger;
        _fallbackEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Language("en"))
            ?? throw new InvalidOperationException("OCR engine is unavailable.");
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var engine = ResolveEngine(settings.SourceLanguage);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        var lines = new List<ModelOcrLine>(result.Lines.Count);
        var normalizedLineCount = 0;
        string? sampleBefore = null;
        string? sampleAfter = null;
        foreach (var line in result.Lines)
        {
            var rect = GetLineRect(line);
            var text = line.Text ?? string.Empty;
            if (ShouldApplyCjkSpacingFix(text, settings.SourceLanguage))
            {
                var normalized = NormalizeWinRtCjkSpacing(text);
                if (!string.Equals(text, normalized, StringComparison.Ordinal))
                {
                    normalizedLineCount++;
                    if (sampleBefore is null)
                    {
                        sampleBefore = text;
                        sampleAfter = normalized;
                    }
                    text = normalized;
                }
            }

            lines.Add(new ModelOcrLine(text, rect, 1.0f, 1, rect.Height));
        }

        if (normalizedLineCount > 0)
        {
            _logger?.Info(
                $"WinRT CJK spacing fix: normalized {normalizedLineCount}/{result.Lines.Count} lines. " +
                $"sample=\"{ToLogPreview(sampleBefore)}\" => \"{ToLogPreview(sampleAfter)}\".");
        }

        return new OcrResultModel(lines, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
    }

    private Windows.Media.Ocr.OcrEngine ResolveEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            _logger?.Info($"OCR language not specified. Using fallback '{_fallbackEngine.RecognizerLanguage.LanguageTag}'.");
            return _fallbackEngine;
        }

        var normalized = WinRtLanguageResolver.ResolveOcrLocale(languageTag) ?? string.Empty;
        if (normalized.Length == 0)
        {
            _logger?.Info($"OCR language tag is empty. Using fallback '{_fallbackEngine.RecognizerLanguage.LanguageTag}'.");
            return _fallbackEngine;
        }

        if (_engines.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        try
        {
            var language = new Language(normalized);
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(language);
            if (engine is null)
            {
                _logger?.Info($"OCR language '{normalized}' unavailable. Using fallback '{_fallbackEngine.RecognizerLanguage.LanguageTag}'.");
                // NOTE: Fall back when the requested OCR language is not installed or unsupported.
                return _fallbackEngine;
            }

            _engines.TryAdd(normalized, engine);
            _logger?.Info($"OCR language resolved: requested '{normalized}', using '{engine.RecognizerLanguage.LanguageTag}'.");
            return engine;
        }
        catch
        {
            _logger?.Info($"OCR language '{normalized}' invalid. Using fallback '{_fallbackEngine.RecognizerLanguage.LanguageTag}'.");
            return _fallbackEngine;
        }
    }

    private static Rect GetLineRect(WinOcrLine line)
    {
        var rect = Rect.Empty;
        foreach (var word in line.Words)
        {
            var wordRect = new Rect(word.BoundingRect.X, word.BoundingRect.Y, word.BoundingRect.Width, word.BoundingRect.Height);
            rect = rect.IsEmpty ? wordRect : Rect.Union(rect, wordRect);
        }

        return rect;
    }

    private static SoftwareBitmap ConvertToSoftwareBitmap(Bitmap bitmap)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var buffer = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, bitmap.Width, bitmap.Height, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(buffer.AsBuffer());
            return softwareBitmap;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
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

    private static string NormalizeWinRtCjkSpacing(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var normalizedWhitespace = NormalizeWhitespaceToSpaces(text);
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

            // WHY: CJK adjacent spacing from WinRT often appears as tokenization artifacts and degrades translation quality.
            if (char.IsPunctuation(next))
            {
                continue;
            }

            if ((IsCjkCharacter(previous) && IsCjkCharacter(next)) ||
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

    private static string NormalizeWhitespaceToSpaces(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            builder.Append(char.IsWhiteSpace(ch) || ch == '\u3000' ? ' ' : ch);
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

    private static string ToLogPreview(string? text)
    {
        var value = text ?? string.Empty;
        var visible = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace(" ", "<sp>", StringComparison.Ordinal)
            .Replace("\u3000", "<fwsp>", StringComparison.Ordinal);
        if (visible.Length <= LogPreviewMaxChars)
        {
            return visible;
        }

        return visible[..LogPreviewMaxChars] + "...";
    }
}
