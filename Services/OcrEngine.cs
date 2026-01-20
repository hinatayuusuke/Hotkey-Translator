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

public sealed class OcrEngine
{
    private readonly ConcurrentDictionary<string, Windows.Media.Ocr.OcrEngine> _engines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Windows.Media.Ocr.OcrEngine _fallbackEngine;

    public OcrEngine()
    {
        _fallbackEngine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Language("en"))
            ?? throw new InvalidOperationException("OCR engine is unavailable.");
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, string? languageTag, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var engine = ResolveEngine(languageTag);
        var result = await engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        var lines = new List<ModelOcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var rect = GetLineRect(line);
            lines.Add(new ModelOcrLine(line.Text, rect, 1.0f));
        }

        return new OcrResultModel(lines, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
    }

    private Windows.Media.Ocr.OcrEngine ResolveEngine(string? languageTag)
    {
        if (string.IsNullOrWhiteSpace(languageTag))
        {
            return _fallbackEngine;
        }

        var normalized = languageTag.Trim().Replace('_', '-');
        if (normalized.Length == 0)
        {
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
                // NOTE: Fall back when the requested OCR language is not installed or unsupported.
                return _fallbackEngine;
            }

            _engines.TryAdd(normalized, engine);
            return engine;
        }
        catch
        {
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
