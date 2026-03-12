using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.Rules;

internal sealed class ResourceHostBudgetSettingsRule : ISettingsRule
{
    public string RuleId => "resource_host_budget";

    public bool Apply(AppSettings settings, SettingsValidationReport report)
    {
        var normalized = Enum.IsDefined(typeof(GraphicsResourceBudgetProfile), settings.ResourceBudgetProfile)
            ? settings.ResourceBudgetProfile
            : GraphicsResourceBudgetProfile.Balanced;
        if (normalized == settings.ResourceBudgetProfile)
        {
            return false;
        }

        settings.ResourceBudgetProfile = normalized;
        report.Add(RuleId, "Resource host budget profile was normalized.");
        return true;
    }
}
