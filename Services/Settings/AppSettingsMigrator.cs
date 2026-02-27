using System;
using System.Windows.Input;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal sealed class AppSettingsMigrator
{
    public SettingsMigrationReport Migrate(AppSettings settings)
    {
        var report = new SettingsMigrationReport();

        // COMPAT: Legacy defaults (F12 / Shift+F12) are migrated to F7 to avoid common overlay/debug-tool conflicts.
        var lockKey = (settings.HotkeyLockCaptureWindowKey ?? string.Empty).Trim();
        var unlockKey = (settings.HotkeyUnlockCaptureWindowKey ?? string.Empty).Trim();
        var lockModifiers = ParseModifiers(settings.HotkeyLockCaptureWindowModifiers);
        var unlockModifiers = ParseModifiers(settings.HotkeyUnlockCaptureWindowModifiers);
        if (lockKey.Equals("F12", StringComparison.OrdinalIgnoreCase) &&
            lockModifiers == ModifierKeys.None &&
            unlockKey.Equals("F12", StringComparison.OrdinalIgnoreCase) &&
            unlockModifiers == ModifierKeys.Shift)
        {
            settings.HotkeyLockCaptureWindowKey = "F7";
            settings.HotkeyLockCaptureWindowModifiers = "None";
            settings.HotkeyUnlockCaptureWindowKey = "F7";
            settings.HotkeyUnlockCaptureWindowModifiers = "Shift";
            report.Add("compat_hotkey_f12_to_f7", "Migrated lock/unlock hotkeys from F12 to F7.");
        }

        if (settings.EnableSceneChangeAutoHide && settings.EnableSceneChangeAutoTranslate)
        {
            // COMPAT: Legacy settings may have both enabled; keep auto-hide as the fixed priority.
            settings.EnableSceneChangeAutoTranslate = false;
            report.Add("compat_scene_mode_priority", "Disabled scene auto-translate because auto-hide is enabled.");
        }

        return report;
    }

    private static ModifierKeys ParseModifiers(string value)
    {
        return Enum.TryParse(value, true, out ModifierKeys parsed) ? parsed : ModifierKeys.None;
    }
}
