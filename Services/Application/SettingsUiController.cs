using System;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Settings;

namespace Hotkey_Translator.Services.Application;

internal interface ISettingsUiBridge
{
    bool IsLoaded { get; }
    bool IsApplyingSettings { get; set; }
    void ApplyRuntimeStateAfterSave(AppSettings settings);
    Task<bool> EnsureResourceHostsAsync(AppSettings settings);
    Task PersistSettingsAsync();
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

    public async Task SaveFromUiAsync()
    {
        if (_bridge.IsApplyingSettings || !_bridge.IsLoaded)
        {
            return;
        }

        var previousApplyingState = _bridge.IsApplyingSettings;
        _bridge.IsApplyingSettings = true;
        try
        {
            var settings = _settingsService.Settings;
            _applySettingsInput(settings);
            _settingsFacade.NormalizeForSave(settings);

            if (!settings.EnableSceneChangeAutoTranslate)
            {
                _bridge.ClearSceneChangeAutoTranslatePending("auto-translate disabled");
            }

            _bridge.ApplyRuntimeStateAfterSave(settings);
            await _bridge.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
            await _bridge.PersistSettingsAsync().ConfigureAwait(true);
            _bridge.AppendLog("Settings saved.");
            _bridge.TryUpdateHotkeys(settings);
            _bridge.UpdateAutoHideWatcher(settings);
        }
        finally
        {
            _bridge.IsApplyingSettings = previousApplyingState;
        }
    }
}
