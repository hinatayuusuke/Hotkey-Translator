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

        if (string.IsNullOrWhiteSpace(settings.MagpieCorePath))
        {
            settings.MagpieCorePath = "Tools\\Magpie\\Magpie.Core.exe";
            changed = true;
        }
        else
        {
            var normalizedPath = settings.MagpieCorePath.Trim();
            if (!string.Equals(normalizedPath, settings.MagpieCorePath, System.StringComparison.Ordinal))
            {
                settings.MagpieCorePath = normalizedPath;
                changed = true;
            }
        }

        if (settings.EnableMirrorFullscreenMode && settings.EnableDx11HookPipeline)
        {
            // WHY: Mirror mode is explicit priority to avoid ambiguous dual-output behavior.
            settings.EnableDx11HookPipeline = false;
            changed = true;
        }

        if (changed)
        {
            report.Add(
                RuleId,
                $"Normalized mirror settings: enabled={settings.EnableMirrorFullscreenMode}, profile={settings.MagpieProfileIndex}, core=\"{settings.MagpieCorePath}\".");
        }

        return changed;
    }
}
