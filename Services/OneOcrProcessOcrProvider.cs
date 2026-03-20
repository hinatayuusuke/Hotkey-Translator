using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OneOcrProcessOcrProvider : IOcrProvider, IDisposable
{
    private readonly OneOcrProcessHost _host;
    private readonly AppLogger? _logger;

    public OneOcrProcessOcrProvider(AppLogger? logger = null)
    {
        _host = new OneOcrProcessHost(logger);
        _logger = logger;
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        byte[] imageBytes;
        using (var stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            imageBytes = stream.ToArray();
        }

        var response = await _host.RecognizeAsync(imageBytes, settings, cancellationToken).ConfigureAwait(false);
        var sourceLines = response.Lines ?? Array.Empty<OneOcrLinePayload>();
        var lines = new List<OcrLine>(sourceLines.Length);

        foreach (var line in sourceLines)
        {
            var rect = ToRect(line.Bbox);
            if (rect is null || rect.Value.Width <= 0 || rect.Value.Height <= 0)
            {
                continue;
            }

            var words = line.Words ?? Array.Empty<OneOcrWordPayload>();
            var wordConfidences = words
                .Select(word => word.Confidence)
                .Where(confidence => double.IsFinite(confidence))
                .ToArray();
            var confidence = wordConfidences.Length > 0
                ? (float)Math.Clamp(wordConfidences.Average(), 0.0, 1.0)
                : 1.0f;

            lines.Add(new OcrLine(
                line.Text ?? string.Empty,
                rect.Value,
                confidence,
                1,
                rect.Value.Height));
        }

        _logger?.Info($"stage=oneocr event=provider_complete duration_ms={response.DurationMs:0.###} lines={lines.Count}.");
        return new OcrResultModel(lines, response.ImageWidth, response.ImageHeight);
    }

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Rect? ToRect(double[]? bbox)
    {
        if (bbox is null || bbox.Length < 4)
        {
            return null;
        }

        if (!double.IsFinite(bbox[0]) ||
            !double.IsFinite(bbox[1]) ||
            !double.IsFinite(bbox[2]) ||
            !double.IsFinite(bbox[3]))
        {
            return null;
        }

        return new Rect(bbox[0], bbox[1], bbox[2], bbox[3]);
    }
}
