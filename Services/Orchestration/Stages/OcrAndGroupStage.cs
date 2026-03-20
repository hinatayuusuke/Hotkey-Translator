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
    private readonly VisionGeometryHybridAligner _visionGeometryHybridAligner;
    private readonly AppLogger? _logger;

    public OcrAndGroupStage(
        OcrPreprocessCoordinator ocrPreprocessCoordinator,
        OcrLineGrouper lineGrouper,
        ReadingUnitBuilder readingUnitBuilder,
        AppLogger? logger = null)
    {
        _ocrPreprocessCoordinator = ocrPreprocessCoordinator;
        _lineGrouper = lineGrouper;
        _readingUnitBuilder = readingUnitBuilder;
        _visionGeometryHybridAligner = new VisionGeometryHybridAligner(logger);
        _logger = logger;
    }

    public async Task<OcrAndGroupStageOutput> ExecuteAsync(
        Bitmap roiBitmap,
        Rect roiScreen,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var ocrStopwatch = Stopwatch.StartNew();
        OcrPassResult? passResult = null;
        OcrPassResult? geometryPass = null;
        var effectiveEngineKind = settings.OcrEngine;
        var primaryWasVision = settings.OcrEngine == OcrEngineKind.VisionLlm;

        if (primaryWasVision)
        {
            try
            {
                passResult = await _ocrPreprocessCoordinator.RunVisionTextAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                passResult = default;
                if (settings.EnableVisionGeometryHybridOcr)
                {
                    try
                    {
                        geometryPass = await _ocrPreprocessCoordinator.RunVisionGeometryAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
                        if (geometryPass is not null && geometryPass.Result.Lines.Count > 0)
                        {
                            _logger?.Error(ex, "VisionLLM OCR failed; using geometry OCR fallback.");
                            passResult = geometryPass;
                            geometryPass = null;
                            effectiveEngineKind = ResolveVisionGeometryEngineKind(settings);
                            primaryWasVision = false;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception geometryEx)
                    {
                        _logger?.Error(geometryEx, "Geometry fallback after VisionLLM failure also failed.");
                    }
                }

                if (passResult is null)
                {
                    _logger?.Error(ex, "VisionLLM OCR failed; falling back to WinRT.");
                    passResult = await _ocrPreprocessCoordinator.RunWinRtAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
                    effectiveEngineKind = OcrEngineKind.WinRt;
                    primaryWasVision = false;
                }
            }
        }
        else
        {
            passResult = await _ocrPreprocessCoordinator.RunAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
        }

        ocrStopwatch.Stop();
        var resolvedPassResult = passResult ?? throw new InvalidOperationException("OCR pass result was not resolved.");

        try
        {
            var ocrResult = resolvedPassResult.Result;
            var rawLines = ocrResult.Lines;
            var filteredLines = ApplyConfidenceFilter(rawLines, settings, effectiveEngineKind, out var confidenceThreshold);

            var groupStopwatch = Stopwatch.StartNew();
            IReadOnlyList<OcrLine> groupedLocalLines;
            if (primaryWasVision && settings.EnableVisionGeometryHybridOcr)
            {
                try
                {
                    geometryPass ??= await _ocrPreprocessCoordinator.RunVisionGeometryAsync(roiBitmap, settings, cancellationToken).ConfigureAwait(false);
                    if (geometryPass is null)
                    {
                        _logger?.Info("stage=vision_geometry_hybrid event=geometry_skip reason=not_configured.");
                        groupedLocalLines = filteredLines;
                    }
                    else
                    {
                        var geometryEngineKind = ResolveVisionGeometryEngineKind(settings);
                        var geometryFilteredLines = ApplyConfidenceFilter(
                            geometryPass.Result.Lines,
                            settings,
                            geometryEngineKind,
                            out _);
                        // WHY: VisionLLM naturally merges visual lines into semantic sentences, so hybrid
                        // matching is more stable after the helper OCR has gone through the existing line grouper.
                        var groupedGeometryLines = _lineGrouper
                            .MergeLines(geometryFilteredLines, settings, geometryEngineKind)
                            .ToList();
                        var aligned = _visionGeometryHybridAligner.Align(
                            groupedGeometryLines,
                            filteredLines,
                            roiBitmap.Width,
                            roiBitmap.Height,
                            settings);

                        _logger?.Info(
                            $"stage=vision_geometry_hybrid event=stage_summary geometryRawLineCount={geometryPass.Result.Lines.Count} " +
                            $"groupedGeometryLineCount={groupedGeometryLines.Count} visionLineCount={filteredLines.Count} " +
                            $"assigned11={aligned.AssignedOneToOneCount} assignedN1={aligned.AssignedManyToOneCount} " +
                            $"postSplit={aligned.SplitCount} syntheticCount={aligned.SyntheticFallbackCount} mergedCount={aligned.SyntheticMergedCount}.");

                        if (aligned.Lines.Count > 0)
                        {
                            groupedLocalLines = aligned.Lines.ToList();
                        }
                        else if (groupedGeometryLines.Count > 0)
                        {
                            _logger?.Info("stage=vision_geometry_hybrid event=fallback reason=no_output.");
                            groupedLocalLines = groupedGeometryLines;
                        }
                        else
                        {
                            groupedLocalLines = filteredLines;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception geometryEx)
                {
                    _logger?.Error(geometryEx, "Vision geometry helper failed; using VisionLLM synthetic lines.");
                    groupedLocalLines = filteredLines;
                }
            }
            else
            {
                // WHY: PaddleOCR-VL and VisionLLM already return coarse blocks or synthesized lines; extra merge can over-merge.
                groupedLocalLines = effectiveEngineKind is OcrEngineKind.PaddleVllm or OcrEngineKind.VisionLlm
                    ? filteredLines
                    : _lineGrouper.MergeLines(filteredLines, settings, effectiveEngineKind).ToList();
            }

            var groupedLines = MapLinesToScreen(groupedLocalLines, roiScreen);
            var readingUnits = _readingUnitBuilder.Build(groupedLines, settings).ToList();
            groupStopwatch.Stop();

            return new OcrAndGroupStageOutput(
                ocrResult,
                resolvedPassResult.Input,
                groupedLines,
                readingUnits,
                ocrStopwatch.ElapsedMilliseconds,
                groupStopwatch.ElapsedMilliseconds,
                rawLines.Count,
                filteredLines.Count,
                confidenceThreshold);
        }
        finally
        {
            if (geometryPass is not null &&
                !ReferenceEquals(geometryPass.Input, roiBitmap) &&
                !ReferenceEquals(geometryPass.Input, resolvedPassResult.Input))
            {
                geometryPass.Input.Dispose();
            }
        }
    }

    private static IReadOnlyList<OcrLine> ApplyConfidenceFilter(
        IReadOnlyList<OcrLine> lines,
        AppSettings settings,
        OcrEngineKind effectiveEngineKind,
        out double? confidenceThreshold)
    {
        confidenceThreshold = null;
        if (effectiveEngineKind != OcrEngineKind.Paddle || !settings.EnablePaddleConfidenceFilter)
        {
            return lines;
        }

        var threshold = Math.Clamp(settings.PaddleConfidenceThreshold, 0.0, 1.0);
        confidenceThreshold = threshold;
        return lines
            .Where(line => line.Confidence >= threshold)
            .ToList();
    }

    private static List<OcrLine> MapLinesToScreen(IReadOnlyList<OcrLine> lines, Rect roiScreen)
    {
        return lines
            .Select(line => line with
            {
                Rect = new Rect(
                    line.Rect.X + roiScreen.X,
                    line.Rect.Y + roiScreen.Y,
                    line.Rect.Width,
                    line.Rect.Height)
            })
            .ToList();
    }

    private static OcrEngineKind ResolveVisionGeometryEngineKind(AppSettings settings)
    {
        return settings.VisionGeometryHybridBaseEngine switch
        {
            VisionGeometryHybridBaseEngineKind.WinRt => OcrEngineKind.WinRt,
            VisionGeometryHybridBaseEngineKind.Ndl => OcrEngineKind.Ndl,
            VisionGeometryHybridBaseEngineKind.Paddle => OcrEngineKind.Paddle,
            _ => OcrEngineKind.WinRt
        };
    }
}
