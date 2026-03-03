using System;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class MainWindowRunCoordinator : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly IMainWindowViewBridge _viewBridge;
    private readonly Func<PipelineOrchestrator?> _pipelineAccessor;
    private readonly WinRtOcrLanguagePackCoordinator _winRtLanguagePackCoordinator;
    private readonly Func<bool> _tryDrainPendingSceneAutoTranslate;

    private CancellationTokenSource? _runCts;
    private int _runInProgress;
    private int _showCenterBusyForCurrentRun;
    private bool _hasRunOnce;

    public MainWindowRunCoordinator(
        SettingsService settingsService,
        IMainWindowViewBridge viewBridge,
        Func<PipelineOrchestrator?> pipelineAccessor,
        WinRtOcrLanguagePackCoordinator winRtLanguagePackCoordinator,
        Func<bool> tryDrainPendingSceneAutoTranslate)
    {
        _settingsService = settingsService;
        _viewBridge = viewBridge;
        _pipelineAccessor = pipelineAccessor;
        _winRtLanguagePackCoordinator = winRtLanguagePackCoordinator;
        _tryDrainPendingSceneAutoTranslate = tryDrainPendingSceneAutoTranslate;
    }

    public bool HasRunOnce => _hasRunOnce;

    public bool IsRunning => Interlocked.CompareExchange(ref _runInProgress, 1, 1) == 1;

    public bool ShouldShowCenterBusyForCurrentRun =>
        Interlocked.CompareExchange(ref _showCenterBusyForCurrentRun, 0, 0) == 1;

    public async Task RunOnceAsync(ForceRunOptions options)
    {
        var pipeline = _pipelineAccessor();
        if (pipeline == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _runInProgress, 1) == 1)
        {
            _viewBridge.AppendLog("Run skipped: OCR already running.");
            return;
        }

        var settings = _settingsService.Settings;
        var isFirstRun = !_hasRunOnce;
        var showCenterBusyForCurrentRun = options.Trigger != RunTrigger.AutoSceneChange;
        _hasRunOnce = true;
        _viewBridge.EnableOverlay();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        Interlocked.Exchange(ref _showCenterBusyForCurrentRun, showCenterBusyForCurrentRun ? 1 : 0);
        if (showCenterBusyForCurrentRun)
        {
            _viewBridge.SetBusyOverlay(true, isFirstRun ? "Initializing OCR..." : "OCR running...");
        }
        if (!options.SuppressTransientUiFeedback)
        {
            _viewBridge.ShowLoadingSpinnerForRun(settings);
        }

        try
        {
            var languagePackResult = await _winRtLanguagePackCoordinator
                .EnsureLanguagePackAsync(settings, _runCts.Token)
                .ConfigureAwait(true);
            if (languagePackResult.Status == WinRtLanguagePackStatus.UserCanceled)
            {
                _viewBridge.AppendLog(
                    $"WinRT OCR canceled: language pack install declined ({languagePackResult.LocaleTag}).");
                return;
            }

            if (languagePackResult.Status == WinRtLanguagePackStatus.InstallFailed)
            {
                _viewBridge.AppendLog(
                    $"WinRT OCR canceled: language pack unavailable ({languagePackResult.LocaleTag}).");
                return;
            }

            await pipeline.RunOnceAsync(_runCts.Token, options).ConfigureAwait(true);
        }
        finally
        {
            if (!options.SuppressTransientUiFeedback)
            {
                _viewBridge.HideLoadingSpinnerForRun();
            }

            if (showCenterBusyForCurrentRun)
            {
                _viewBridge.SetBusyOverlay(false, null);
            }

            Interlocked.Exchange(ref _showCenterBusyForCurrentRun, 0);
            Interlocked.Exchange(ref _runInProgress, 0);
            _viewBridge.CancelTranslationOverlay();
            _tryDrainPendingSceneAutoTranslate();
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }
}
