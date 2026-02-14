using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings;

internal sealed class SettingsFacade
{
    private readonly AppSettingsMigrator _migrator;
    private readonly AppSettingsValidator _validator;
    private readonly System.Func<AppLogger?> _loggerAccessor;

    public SettingsFacade(System.Func<AppLogger?> loggerAccessor)
    {
        _migrator = new AppSettingsMigrator();
        _validator = new AppSettingsValidator();
        _loggerAccessor = loggerAccessor;
    }

    public SettingsNormalizationResult NormalizeForLoad(AppSettings settings)
    {
        var migration = _migrator.Migrate(settings);
        var validation = _validator.ValidateAndNormalize(settings);
        LogReports("load", migration, validation);
        return new SettingsNormalizationResult(
            migration.HasChanges || validation.HasChanges,
            migration,
            validation);
    }

    public SettingsNormalizationResult NormalizeForSave(AppSettings settings)
    {
        var migration = _migrator.Migrate(settings);
        var validation = _validator.ValidateAndNormalize(settings);
        LogReports("save", migration, validation);
        return new SettingsNormalizationResult(
            migration.HasChanges || validation.HasChanges,
            migration,
            validation);
    }

    private void LogReports(
        string phase,
        SettingsMigrationReport migration,
        SettingsValidationReport validation)
    {
        foreach (var entry in migration.Entries)
        {
            _loggerAccessor()?.Info(
                $"stage=settings event=migrate phase={phase} rule={entry.RuleId} message=\"{entry.Message}\".");
        }

        foreach (var entry in validation.Entries)
        {
            _loggerAccessor()?.Info(
                $"stage=settings event=normalize phase={phase} rule={entry.RuleId} message=\"{entry.Message}\".");
        }
    }
}
