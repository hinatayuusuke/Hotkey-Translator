using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class LlamaHostSettingsRule : ISettingsRule
{
    public string RuleId => "llama_host";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = SettingsHostNormalizer.NormalizeLlamaSettings(settings);
        if (changed)
        {
            report.Add(RuleId, "Llama host settings were normalized.");
        }

        return changed;
    }
}
