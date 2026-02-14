using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class SmallBoxReadabilitySettingsRule : ISettingsRule
{
    public string RuleId => "small_box_readability";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        changed |= SettingsRuleHelpers.ClampSetting(settings.SmallTextThresholdPx, 8.0, 48.0, 22.0, out var smallTextThreshold);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SmallBoxMaxScale, 1.0, 3.0, 1.6, out var smallBoxMaxScale);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SmallBoxFontScaleWeight, 0.0, 1.0, 0.7, out var smallBoxFontScaleWeight);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SmallBoxSlenderAspectThreshold, 1.0, 8.0, 3.0, out var smallBoxSlenderAspectThreshold);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SmallBoxSlenderThresholdBoost, 1.0, 2.0, 1.2, out var smallBoxSlenderThresholdBoost);
        settings.SmallTextThresholdPx = smallTextThreshold;
        settings.SmallBoxMaxScale = smallBoxMaxScale;
        settings.SmallBoxFontScaleWeight = smallBoxFontScaleWeight;
        settings.SmallBoxSlenderAspectThreshold = smallBoxSlenderAspectThreshold;
        settings.SmallBoxSlenderThresholdBoost = smallBoxSlenderThresholdBoost;
        if (changed)
        {
            report.Add(RuleId, "Small-box readability values were clamped to safe ranges.");
        }

        return changed;
    }
}
