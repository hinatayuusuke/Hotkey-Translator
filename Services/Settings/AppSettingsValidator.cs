using System.Collections.Generic;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Settings.Rules;

namespace Hotkey_Translator.Services.Settings;

internal sealed class AppSettingsValidator
{
    private readonly IReadOnlyList<ISettingsRule> _rules;

    public AppSettingsValidator()
    {
        _rules = new ISettingsRule[]
        {
            new HotkeyDefaultsRule(),
            new RoiPresetSettingsRule(),
            new SceneSemanticSettingsRule(),
            new WritingModeSettingsRule(),
            new SmallBoxReadabilitySettingsRule(),
            new PaddleOcrSettingsRule(),
            new NdlOcrSettingsRule(),
            new VisionLlmSettingsRule(),
            new LlamaHostSettingsRule(),
            new GraphicsHookSettingsRule(),
            new MirrorModeSettingsRule()
        };
    }

    public SettingsValidationReport ValidateAndNormalize(AppSettings settings)
    {
        var report = new SettingsValidationReport();
        foreach (var rule in _rules)
        {
            _ = rule.Apply(settings, report);
        }

        return report;
    }
}

