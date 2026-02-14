using System.Collections.Generic;

namespace Hotkey_Translator.Services.Settings;

internal sealed class SettingsMigrationReport
{
    private readonly List<SettingsReportEntry> _entries = new();

    public bool HasChanges => _entries.Count > 0;

    public IReadOnlyList<SettingsReportEntry> Entries => _entries;

    public void Add(string ruleId, string message)
    {
        _entries.Add(new SettingsReportEntry(ruleId, message));
    }
}
