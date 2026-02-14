using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal interface ISettingsRule
{
    string RuleId { get; }

    bool Apply(AppSettings settings, SettingsValidationReport report);
}
