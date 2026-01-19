using System;
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
    private readonly Windows.Media.Ocr.OcrEngine _engine;

    public OcrEngine()
    {
        _engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Language("en"))
            ?? throw new InvalidOperationException("OCR engine is unavailable.");
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var softwareBitmap = ConvertToSoftwareBitmap(bitmap);
        var result = await _engine.RecognizeAsync(softwareBitmap).AsTask(cancellationToken);

        var lines = new List<ModelOcrLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var rect = GetLineRect(line);
            lines.Add(new ModelOcrLine(line.Text, rect, 1.0f));
        }

        return new OcrResultModel(lines, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight);
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
