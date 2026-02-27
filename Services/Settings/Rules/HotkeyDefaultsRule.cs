using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class HotkeyDefaultsRule : ISettingsRule
{
    public string RuleId => "hotkey_defaults";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(settings.HotkeyForceGeminiStrictKey))
        {
            settings.HotkeyForceGeminiStrictKey = "F10";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceGeminiStrictModifiers))
        {
            settings.HotkeyForceGeminiStrictModifiers = "Shift";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleSceneAutoTranslateKey))
        {
            settings.HotkeyToggleSceneAutoTranslateKey = "F5";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleSceneAutoTranslateModifiers))
        {
            settings.HotkeyToggleSceneAutoTranslateModifiers = "None";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeySelectRoiKey))
        {
            settings.HotkeySelectRoiKey = "F6";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyLockCaptureWindowKey))
        {
            settings.HotkeyLockCaptureWindowKey = "F7";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyUnlockCaptureWindowKey))
        {
            settings.HotkeyUnlockCaptureWindowKey = "F7";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleMirrorFullscreenKey))
        {
            settings.HotkeyToggleMirrorFullscreenKey = "F7";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleMirrorFullscreenModifiers))
        {
            settings.HotkeyToggleMirrorFullscreenModifiers = "Control";
            changed = true;
        }

        if (changed)
        {
            report.Add(RuleId, "Default hotkeys were applied.");
        }

        return changed;
    }
}
