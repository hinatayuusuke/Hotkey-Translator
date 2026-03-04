using System;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class HotkeyCommandController
{
    private readonly Func<bool> _hasRunOnce;
    private readonly Func<Task> _runOnceAsync;
    private readonly Func<ForceRunOptions, Task> _runOnceWithOptionsAsync;
    private readonly Func<PipelineOrchestrator?> _pipelineAccessor;
    private readonly Func<OverlayTextMode> _overlayTextModeAccessor;
    private readonly Action<OverlayTextMode> _setOverlayTextMode;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<AppSettings> _syncSettingsToView;
    private readonly Action<AppSettings> _updateAutoHideWatcher;
    private readonly Action<string> _clearSceneChangeAutoTranslatePending;
    private readonly WindowBindingService _windowBindingService;
    private readonly Func<Task> _saveSettingsAsync;
    private readonly Func<Task> _selectRoiAsync;
    private readonly Func<OverlayPresenter?> _overlayPresenterAccessor;
    private readonly Func<bool> _overlayEnabledAccessor;
    private readonly Action<bool> _setOverlayEnabled;
    private readonly Func<Task> _toggleMirrorFullscreenAsync;
    private readonly Action<string> _appendLog;

    public HotkeyCommandController(
        Func<bool> hasRunOnce,
        Func<Task> runOnceAsync,
        Func<ForceRunOptions, Task> runOnceWithOptionsAsync,
        Func<PipelineOrchestrator?> pipelineAccessor,
        Func<OverlayTextMode> overlayTextModeAccessor,
        Action<OverlayTextMode> setOverlayTextMode,
        Func<AppSettings> settingsAccessor,
        Action<AppSettings> syncSettingsToView,
        Action<AppSettings> updateAutoHideWatcher,
        Action<string> clearSceneChangeAutoTranslatePending,
        WindowBindingService windowBindingService,
        Func<Task> saveSettingsAsync,
        Func<Task> selectRoiAsync,
        Func<OverlayPresenter?> overlayPresenterAccessor,
        Func<bool> overlayEnabledAccessor,
        Action<bool> setOverlayEnabled,
        Func<Task> toggleMirrorFullscreenAsync,
        Action<string> appendLog)
    {
        _hasRunOnce = hasRunOnce;
        _runOnceAsync = runOnceAsync;
        _runOnceWithOptionsAsync = runOnceWithOptionsAsync;
        _pipelineAccessor = pipelineAccessor;
        _overlayTextModeAccessor = overlayTextModeAccessor;
        _setOverlayTextMode = setOverlayTextMode;
        _settingsAccessor = settingsAccessor;
        _syncSettingsToView = syncSettingsToView;
        _updateAutoHideWatcher = updateAutoHideWatcher;
        _clearSceneChangeAutoTranslatePending = clearSceneChangeAutoTranslatePending;
        _windowBindingService = windowBindingService;
        _saveSettingsAsync = saveSettingsAsync;
        _selectRoiAsync = selectRoiAsync;
        _overlayPresenterAccessor = overlayPresenterAccessor;
        _overlayEnabledAccessor = overlayEnabledAccessor;
        _setOverlayEnabled = setOverlayEnabled;
        _toggleMirrorFullscreenAsync = toggleMirrorFullscreenAsync;
        _appendLog = appendLog;
    }

    public async Task HandleRunOnceHotkeyAsync()
    {
        EnsureTranslatedOverlayForRunHotkeys();
        if (!_hasRunOnce())
        {
            _appendLog("F8: Run once (first run).");
            await _runOnceAsync().ConfigureAwait(true);
            return;
        }

        _appendLog("F8: Run once.");
        await _runOnceAsync().ConfigureAwait(true);
    }

    public async Task HandleForceRunHotkeyAsync()
    {
        EnsureTranslatedOverlayForRunHotkeys();
        _appendLog("Force run: skip pHash, OCR diff, translation cache.");
        await _runOnceWithOptionsAsync(
                new ForceRunOptions(SkipPhash: true, SkipOcrDiff: true, SkipTranslationCache: true, SkipTranslation: false))
            .ConfigureAwait(true);
    }

    public async Task HandleForceGeminiStrictHotkeyAsync()
    {
        EnsureTranslatedOverlayForRunHotkeys();
        _appendLog("Force Gemini strict run: skip pHash, OCR diff, translation cache.");
        await _runOnceWithOptionsAsync(
                new ForceRunOptions(
                    SkipPhash: true,
                    SkipOcrDiff: true,
                    SkipTranslationCache: true,
                    SkipTranslation: false,
                    ForceGeminiStrict: true))
            .ConfigureAwait(true);
    }

    public void HandleOverlayTextHotkey()
    {
        var pipeline = _pipelineAccessor();
        if (pipeline == null)
        {
            return;
        }

        var nextMode = _overlayTextModeAccessor() == OverlayTextMode.Translated
            ? OverlayTextMode.Source
            : OverlayTextMode.Translated;

        if (!pipeline.TrySetOverlayTextMode(nextMode, out var reason))
        {
            _appendLog(reason ?? "Overlay text toggle ignored.");
            return;
        }

        _setOverlayTextMode(nextMode);
        _appendLog($"Overlay text mode: {nextMode}.");
    }

    public async Task HandleToggleSceneAutoTranslateHotkeyAsync()
    {
        var settings = _settingsAccessor();
        var nextEnabled = !settings.EnableSceneChangeAutoTranslate;
        settings.EnableSceneChangeAutoTranslate = nextEnabled;
        if (nextEnabled)
        {
            settings.EnableSceneChangeAutoHide = false;
        }
        else
        {
            _clearSceneChangeAutoTranslatePending("auto-translate disabled");
        }

        // WHY: Keep hotkey-driven toggles on the same state path as UI binding.
        _syncSettingsToView(settings);
        _updateAutoHideWatcher(settings);
        _appendLog(nextEnabled
            ? "Scene change auto-translate enabled (F5). Auto-hide disabled."
            : "Scene change auto-translate disabled (F5).");
        await _saveSettingsAsync().ConfigureAwait(true);
    }

    public async Task<FixedCaptureWindowSpec?> HandleLockCaptureWindowHotkeyAsync()
    {
        var settings = _settingsAccessor();
        if (IsVulkanEarlyInjectionModeEnabled(settings))
        {
            _appendLog("Capture window lock blocked: Vulkan early-injection launcher mode is enabled.");
            return null;
        }

        if (_windowBindingService.TryBindForegroundWindow(settings, out var spec, out var reason))
        {
            _appendLog(
                $"Capture window locked: hwnd=0x{spec.Hwnd:X} pid={spec.ProcessId} class=\"{spec.ClassName}\" title=\"{spec.WindowTitle}\".");
            await _saveSettingsAsync().ConfigureAwait(true);
            return spec;
        }

        _appendLog($"Capture window lock failed: {reason ?? "unknown"}.");
        return null;
    }

    public async Task HandleUnlockCaptureWindowHotkeyAsync()
    {
        var settings = _settingsAccessor();
        if (!settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowHandle == 0)
        {
            _appendLog("Capture window lock already cleared.");
            return;
        }

        _windowBindingService.ClearBinding(settings);
        _appendLog("Capture window unlocked.");
        await _saveSettingsAsync().ConfigureAwait(true);
    }

    public Task HandleSelectRoiHotkeyAsync()
    {
        return _selectRoiAsync();
    }

    public void HandleToggleOverlayHotkey()
    {
        var overlayPresenter = _overlayPresenterAccessor();
        if (overlayPresenter == null)
        {
            return;
        }

        var nextEnabled = !_overlayEnabledAccessor();
        _setOverlayEnabled(nextEnabled);
        overlayPresenter.SetEnabled(nextEnabled);
        _appendLog(nextEnabled ? "Overlay shown." : "Overlay hidden.");
    }

    public Task HandleToggleMirrorFullscreenHotkeyAsync()
    {
        return _toggleMirrorFullscreenAsync();
    }

    private void EnsureTranslatedOverlayForRunHotkeys()
    {
        var pipeline = _pipelineAccessor();
        if (pipeline == null || _overlayTextModeAccessor() == OverlayTextMode.Translated)
        {
            return;
        }

        if (pipeline.TrySetOverlayTextMode(OverlayTextMode.Translated, out _, allowModeUpdateWithoutData: true))
        {
            _setOverlayTextMode(OverlayTextMode.Translated);
        }
    }

    private static bool IsVulkanEarlyInjectionModeEnabled(AppSettings settings)
    {
        return settings.EnableGraphicsHookPipeline &&
               settings.EnableVulkanEarlyInjectionLauncher &&
               settings.GraphicsHookApi == GraphicsHookApiKind.Vulkan;
    }
}
