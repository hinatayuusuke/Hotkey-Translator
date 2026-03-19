using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal sealed class AppSettingsMigrator
{
    public SettingsMigrationReport Migrate(AppSettings settings)
    {
        var report = new SettingsMigrationReport();

        if (settings.EnableSceneChangeAutoHide && settings.EnableSceneChangeAutoTranslate)
        {
            // COMPAT: Legacy settings may have both enabled; keep auto-hide as the fixed priority.
            settings.EnableSceneChangeAutoTranslate = false;
            report.Add("compat_scene_mode_priority", "Disabled scene auto-translate because auto-hide is enabled.");
        }

        return report;
    }
}
