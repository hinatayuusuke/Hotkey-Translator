using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class MirrorModeSettingsRule : ISettingsRule
{
    public string RuleId => "mirror_mode_settings";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;

        changed |= SettingsRuleHelpers.ClampSetting(settings.MagpieProfileIndex, 0, 99, 0, out var profileIndex);
        settings.MagpieProfileIndex = profileIndex;

        if (settings.EnableMirrorFullscreenMode && settings.EnableGraphicsHookPipeline)
        {
            // WHY: Mirror mode is explicit priority to avoid ambiguous dual-output behavior.
            settings.EnableGraphicsHookPipeline = false;
            changed = true;
        }

        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized mirror settings: enabled={settings.EnableMirrorFullscreenMode}, profile={settings.MagpieProfileIndex}.");
        }

        return changed;
    }
}

