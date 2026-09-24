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
    private readonly Func<AppSettings, CancellationToken, Task> _repairOneOcr;
    private readonly Action _clearPendingSceneAutoTranslate;
    private bool _suspendOneOcrAutoRuns;

    private CancellationTokenSource? _runCts;
    private int _runInProgress;
    private int _showCenterBusyForCurrentRun;
    private bool _hasRunOnce;

    public MainWindowRunCoordinator(
        SettingsService settingsService,
        IMainWindowViewBridge viewBridge,
        Func<PipelineOrchestrator?> pipelineAccessor,
        WinRtOcrLanguagePackCoordinator winRtLanguagePackCoordinator,
        Func<bool> tryDrainPendingSceneAutoTranslate,
        Func<AppSettings, CancellationToken, Task> repairOneOcr,
        Action clearPendingSceneAutoTranslate)
    {
        _settingsService = settingsService;
        _viewBridge = viewBridge;
        _pipelineAccessor = pipelineAccessor;
        _winRtLanguagePackCoordinator = winRtLanguagePackCoordinator;
        _tryDrainPendingSceneAutoTranslate = tryDrainPendingSceneAutoTranslate;
        _repairOneOcr = repairOneOcr;
        _clearPendingSceneAutoTranslate = clearPendingSceneAutoTranslate;
    }

    public bool HasRunOnce => _hasRunOnce;

    public bool IsRunning => Interlocked.CompareExchange(ref _runInProgress, 1, 1) == 1;

    public bool IsOneOcrAutoRunSuspended => _suspendOneOcrAutoRuns &&
        OneOcrVendorUiController.IsOneOcrRequired(_settingsService.Settings);

    public async Task ReportOneOcrFailureAsync()
    {
        // WHY: Semantic scene OCR can fail outside a translation run; share the same prompt/run gate.
        if (IsOneOcrAutoRunSuspended || Interlocked.Exchange(ref _runInProgress, 1) == 1) return;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var runToken = _runCts.Token;
        try
        {
            await HandleOneOcrFailureAsync(_settingsService.Settings, runToken).ConfigureAwait(true);
        }
        finally
        {
            _clearPendingSceneAutoTranslate();
            Interlocked.Exchange(ref _runInProgress, 0);
        }
    }

    private async Task HandleOneOcrFailureAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        _suspendOneOcrAutoRuns = true;
        _clearPendingSceneAutoTranslate();
        _viewBridge.HideLoadingSpinnerForRun();
        _viewBridge.CancelTranslationOverlay();
        _viewBridge.SetBusyOverlayCancelable(false);
        _viewBridge.SetBusyOverlay(false, null);
        // WHY: Keep the run gate held throughout the modal prompt and repair to prevent duplicate requests.
        await _repairOneOcr(settings, cancellationToken).ConfigureAwait(true);
    }

    public bool ShouldShowCenterBusyForCurrentRun =>
        Interlocked.CompareExchange(ref _showCenterBusyForCurrentRun, 0, 0) == 1;

    public void CancelCurrentRun()
    {
        if (!IsRunning)
        {
            return;
        }

        _viewBridge.AppendLog("Cancel requested: current OCR run will stop.");
        _runCts?.Cancel();
    }

    public async Task RunOnceAsync(ForceRunOptions options)
    {
        // NOTE: Background scene changes must not reopen the repair prompt or replay a failed capture.
        // A deliberate manual run resumes automatic translation after recovery.
        if (_suspendOneOcrAutoRuns && options.Trigger == RunTrigger.AutoSceneChange &&
            OneOcrVendorUiController.IsOneOcrRequired(_settingsService.Settings)) return;

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
        var runToken = _runCts.Token;
        Interlocked.Exchange(ref _showCenterBusyForCurrentRun, showCenterBusyForCurrentRun ? 1 : 0);
        if (showCenterBusyForCurrentRun)
        {
            _viewBridge.SetBusyOverlay(true, isFirstRun ? "Initializing OCR..." : "OCR running...");
            _viewBridge.SetBusyOverlayCancelable(true);
        }
        if (!options.SuppressTransientUiFeedback)
        {
            _viewBridge.ShowLoadingSpinnerForRun(settings);
        }

        try
        {
            var languagePackResult = await _winRtLanguagePackCoordinator
                .EnsureLanguagePackAsync(settings, runToken)
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

            await pipeline.RunOnceAsync(runToken, options).ConfigureAwait(true);
            if (!runToken.IsCancellationRequested && options.Trigger == RunTrigger.Manual)
            {
                _suspendOneOcrAutoRuns = false;
            }
        }
        catch (OneOcrUnavailableException)
        {
            await HandleOneOcrFailureAsync(settings, runToken).ConfigureAwait(true);
        }
        finally
        {
            if (!options.SuppressTransientUiFeedback)
            {
                _viewBridge.HideLoadingSpinnerForRun();
            }

            if (showCenterBusyForCurrentRun)
            {
                _viewBridge.SetBusyOverlayCancelable(false);
                _viewBridge.SetBusyOverlay(false, null);
            }

            Interlocked.Exchange(ref _showCenterBusyForCurrentRun, 0);
            Interlocked.Exchange(ref _runInProgress, 0);
            _viewBridge.CancelTranslationOverlay();
            if (_suspendOneOcrAutoRuns) _clearPendingSceneAutoTranslate();
            else _tryDrainPendingSceneAutoTranslate();
        }
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = null;
    }
}
