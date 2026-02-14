namespace Hotkey_Translator.Services.Settings;

internal readonly record struct SettingsNormalizationResult(
    bool Changed,
    SettingsMigrationReport MigrationReport,
    SettingsValidationReport ValidationReport);
