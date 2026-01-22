using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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
        foreach (var line in result.Lines)
        {
            var rect = GetLineRect(line);
            lines.Add(new ModelOcrLine(line.Text, rect, 1.0f, 1, rect.Height));
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

        var normalized = languageTag.Trim().Replace('_', '-');
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
}
