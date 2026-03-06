using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class WritingModeSettingsRule : ISettingsRule
{
    public string RuleId => "writing_mode";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        if (!Enum.IsDefined(typeof(VerticalModeOverride), settings.VerticalModeOverride))
        {
            settings.VerticalModeOverride = VerticalModeOverride.Auto;
            changed = true;
        }

        // COMPAT: Writing-mode behavior is now controlled by VerticalModeOverride; keep legacy toggles enabled.
        if (!settings.EnableVerticalMerge)
        {
            settings.EnableVerticalMerge = true;
            changed = true;
        }

        if (!settings.VerticalModeAutoDetect)
        {
            settings.VerticalModeAutoDetect = true;
            changed = true;
        }

        changed |= SettingsRuleHelpers.ClampSetting(settings.HorizontalMergeStrength, 0, 100, 50, out var horizontalMergeStrength);
        changed |= SettingsRuleHelpers.ClampSetting(settings.VerticalMergeStrength, 0, 100, 50, out var verticalMergeStrength);
        settings.HorizontalMergeStrength = horizontalMergeStrength;
        settings.VerticalMergeStrength = verticalMergeStrength;

        if (changed)
        {
            report.Add(RuleId, "Writing-mode compatibility toggles were normalized.");
        }

        return changed;
    }
}
