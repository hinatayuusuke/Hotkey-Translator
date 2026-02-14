using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class CTranslate2HostSettingsRule : ISettingsRule
{
    public string RuleId => "ct2_host";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = SettingsHostNormalizer.NormalizeCTranslate2Settings(settings);
        if (changed)
        {
            report.Add(RuleId, "CTranslate2 host settings were normalized.");
        }

        return changed;
    }
}
