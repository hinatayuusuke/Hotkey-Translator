using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class SceneSemanticSettingsRule : ISettingsRule
{
    public string RuleId => "scene_semantic";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        changed |= SettingsRuleHelpers.ClampSetting(settings.SceneSemanticBlockIouThreshold, 0.1, 0.95, 0.5, out var semanticIou);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SceneSemanticMinChars, 0, 64, 2, out var semanticMinChars);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SceneSemanticRequireConfirmTicks, 1, 5, 1, out var semanticConfirmTicks);
        changed |= SettingsRuleHelpers.ClampSetting(settings.SceneChangeQuietWindowMs, 100, 3000, 450, out var quietWindowMs);
        settings.SceneSemanticBlockIouThreshold = semanticIou;
        settings.SceneSemanticMinChars = semanticMinChars;
        settings.SceneSemanticRequireConfirmTicks = semanticConfirmTicks;
        settings.SceneChangeQuietWindowMs = quietWindowMs;
        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized IoU={semanticIou:0.##}, MinChars={semanticMinChars}, ConfirmTicks={semanticConfirmTicks}, QuietWindowMs={quietWindowMs}.");
        }

        return changed;
    }
}
