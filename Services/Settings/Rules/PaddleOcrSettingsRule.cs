using System;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class PaddleOcrSettingsRule : ISettingsRule
{
    public string RuleId => "paddle_ocr";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        changed |= SettingsRuleHelpers.ClampSetting(settings.PaddleTextDetThresh, 0.0, 1.0, 0.5, out var textDetThresh);
        changed |= SettingsRuleHelpers.ClampSetting(settings.PaddleTextDetBoxThresh, 0.0, 1.0, 0.68, out var textDetBoxThresh);
        changed |= SettingsRuleHelpers.ClampSetting(settings.PaddleTextDetUnclipRatio, 0.5, 3.0, 1.3, out var textDetUnclipRatio);
        changed |= SettingsRuleHelpers.ClampSetting(settings.PaddleTextRecScoreThresh, 0.0, 1.0, 0.58, out var textRecScoreThresh);
        settings.PaddleTextDetThresh = textDetThresh;
        settings.PaddleTextDetBoxThresh = textDetBoxThresh;
        settings.PaddleTextDetUnclipRatio = textDetUnclipRatio;
        settings.PaddleTextRecScoreThresh = textRecScoreThresh;
        if (settings.EnablePaddleConfidenceFilter)
        {
            // COMPAT: Keep one confidence gate path to avoid double-filtering with text_rec_score_thresh.
            settings.EnablePaddleConfidenceFilter = false;
            changed = true;
        }

        var trimmedDetectionModel = settings.PaddleTextDetectionModelName?.Trim();
        if (!PaddleModelResolver.IsSupportedDetectionModel(trimmedDetectionModel))
        {
            // WHY: No legacy alias mapping; accept only current UI-supported model ids.
            settings.PaddleTextDetectionModelName = PaddleModelResolver.DefaultDetectionModel;
            changed = true;
        }
        else if (!string.Equals(settings.PaddleTextDetectionModelName, trimmedDetectionModel, StringComparison.Ordinal))
        {
            settings.PaddleTextDetectionModelName = trimmedDetectionModel!;
            changed = true;
        }

        var trimmedRecognitionModel = settings.PaddleTextRecognitionModelName?.Trim();
        if (!PaddleModelResolver.IsSupportedRecognitionModel(trimmedRecognitionModel))
        {
            // WHY: No legacy alias mapping; unknown values fall back to the stable default.
            settings.PaddleTextRecognitionModelName = PaddleModelResolver.DefaultRecognitionModel;
            changed = true;
        }
        else if (!string.Equals(settings.PaddleTextRecognitionModelName, trimmedRecognitionModel, StringComparison.Ordinal))
        {
            settings.PaddleTextRecognitionModelName = trimmedRecognitionModel!;
            changed = true;
        }

        var pipelineVersion = (settings.PaddleVlPipelineVersion ?? string.Empty).Trim();
        if (!string.Equals(pipelineVersion, "v1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pipelineVersion, "v1.5", StringComparison.OrdinalIgnoreCase))
        {
            settings.PaddleVlPipelineVersion = "v1.5";
            changed = true;
        }
        else
        {
            var normalized = string.Equals(pipelineVersion, "v1", StringComparison.OrdinalIgnoreCase) ? "v1" : "v1.5";
            if (!string.Equals(settings.PaddleVlPipelineVersion, normalized, StringComparison.Ordinal))
            {
                settings.PaddleVlPipelineVersion = normalized;
                changed = true;
            }
        }

        if (settings.PaddleVlMaxPixels.HasValue && settings.PaddleVlMaxPixels.Value <= 0)
        {
            settings.PaddleVlMaxPixels = null;
            changed = true;
        }

        if (settings.PaddleVlLayoutThreshold.HasValue)
        {
            var clamped = Math.Clamp(settings.PaddleVlLayoutThreshold.Value, 0.0, 1.0);
            if (Math.Abs(clamped - settings.PaddleVlLayoutThreshold.Value) > 0.0001)
            {
                settings.PaddleVlLayoutThreshold = clamped;
                changed = true;
            }
        }

        if (settings.PaddleVlLayoutUnclipRatio.HasValue)
        {
            var value = settings.PaddleVlLayoutUnclipRatio.Value;
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            {
                // WHY: invalid unclip ratio can break detector post-processing; use native default via null.
                settings.PaddleVlLayoutUnclipRatio = null;
                changed = true;
            }
        }

        if (settings.PaddleVlLayoutMergeBboxesIouThreshold.HasValue)
        {
            var value = settings.PaddleVlLayoutMergeBboxesIouThreshold.Value;
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0 || value > 1.0)
            {
                // WHY: out-of-range IoU threshold has undefined behavior across releases; fallback to native default.
                settings.PaddleVlLayoutMergeBboxesIouThreshold = null;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.PaddleVlLayoutMergeBboxesMode))
        {
            if (settings.PaddleVlLayoutMergeBboxesMode is not null)
            {
                settings.PaddleVlLayoutMergeBboxesMode = null;
                changed = true;
            }
        }
        else
        {
            var normalizedMode = settings.PaddleVlLayoutMergeBboxesMode.Trim().ToLowerInvariant();
            if (normalizedMode is not ("small" or "large" or "union"))
            {
                // WHY: keep unsupported mode values from reaching PaddleOCR-VL runtime.
                settings.PaddleVlLayoutMergeBboxesMode = null;
                changed = true;
            }
            else if (!string.Equals(settings.PaddleVlLayoutMergeBboxesMode, normalizedMode, StringComparison.Ordinal))
            {
                settings.PaddleVlLayoutMergeBboxesMode = normalizedMode;
                changed = true;
            }
        }

        if (settings.PaddleVlMaxNewTokens.HasValue)
        {
            var clamped = Math.Clamp(settings.PaddleVlMaxNewTokens.Value, 512, 4096);
            if (clamped != settings.PaddleVlMaxNewTokens.Value)
            {
                settings.PaddleVlMaxNewTokens = clamped;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.PaddleVlDevice))
        {
            settings.PaddleVlDevice = "gpu:0";
            changed = true;
        }
        else
        {
            var trimmed = settings.PaddleVlDevice.Trim();
            if (!string.Equals(trimmed, settings.PaddleVlDevice, StringComparison.Ordinal))
            {
                settings.PaddleVlDevice = trimmed;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.PaddleVlPrecision))
        {
            settings.PaddleVlPrecision = "fp32";
            changed = true;
        }
        else
        {
            var precision = settings.PaddleVlPrecision.Trim().ToLowerInvariant();
            if (precision is not ("fp16" or "fp32"))
            {
                settings.PaddleVlPrecision = "fp32";
                changed = true;
            }
            else if (!string.Equals(settings.PaddleVlPrecision, precision, StringComparison.Ordinal))
            {
                settings.PaddleVlPrecision = precision;
                changed = true;
            }
        }

        if (settings.PaddleVlDevice.StartsWith("cpu", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.PaddleVlPrecision, "fp16", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: fp16 on CPU backends is unstable/non-portable; keep first-run and manual edits safe.
            settings.PaddleVlPrecision = "fp32";
            changed = true;
        }

        if (changed)
        {
            report.Add(RuleId, "Paddle OCR/VL settings were normalized.");
        }

        return changed;
    }
}
