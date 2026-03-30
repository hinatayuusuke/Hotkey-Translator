using System;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class UiLanguageSettingsRule : ISettingsRule
{
    public string RuleId => "ui_language";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var normalized = LocalizationService.Instance.NormalizeUiLanguage(settings.UiLanguage);
        if (string.Equals(settings.UiLanguage, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        // COMPAT: Persist only supported UI language tags so malformed settings fall back to the System option.
        settings.UiLanguage = normalized;
        report.Add(RuleId, $"Normalized unsupported UI language to '{normalized}'.");
        return true;
    }
}
