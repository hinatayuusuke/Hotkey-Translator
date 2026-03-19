using System;
using System.Text.Json;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Settings;

namespace Hotkey_Translator.Services.Application;

internal interface ISettingsUiBridge
{
    bool IsLoaded { get; }
    bool IsApplyingSettings { get; set; }
    bool HasHotkeyConflicts { get; }
    void ApplyRuntimeStateAfterSave(AppSettings settings);
    Task<ResourceBootstrapConfirmationResult> ConfirmResourceBootstrapAsync(AppSettings settings, ResourceBootstrapIntent intent);
    Task<bool> EnsureResourceHostsAsync(AppSettings settings);
    Task PersistSettingsAsync();
    bool TryValidateResourceHostBudget(AppSettings settings, out string? message);
    void SyncSettingsToView(AppSettings settings, bool updateTranslationStatus);
    void ShowLoadFailure(string message);
    void AppendLog(string message);
    void TryUpdateHotkeys(AppSettings settings);
    void UpdateAutoHideWatcher(AppSettings settings);
    void ClearSceneChangeAutoTranslatePending(string reason);
}

internal sealed class SettingsUiController
{
    private readonly SettingsService _settingsService;
    private readonly ISettingsUiBridge _bridge;
    private readonly Action<AppSettings> _applySettingsInput;
    private readonly SettingsFacade _settingsFacade;

    public SettingsUiController(
        SettingsService settingsService,
        ISettingsUiBridge bridge,
        Func<AppLogger?> loggerAccessor,
        Action<AppSettings> applySettingsInput)
    {
        _settingsService = settingsService;
        _bridge = bridge;
        _applySettingsInput = applySettingsInput;
        _settingsFacade = new SettingsFacade(loggerAccessor);
    }

    public bool NormalizeOnLoad(AppSettings settings)
    {
        var result = _settingsFacade.NormalizeForLoad(settings);
        return result.Changed;
    }

    public async Task<bool> SaveFromUiAsync()
    {
        if (_bridge.IsApplyingSettings || !_bridge.IsLoaded)
        {
            return false;
        }

        var previousApplyingState = _bridge.IsApplyingSettings;
        _bridge.IsApplyingSettings = true;
        try
        {
            if (_bridge.HasHotkeyConflicts)
            {
                // WHY: Keep the last valid hotkey set active until duplicate bindings in the editor are resolved.
                return false;
            }

            var settings = _settingsService.Settings;
            var previousSettings = CloneSettings(settings);
            _applySettingsInput(settings);
            _settingsFacade.NormalizeForSave(settings);
            if (!_bridge.TryValidateResourceHostBudget(settings, out var budgetFailureMessage))
            {
                _settingsService.ReplaceSettings(previousSettings);
                _bridge.SyncSettingsToView(previousSettings, true);
                _bridge.ShowLoadFailure(budgetFailureMessage ?? "Resource host VRAM budget exceeded.");
                _bridge.AppendLog("Settings change rejected: resource host VRAM budget exceeded.");
                return false;
            }

            if (!settings.EnableSceneChangeAutoTranslate)
            {
                _bridge.ClearSceneChangeAutoTranslatePending("auto-translate disabled");
            }

            var bootstrapConfirmation = await _bridge
                .ConfirmResourceBootstrapAsync(settings, ResourceBootstrapIntent.SettingsSave)
                .ConfigureAwait(true);
            if (!bootstrapConfirmation.Approved)
            {
                _settingsService.ReplaceSettings(previousSettings);
                _bridge.SyncSettingsToView(previousSettings, true);
                _bridge.AppendLog("Settings change canceled before resource setup/download.");
                return false;
            }

            _bridge.ApplyRuntimeStateAfterSave(settings);
            await _bridge.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
            await _bridge.PersistSettingsAsync().ConfigureAwait(true);
            _bridge.AppendLog("Settings saved.");
            _bridge.TryUpdateHotkeys(settings);
            _bridge.UpdateAutoHideWatcher(settings);
            return true;
        }
        finally
        {
            _bridge.IsApplyingSettings = previousApplyingState;
        }
    }

    private static AppSettings CloneSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings);
        var clone = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        clone.ApiKey = settings.ApiKey;
        clone.DeepLApiKey = settings.DeepLApiKey;
        return clone;
    }
}
