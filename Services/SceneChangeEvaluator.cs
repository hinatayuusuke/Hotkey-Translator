using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public readonly record struct SceneChangeEvaluation(bool CanEvaluate, double Score, string? SkipReason);

public sealed class SceneChangeEvaluator
{
    private const double AreaPower = 0.7;
    private const double TextPower = 0.3;
    private const double CharWeight = 10.0;
    private const double LineWeight = 1.0;
    private const double RoiTolerance = 0.5;
    private readonly PhashService _phashService;

    public SceneChangeEvaluator(PhashService phashService)
    {
        _phashService = phashService;
    }

    public SceneChangeEvaluation Evaluate(
        Bitmap currentRoi,
        Rect roiScreen,
        Bitmap? lastRoiSnapshot,
        Rect? lastRoiBounds,
        IReadOnlyList<OverlayItem> lastOverlayItems,
        AppSettings settings)
    {
        if (lastRoiSnapshot == null || lastRoiBounds == null)
        {
            return new SceneChangeEvaluation(false, 0.0, "no previous ROI snapshot.");
        }

        if (lastOverlayItems.Count == 0)
        {
            return new SceneChangeEvaluation(false, 0.0, "no previous overlay items.");
        }

        if (!AreRoiBoundsCompatible(lastRoiBounds.Value, roiScreen))
        {
            return new SceneChangeEvaluation(false, 0.0,
                $"ROI bounds changed (prev={FormatRect(lastRoiBounds.Value)} current={FormatRect(roiScreen)}).");
        }

        var maxArea = 0.0;
        var maxTextScore = 0.0;
        var textScores = new double[lastOverlayItems.Count];
        for (var i = 0; i < lastOverlayItems.Count; i++)
        {
            var item = lastOverlayItems[i];
            var area = Math.Max(0.0, item.Rect.Width * item.Rect.Height);
            maxArea = Math.Max(maxArea, area);
            var textScore = GetTextScore(item);
            textScores[i] = textScore;
            maxTextScore = Math.Max(maxTextScore, textScore);
        }

        if (maxArea <= 0)
        {
            return new SceneChangeEvaluation(false, 0.0, "overlay areas are empty.");
        }

        var roiLocal = new Rect(0, 0, currentRoi.Width, currentRoi.Height);
        var weightedSum = 0.0;
        var weightSum = 0.0;
        for (var i = 0; i < lastOverlayItems.Count; i++)
        {
            var item = lastOverlayItems[i];
            var localRect = new Rect(
                item.Rect.X - roiScreen.X,
                item.Rect.Y - roiScreen.Y,
                item.Rect.Width,
                item.Rect.Height);
            var clip = Rect.Intersect(roiLocal, localRect);
            if (clip.IsEmpty || clip.Width <= 1 || clip.Height <= 1)
            {
                continue;
            }

            var area = clip.Width * clip.Height;
            var areaNorm = Math.Clamp(area / maxArea, 0.0, 1.0);
            var weight = Math.Pow(areaNorm, AreaPower);
            if (settings.EnableSceneChangeTextWeighted)
            {
                var textNorm = maxTextScore > 0 ? Math.Clamp(textScores[i] / maxTextScore, 0.0, 1.0) : 0.0;
                weight *= Math.Pow(textNorm, TextPower);
            }

            if (weight <= 0)
            {
                continue;
            }

            using var currentCrop = BitmapHelper.Crop(currentRoi, clip);
            using var lastCrop = BitmapHelper.Crop(lastRoiSnapshot, clip);
            var currentHash = _phashService.ComputeHash(currentCrop);
            var lastHash = _phashService.ComputeHash(lastCrop);
            var delta = _phashService.HammingDistance(currentHash, lastHash) / 64.0;

            weightedSum += delta * weight;
            weightSum += weight;
        }

        if (weightSum <= 0)
        {
            return new SceneChangeEvaluation(false, 0.0, "no weighted overlay regions inside ROI.");
        }

        var score = Math.Clamp(weightedSum / weightSum, 0.0, 1.0);
        return new SceneChangeEvaluation(true, score, null);
    }

    private static double GetTextScore(OverlayItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            return 0.0;
        }

        var charCount = 0;
        foreach (var ch in item.Text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                charCount++;
            }
        }

        return (charCount * CharWeight) + (item.LineCount * LineWeight);
    }

    private static bool AreRoiBoundsCompatible(Rect a, Rect b)
    {
        // WHY: Capture bounds can vary by sub-pixel amounts; allow small drift.
        return Math.Abs(a.X - b.X) <= RoiTolerance &&
               Math.Abs(a.Y - b.Y) <= RoiTolerance &&
               Math.Abs(a.Width - b.Width) <= RoiTolerance &&
               Math.Abs(a.Height - b.Height) <= RoiTolerance;
    }

    private static string FormatRect(Rect rect)
    {
        return $"{rect.X:0.##},{rect.Y:0.##} {rect.Width:0.##}x{rect.Height:0.##}";
    }
}
