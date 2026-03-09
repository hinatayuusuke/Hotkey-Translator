using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Orchestration.Stages;

internal readonly record struct OcrAndGroupStageOutput(
    OcrResultModel OcrResult,
    Bitmap OcrInput,
    IReadOnlyList<OcrLine> GroupedLines,
    IReadOnlyList<ReadingUnit> ReadingUnits,
    long OcrElapsedMs,
    long GroupElapsedMs,
    int RawLineCount,
    int FilteredLineCount,
    double? PaddleConfidenceThreshold);

internal sealed class OcrAndGroupStage
{
    private readonly OcrPreprocessCoordinator _ocrPreprocessCoordinator;
    private readonly OcrLineGrouper _lineGrouper;
    private readonly ReadingUnitBuilder _readingUnitBuilder;

    public OcrAndGroupStage(
        OcrPreprocessCoordinator ocrPreprocessCoordinator,
        OcrLineGrouper lineGrouper,
        ReadingUnitBuilder readingUnitBuilder)
    {
        _ocrPreprocessCoordinator = ocrPreprocessCoordinator;
        _lineGrouper = lineGrouper;
        _readingUnitBuilder = readingUnitBuilder;
    }

    public async Task<OcrAndGroupStageOutput> ExecuteAsync(
        Bitmap roiBitmap,
        Rect roiScreen,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var ocrStopwatch = Stopwatch.StartNew();
        var passResult = await _ocrPreprocessCoordinator.RunAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
        ocrStopwatch.Stop();

        var ocrResult = passResult.Result;
        var rawLines = ocrResult.Lines;
        var filteredLines = rawLines;
        double? confidenceThreshold = null;
        if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleConfidenceFilter)
        {
            confidenceThreshold = Math.Clamp(settings.PaddleConfidenceThreshold, 0.0, 1.0);
            filteredLines = filteredLines
                .Where(line => line.Confidence >= confidenceThreshold.Value)
                .ToList();
        }

        var mappedLines = filteredLines
            .Select(line => line with
            {
                Rect = new Rect(
                    line.Rect.X + roiScreen.X,
                    line.Rect.Y + roiScreen.Y,
                    line.Rect.Width,
                    line.Rect.Height)
            })
            .ToList();

        var groupStopwatch = Stopwatch.StartNew();
        // WHY: PaddleOCR-VL and VisionLLM already return coarse blocks or synthesized lines; extra merge can over-merge.
        var groupedLines = settings.OcrEngine is OcrEngineKind.PaddleVllm or OcrEngineKind.VisionLlm
            ? mappedLines
            : _lineGrouper.MergeLines(mappedLines, settings).ToList();
        var readingUnits = _readingUnitBuilder.Build(groupedLines, settings).ToList();
        groupStopwatch.Stop();

        return new OcrAndGroupStageOutput(
            ocrResult,
            passResult.Input,
            groupedLines,
            readingUnits,
            ocrStopwatch.ElapsedMilliseconds,
            groupStopwatch.ElapsedMilliseconds,
            rawLines.Count,
            filteredLines.Count,
            confidenceThreshold);
    }
}
