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
            settings.HotkeyForceGeminiStrictModifiers = "Alt";
            changed = true;
        }

        if (string.Equals(settings.HotkeyForceGeminiStrictKey, "F10", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(settings.HotkeyForceGeminiStrictModifiers, "Shift", StringComparison.OrdinalIgnoreCase))
        {
            // WHY: Shift+F10 is reserved for ROI-slot force runs, so move the legacy strict shortcut aside.
            settings.HotkeyForceGeminiStrictModifiers = "Alt";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyRunNextRoiKey))
        {
            settings.HotkeyRunNextRoiKey = "F8";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyRunNextRoiModifiers))
        {
            settings.HotkeyRunNextRoiModifiers = "Shift";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyRunNextNextRoiKey))
        {
            settings.HotkeyRunNextNextRoiKey = "F8";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyRunNextNextRoiModifiers))
        {
            settings.HotkeyRunNextNextRoiModifiers = "Control";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceRunNextRoiKey))
        {
            settings.HotkeyForceRunNextRoiKey = "F10";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceRunNextRoiModifiers))
        {
            settings.HotkeyForceRunNextRoiModifiers = "Shift";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceRunNextNextRoiKey))
        {
            settings.HotkeyForceRunNextNextRoiKey = "F10";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceRunNextNextRoiModifiers))
        {
            settings.HotkeyForceRunNextNextRoiModifiers = "Control";
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

        if (string.IsNullOrWhiteSpace(settings.HotkeyNextRoiPresetKey))
        {
            settings.HotkeyNextRoiPresetKey = "F6";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyNextRoiPresetModifiers))
        {
            settings.HotkeyNextRoiPresetModifiers = "Shift";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyPreviousRoiPresetKey))
        {
            settings.HotkeyPreviousRoiPresetKey = "F6";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyPreviousRoiPresetModifiers))
        {
            settings.HotkeyPreviousRoiPresetModifiers = "Control";
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
