using System.Collections.Generic;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class RoiPresetSettingsRule : ISettingsRule
{
    private const int SlotCount = 10;

    public string RuleId => "roi_preset_settings";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        var presets = settings.RoiPresets ?? new List<RoiPreset>();
        if (!ReferenceEquals(settings.RoiPresets, presets))
        {
            settings.RoiPresets = presets;
            changed = true;
        }

        if (presets.Count != SlotCount)
        {
            changed = true;
        }

        var normalizedPresets = new List<RoiPreset>(SlotCount);
        for (var i = 0; i < SlotCount; i++)
        {
            var existing = i < presets.Count ? presets[i] : null;
            var normalized = existing?.NormalizedRoi;
            if (normalized is { } roi)
            {
                var clamped = roi.Clamp();
                if (clamped.IsEmpty)
                {
                    normalized = null;
                    changed = true;
                }
                else if (!clamped.Equals(roi))
                {
                    normalized = clamped;
                    changed = true;
                }
            }

            var enableRoi = existing?.EnableRoi ?? true;
            if (existing?.SlotIndex != i)
            {
                changed = true;
            }

            normalizedPresets.Add(new RoiPreset
            {
                SlotIndex = i,
                NormalizedRoi = normalized,
                EnableRoi = enableRoi
            });
        }

        settings.RoiPresets = normalizedPresets;

        changed |= SettingsRuleHelpers.ClampSetting(settings.ActiveRoiPresetIndex, 0, SlotCount - 1, 0, out var activeIndex);
        settings.ActiveRoiPresetIndex = activeIndex;

        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized ROI preset settings: slots={SlotCount}, active={settings.ActiveRoiPresetIndex}.");
        }

        return changed;
    }
}
