using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class VisionLlmSettingsRule : ISettingsRule
{
    public string RuleId => "vision_llm";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var changed = SettingsHostNormalizer.NormalizeVisionLlmSettings(settings);
        if (changed)
        {
            report.Add(RuleId, "VisionLLM settings were normalized.");
        }

        return changed;
    }
}
