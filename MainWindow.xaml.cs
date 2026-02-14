using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Application;
using Hotkey_Translator.UI;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator;

// NOTE: Global hotkey registration and Window lifecycle handling remain in View because they depend on HWND and WPF dispatcher boundaries.
public partial class MainWindow : Window, IMainWindowViewBridge, ISettingsUiBridge
{
    private readonly SettingsService _settingsService = new();
    private readonly LlamaModelCatalog _llamaModelCatalog = new();
    private readonly WindowBindingService _windowBindingService = new();
    private readonly HttpClient _httpClient = new();
    private OverlayWindow? _overlayWindow;
    private OverlayPresenter? _overlayPresenter;
    private CacheRepository? _cacheRepository;
    private CaptureManager? _captureManager;
    private PipelineOrchestrator? _pipeline;
    private OcrEngine? _ocrEngine;
    private SceneTextSnapshotService? _sceneTextSnapshotService;
    private PaddleGrpcHost? _paddleGrpcHost;
    private PaddleVlGrpcHost? _paddleVlGrpcHost;
    private CTranslate2GrpcHost? _ct2GrpcHost;
    private LlamaGrpcHost? _llamaGrpcHost;
    private readonly HotkeyController _hotkeyController;
    private readonly UiLogController _uiLogController;
    private readonly SceneChangeController _sceneChangeController;
    private readonly SettingsUiController _settingsUiController;
    private readonly SettingsChangeScheduler _settingsChangeScheduler;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly MainWindowRunCoordinator _runCoordinator;
    private PhashService? _phashService;
    private CancellationTokenSource? _translationOverlayCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private HotkeyConfig? _currentHotkeyConfig;
    private readonly ObservableCollection<string> _translationPriority = new();
    private bool _isApplyingSettings;
    private int _logLineCount;
    private const int OverlayBaselineDelayMs = 150;
    private const int LogFlushIntervalMs = 150;
    private const int MaxLogLines = 1000;
    private const int TranslationOverlayDelayMs = 200;
    private const int SettingsSaveDebounceMs = 400;
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";
    private CTranslate2HostConfig? _ct2HostConfig;
    private LlamaHostConfig? _llamaHostConfig;
    private readonly SemaphoreSlim _resourceLoadGate = new(1, 1);

    public MainWindow()
    {
        _settingsUiController = new SettingsUiController(_settingsService, this, () => _logger);
        // WHY: XAML initialization can raise ValueChanged handlers before constructor finishes.
        _settingsChangeScheduler = new SettingsChangeScheduler(
            Dispatcher,
            SaveSettingsCoreAsync,
            TimeSpan.FromMilliseconds(SettingsSaveDebounceMs),
            ex => _logger?.Error(ex, "Failed to save settings from debounce scheduler."));
        InitializeComponent();
        _mainWindowViewModel = new MainWindowViewModel(
            new SettingsViewModel(_settingsChangeScheduler),
            new RuntimeStatusViewModel(),
            RunOnceAsync,
            SelectRoiAsync,
            SwapLanguages,
            MoveTranslationPriorityUp,
            MoveTranslationPriorityDown,
            ReloadLlamaModelsAsync,
            RestartLlamaCppAsync,
            StopLlamaServerAsync,
            RestartPaddleOcrHostsAsync,
            StopPaddleVlHost,
            SaveSettingsImmediatelyAsync);
        DataContext = _mainWindowViewModel;
        _hotkeyController = new HotkeyController(this, () => _logger, FormatHotkey);
        _uiLogController = new UiLogController(Dispatcher, FlushLogPayload, LogFlushIntervalMs);
        SceneChangeController? sceneChangeController = null;
        _runCoordinator = new MainWindowRunCoordinator(
            _settingsService,
            this,
            () => _pipeline,
            () => _logger,
            () => sceneChangeController?.ConsumePendingAutoTranslatePayload(),
            () => sceneChangeController?.TryDrainPendingAutoTranslate() ?? false);
        _sceneChangeController = new SceneChangeController(
            Dispatcher,
            _settingsService,
            () => _logger,
            () => _captureManager,
            () => _phashService,
            () => _sceneTextSnapshotService,
            () => _overlayPresenter,
            GetRoiBounds,
            RunOnceAsync,
            () => _runCoordinator.IsRunning,
            AppendLog,
            enabled => _overlayEnabled = enabled);
        sceneChangeController = _sceneChangeController;
        PopulateHotkeyKeyBoxes();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger = new AppLogger(AppendLog);
        InitializeLogBuffer();
        await _settingsService.LoadAsync().ConfigureAwait(true);
        var settings = _settingsService.Settings;
        var settingsChanged = _settingsUiController.NormalizeOnLoad(settings);
        ApplySettingsToUi(settings);
        TranslationPriorityList.ItemsSource = _translationPriority;
        settingsChanged |= await EnsureResourceHostsAsync(settings).ConfigureAwait(true);
        if (settingsChanged)
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }

        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplyStyle(settings);
        _overlayPresenter = new OverlayPresenter(_overlayWindow, _logger);
        _overlayPresenter.Shown += OnOverlayShown;
        _overlayPresenter.Hidden += OnOverlayHidden;
        _overlayPresenter.Updated += OnOverlayUpdated;
        _overlayPresenter.UpdatePerfLogging(settings.EnableOcrPerfLog && settings.EnableLogging,
            settings.OcrPerfLogThresholdMs);
        _overlayPresenter.Show();

        _cacheRepository = new CacheRepository(_settingsService.CachePath);
        var frameGate = new FrameGate();
        _captureManager = new CaptureManager(frameGate, _logger);
        _ocrEngine = new OcrEngine(_httpClient, _logger);
        var ocrDiff = new OcrDiffService { IouThreshold = settings.OcrIouThreshold };
        _phashService = new PhashService();
        var normalization = new NormalizationService();
        var ocrPreprocess = new OcrPreprocessService();
        var lineGrouper = new OcrLineGrouper(_logger);
        _sceneTextSnapshotService = new SceneTextSnapshotService(_captureManager, _ocrEngine, lineGrouper, _logger);
        var keyBuilder = new CacheKeyBuilder();
        var geminiClient = new GeminiClient(_httpClient, _logger);
        var translationProviders = new List<ITranslationProvider>
        {
            new LlamaGrpcTranslationProvider(_logger),
            new DeepLTranslationProvider(_httpClient, _logger),
            new GeminiTranslationProvider(geminiClient)
        };
        var translationService = new TranslationFallbackService(translationProviders, _logger);

        _pipeline = new PipelineOrchestrator(
            _captureManager,
            _ocrEngine,
            ocrDiff,
            _phashService,
            normalization,
            ocrPreprocess,
            lineGrouper,
            _cacheRepository,
            keyBuilder,
            translationService,
            _overlayPresenter,
            _settingsService,
            _logger);
        _pipeline.OcrPreprocessPreviewReady += OnOcrPreprocessPreviewReady;
        _pipeline.TranslationStarted += OnTranslationStarted;
        _pipeline.TranslationCompleted += OnTranslationCompleted;

        InitializeHotkeys(settings);
        InitializeAutoHideWatcher(settings);
        AppendLog("Ready. F5: toggle scene auto-translate. F6: select ROI. F8: run once. F9: toggle overlay. F10: force run. Shift+F10: force Gemini strict. F11: toggle overlay text. F7: lock window. Shift+F7: unlock window.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _runCoordinator.Dispose();
        _settingsChangeScheduler.CancelPending();
        _settingsChangeScheduler.Dispose();
        _translationOverlayCts?.Cancel();
        _translationOverlayCts?.Dispose();
        _hotkeyController.Dispose();
        _uiLogController.Dispose();
        _sceneChangeController.Dispose();
        _cacheRepository?.Dispose();
        _ocrEngine?.Dispose();
        _httpClient.Dispose();
        _paddleGrpcHost?.Stop();
        _paddleVlGrpcHost?.Stop();
        _ct2GrpcHost?.Stop();
        _llamaGrpcHost?.Stop();
        if (_pipeline != null)
        {
            _pipeline.OcrPreprocessPreviewReady -= OnOcrPreprocessPreviewReady;
            _pipeline.TranslationStarted -= OnTranslationStarted;
            _pipeline.TranslationCompleted -= OnTranslationCompleted;
        }
        if (_overlayPresenter != null)
        {
            _overlayPresenter.Shown -= OnOverlayShown;
            _overlayPresenter.Hidden -= OnOverlayHidden;
            _overlayPresenter.Updated -= OnOverlayUpdated;
        }
        _overlayWindow?.Close();
    }

    private async Task<bool> EnsureResourceHostsAsync(AppSettings settings)
    {
        if (!ShouldLoadPaddle(settings) && !ShouldLoadPaddleVl(settings) &&
            !ShouldLoadCTranslate2(settings) && !ShouldLoadLlama(settings))
        {
            return false;
        }

        await _resourceLoadGate.WaitAsync().ConfigureAwait(true);
        var overlayShown = false;
        var settingsChanged = false;
        try
        {
            if (ShouldLoadPaddle(settings))
            {
                if (_paddleVlGrpcHost is { IsRunning: true })
                {
                    _logger?.Info("Stopping PaddleOCR-VL host before loading PaddleOCR.");
                    _paddleVlGrpcHost.Stop();
                }

                _paddleGrpcHost ??= new PaddleGrpcHost(_logger);
                if (_paddleGrpcHost is not { IsRunning: true })
                {
                    SetBusyOverlay(true, "Loading PaddleOCR...");
                    overlayShown = true;
                    if (!await TryStartPaddleGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        settingsChanged = true;
                    }
                }
            }
            else if (ShouldLoadPaddleVl(settings))
            {
                if (_paddleGrpcHost is { IsRunning: true })
                {
                    _logger?.Info("Stopping PaddleOCR host before loading PaddleOCR-VL.");
                    _paddleGrpcHost.Stop();
                }

                _paddleVlGrpcHost ??= new PaddleVlGrpcHost(_logger);
                if (_paddleVlGrpcHost is not { IsRunning: true })
                {
                    SetBusyOverlay(true, "Loading PaddleOCR-VL...");
                    overlayShown = true;
                    if (!await TryStartPaddleVlGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        settingsChanged = true;
                    }
                }
            }

            if (ShouldLoadCTranslate2(settings))
            {
                _ct2GrpcHost ??= new CTranslate2GrpcHost(_logger);
                var config = BuildCTranslate2HostConfig(settings);
                if (_ct2GrpcHost is { IsRunning: true })
                {
                    if (_ct2HostConfig.HasValue && !_ct2HostConfig.Value.Equals(config))
                    {
                        // NOTE: Keep the host resident until restart; apply changes on next launch.
                        _logger?.Info("CTranslate2 settings changed; reload deferred until restart.");
                    }
                }
                else
                {
                    SetBusyOverlay(true, "Loading CTranslate2...");
                    overlayShown = true;
                    if (await TryStartCTranslate2GrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        _ct2HostConfig = config;
                    }
                    else
                    {
                        settingsChanged = true;
                    }
                }
            }

            if (ShouldLoadLlama(settings))
            {
                if (_ct2GrpcHost is { IsRunning: true })
                {
                    _logger?.Info("Stopping CTranslate2 host to avoid VRAM contention with Llama.");
                    _ct2GrpcHost.Stop();
                    _ct2HostConfig = null;
                }

                _llamaGrpcHost ??= new LlamaGrpcHost(_logger);
                var config = BuildLlamaHostConfig(settings);
                if (_llamaGrpcHost is { IsRunning: true })
                {
                    if (_llamaHostConfig.HasValue && !_llamaHostConfig.Value.Equals(config))
                    {
                        // NOTE: Keep the host resident until restart; apply changes on next launch.
                        _logger?.Info("Llama settings changed; reload deferred until restart.");
                    }
                }
                else
                {
                    SetBusyOverlay(true, "Loading Llama.cpp...");
                    overlayShown = true;
                    if (await TryStartLlamaGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        _llamaHostConfig = config;
                    }
                    else
                    {
                        settingsChanged = true;
                    }
                }
            }
        }
        finally
        {
            if (overlayShown)
            {
                SetBusyOverlay(false, null);
            }
            _resourceLoadGate.Release();
        }

        return settingsChanged;
    }

    private static bool ShouldLoadPaddle(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost;
    }

    private static bool ShouldLoadPaddleVl(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost;
    }

    private static bool ShouldLoadCTranslate2(AppSettings settings)
    {
        // WHY: CTranslate2 translation path is retired; keep host disabled even if legacy settings remain.
        return false;
    }

    private static bool ShouldLoadLlama(AppSettings settings)
    {
        return settings.EnableLlamaCppTranslation;
    }

    private async Task<bool> TryStartPaddleGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _paddleGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Paddle gRPC host failed to start.");
            _paddleGrpcHost?.Stop();
            DisablePaddleOcr(settings);
            ShowLoadFailure("Failed to load PaddleOCR. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartPaddleVlGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _paddleVlGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "PaddleOCR-VL gRPC host failed to start.");
            _paddleVlGrpcHost?.Stop();
            DisablePaddleVlOcr(settings);
            ShowLoadFailure("Failed to load PaddleOCR-VL. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartCTranslate2GrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _ct2GrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "CTranslate2 gRPC host failed to start.");
            _ct2GrpcHost?.Stop();
            _ct2HostConfig = null;
            DisableCTranslate2(settings);
            ShowLoadFailure("Failed to load CTranslate2. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartLlamaGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _llamaGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Llama gRPC host failed to start.");
            _llamaGrpcHost?.Stop();
            _llamaHostConfig = null;
            DisableLlamaTranslation(settings);
            ShowLoadFailure("Failed to load Llama.cpp. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private void DisablePaddleOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _isApplyingSettings = true;
        SetComboBoxByTag(OcrEngineBox, "WinRt");
        _isApplyingSettings = false;
    }

    private void DisablePaddleVlOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _isApplyingSettings = true;
        SetComboBoxByTag(OcrEngineBox, "WinRt");
        _isApplyingSettings = false;
    }

    private void DisableCTranslate2(AppSettings settings)
    {
        settings.EnableCTranslate2 = false;
        _isApplyingSettings = true;
        if (EnableCTranslate2Check != null)
        {
            EnableCTranslate2Check.IsChecked = false;
        }
        _isApplyingSettings = false;
        UpdateTranslationStatus(settings);
    }

    private void DisableLlamaTranslation(AppSettings settings)
    {
        settings.EnableLlamaCppTranslation = false;
        _isApplyingSettings = true;
        if (EnableLlamaCppCheck != null)
        {
            EnableLlamaCppCheck.IsChecked = false;
        }
        _isApplyingSettings = false;
        UpdateTranslationStatus(settings);
    }

    private void ShowLoadFailure(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowLoadFailure(message));
            return;
        }

        MessageBox.Show(this, message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        EnsureTranslatedOverlayForRunHotkeys();
        if (!_runCoordinator.HasRunOnce)
        {
            AppendLog("F8: Run once (first run).");
            await RunOnceAsync().ConfigureAwait(true);
            return;
        }

        AppendLog("F8: Run once.");
        await RunOnceAsync().ConfigureAwait(true);
    }

    private async void OnForceRunHotkeyPressed(object? sender, EventArgs e)
    {
        EnsureTranslatedOverlayForRunHotkeys();
        AppendLog("Force run: skip pHash, OCR diff, translation cache.");
        await RunOnceAsync(new ForceRunOptions(SkipPhash: true, SkipOcrDiff: true, SkipTranslationCache: true, SkipTranslation: false))
            .ConfigureAwait(true);
    }

    private async void OnForceGeminiStrictHotkeyPressed(object? sender, EventArgs e)
    {
        EnsureTranslatedOverlayForRunHotkeys();
        AppendLog("Force Gemini strict run: skip pHash, OCR diff, translation cache.");
        await RunOnceAsync(new ForceRunOptions(
                SkipPhash: true,
                SkipOcrDiff: true,
                SkipTranslationCache: true,
                SkipTranslation: false,
                ForceGeminiStrict: true))
            .ConfigureAwait(true);
    }

    private void OnOcrOnlyHotkeyPressed(object? sender, EventArgs e)
    {
        if (_pipeline == null)
        {
            return;
        }

        var nextMode = _overlayTextMode == OverlayTextMode.Translated
            ? OverlayTextMode.Source
            : OverlayTextMode.Translated;

        if (!_pipeline.TrySetOverlayTextMode(nextMode, out var reason))
        {
            AppendLog(reason ?? "Overlay text toggle ignored.");
            return;
        }

        _overlayTextMode = nextMode;
        AppendLog($"Overlay text mode: {_overlayTextMode}.");
    }

    private async void OnToggleSceneAutoTranslateHotkeyPressed(object? sender, EventArgs e)
    {
        var settings = _settingsService.Settings;
        var nextEnabled = !settings.EnableSceneChangeAutoTranslate;
        settings.EnableSceneChangeAutoTranslate = nextEnabled;
        if (nextEnabled)
        {
            settings.EnableSceneChangeAutoHide = false;
        }
        else
        {
            ClearSceneChangeAutoTranslatePending("auto-translate disabled");
        }

        // WHY: Keep hotkey-driven toggles on the same state path as UI binding.
        _mainWindowViewModel.Settings.LoadFrom(settings);

        UpdateAutoHideWatcher(settings);
        AppendLog(nextEnabled
            ? "Scene change auto-translate enabled (F5). Auto-hide disabled."
            : "Scene change auto-translate disabled (F5).");
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    private async void OnLockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        var settings = _settingsService.Settings;
        if (_windowBindingService.TryBindForegroundWindow(settings, out var spec, out var reason))
        {
            AppendLog(
                $"Capture window locked: hwnd=0x{spec.Hwnd:X} pid={spec.ProcessId} class=\"{spec.ClassName}\" title=\"{spec.WindowTitle}\".");
            await _settingsService.SaveAsync().ConfigureAwait(true);
            return;
        }

        AppendLog($"Capture window lock failed: {reason ?? "unknown"}.");
    }

    private async void OnUnlockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        var settings = _settingsService.Settings;
        if (!settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowHandle == 0)
        {
            AppendLog("Capture window lock already cleared.");
            return;
        }

        _windowBindingService.ClearBinding(settings);
        AppendLog("Capture window unlocked.");
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    private async void OnSelectRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await SelectRoiAsync().ConfigureAwait(true);
    }

    private void EnsureTranslatedOverlayForRunHotkeys()
    {
        if (_pipeline == null || _overlayTextMode == OverlayTextMode.Translated)
        {
            return;
        }

        if (_pipeline.TrySetOverlayTextMode(OverlayTextMode.Translated, out _, allowModeUpdateWithoutData: true))
        {
            _overlayTextMode = OverlayTextMode.Translated;
        }
    }

    private void OnToggleOverlayHotkeyPressed(object? sender, EventArgs e)
    {
        if (_overlayPresenter == null)
        {
            return;
        }

        _overlayEnabled = !_overlayEnabled;
        _overlayPresenter.SetEnabled(_overlayEnabled);
        AppendLog(_overlayEnabled ? "Overlay shown." : "Overlay hidden.");
    }

    private void EnableOverlay()
    {
        if (_overlayPresenter == null || _overlayEnabled)
        {
            return;
        }

        _overlayEnabled = true;
        _overlayPresenter.SetEnabled(true, showLast: false);
        AppendLog("Overlay shown.");
    }

    private async Task RunOnceAsync()
    {
        await RunOnceAsync(ForceRunOptions.None).ConfigureAwait(true);
    }

    private async Task RunOnceAsync(ForceRunOptions options)
    {
        await RunOnceAsync(options, null).ConfigureAwait(true);
    }

    private async Task RunOnceAsync(ForceRunOptions options, SceneTextSnapshot? semanticPayload)
    {
        // WHY: Explicit actions should observe the latest UI edits before pipeline execution.
        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);
        await _runCoordinator.RunOnceAsync(options, semanticPayload).ConfigureAwait(true);
    }

    private void SetBusyOverlay(bool visible, string? message)
    {
        if (BusyOverlay == null || BusyOverlayText == null)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusyOverlay(visible, message));
            return;
        }

        BusyOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message))
        {
            BusyOverlayText.Text = message;
        }
    }

    private void ShowLoadingSpinnerForRun(AppSettings settings)
    {
        if (_overlayPresenter == null || _captureManager == null)
        {
            return;
        }

        try
        {
            _overlayPresenter.ShowLoadingSpinner(ResolveSpinnerAnchorScreenRect(settings));
        }
        catch (Exception ex)
        {
            // WHY: Spinner must never block OCR execution even if coordinate conversion fails.
            _logger?.Error(ex, "Failed to show capture loading spinner.");
        }
    }

    private void HideLoadingSpinnerForRun()
    {
        if (_overlayPresenter == null)
        {
            return;
        }

        try
        {
            _overlayPresenter.HideLoadingSpinner();
        }
        catch (Exception ex)
        {
            // WHY: Hide failures should not prevent cleanup paths from completing.
            _logger?.Error(ex, "Failed to hide capture loading spinner.");
        }
    }

    private void CancelTranslationOverlay()
    {
        _translationOverlayCts?.Cancel();
    }

    void IMainWindowViewBridge.AppendLog(string message) => AppendLog(message);
    void IMainWindowViewBridge.EnableOverlay() => EnableOverlay();
    void IMainWindowViewBridge.SetBusyOverlay(bool visible, string? message) => SetBusyOverlay(visible, message);
    void IMainWindowViewBridge.ShowLoadingSpinnerForRun(AppSettings settings) => ShowLoadingSpinnerForRun(settings);
    void IMainWindowViewBridge.HideLoadingSpinnerForRun() => HideLoadingSpinnerForRun();
    void IMainWindowViewBridge.CancelTranslationOverlay() => CancelTranslationOverlay();

    bool ISettingsUiBridge.IsLoaded => IsLoaded;

    bool ISettingsUiBridge.IsApplyingSettings
    {
        get => _isApplyingSettings;
        set => _isApplyingSettings = value;
    }

    void ISettingsUiBridge.ApplyUiInputToSettings(AppSettings settings) => ApplyUiInputToSettings(settings);

    void ISettingsUiBridge.ApplySceneChangeModeToUi(AppSettings settings)
    {
        EnableSceneChangeAutoHideCheck.IsChecked = settings.EnableSceneChangeAutoHide;
        EnableSceneChangeAutoTranslateCheck.IsChecked = settings.EnableSceneChangeAutoTranslate;
    }

    void ISettingsUiBridge.ApplyRuntimeStateAfterSave(AppSettings settings) => ApplyRuntimeStateAfterSave(settings);
    Task<bool> ISettingsUiBridge.EnsureResourceHostsAsync(AppSettings settings) => EnsureResourceHostsAsync(settings);
    Task ISettingsUiBridge.PersistSettingsAsync() => _settingsService.SaveAsync();
    void ISettingsUiBridge.AppendLog(string message) => AppendLog(message);
    void ISettingsUiBridge.TryUpdateHotkeys(AppSettings settings) => TryUpdateHotkeys(settings);
    void ISettingsUiBridge.UpdateAutoHideWatcher(AppSettings settings) => UpdateAutoHideWatcher(settings);

    void ISettingsUiBridge.ClearSceneChangeAutoTranslatePending(string reason) =>
        ClearSceneChangeAutoTranslatePending(reason);

    private Rect ResolveSpinnerAnchorScreenRect(AppSettings settings)
    {
        if (_captureManager == null)
        {
            return Rect.Empty;
        }

        var captureBounds = _captureManager.GetCaptureBounds(settings);
        if (captureBounds.IsEmpty)
        {
            return Rect.Empty;
        }

        var roiOrCapture = GetRoiBounds(settings, captureBounds);
        // WHY: When ROI is invalid/outside the frame, keep spinner anchored to the capture target.
        return roiOrCapture.IsEmpty ? captureBounds : roiOrCapture;
    }

    private async Task SelectRoiAsync()
    {
        if (_captureManager == null)
        {
            return;
        }

        var bounds = _captureManager.GetCaptureBounds(_settingsService.Settings);
        var selector = new RoiSelectorWindow(bounds);
        var result = selector.ShowDialog();
        if (result == true && selector.SelectedRect is { } rect)
        {
            var settings = _settingsService.Settings;
            settings.Roi = SerializableRect.FromRect(rect);
            settings.NormalizedRoi = selector.SelectedNormalizedRect;
            var roiWasDisabled = !settings.EnableRoi;
            settings.EnableRoi = true;
            if (EnableRoiCheck != null)
            {
                _isApplyingSettings = true;
                EnableRoiCheck.IsChecked = true;
                _isApplyingSettings = false;
            }

            UpdateRoiStatus(settings);
            await _settingsService.SaveAsync().ConfigureAwait(true);
            if (roiWasDisabled)
            {
                AppendLog("ROI enabled automatically.");
            }

            AppendLog("ROI updated.");
        }
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        _isApplyingSettings = true;
        ReloadLlamaModelOptions(settings);
        ApplyTranslationPriority(settings);
        UpdateTranslationStatus(settings);
        _mainWindowViewModel.Settings.LoadFrom(settings);
        UpdateLoggingState(settings.EnableLogging);
        UpdateRoiStatus(settings);
        _isApplyingSettings = false;
    }

    private void UpdateRoiStatus(AppSettings settings)
    {
        if (!settings.EnableRoi)
        {
            RoiStatusText.Text = "ROI: disabled";
            return;
        }

        if (settings.NormalizedRoi is null || settings.NormalizedRoi.Value.IsEmpty)
        {
            RoiStatusText.Text = "ROI: not set";
            return;
        }

        var roi = settings.NormalizedRoi.Value;
        RoiStatusText.Text = $"ROI: {roi.X:0.000},{roi.Y:0.000} {roi.Width:0.000}x{roi.Height:0.000}";
    }

    private void UpdateTranslationStatus(AppSettings settings)
    {
        var llamaStatus = settings.EnableLlamaCppTranslation
            ? "Llama: enabled"
            : "Llama: disabled";
        var geminiStatus = settings.EnableGemini
            ? (string.IsNullOrWhiteSpace(settings.ApiKey) ? "Gemini: key missing" : "Gemini: enabled")
            : "Gemini: disabled";
        var deepLStatus = settings.EnableDeepL
            ? (string.IsNullOrWhiteSpace(settings.DeepLApiKey) ? "DeepL: key missing" : "DeepL: enabled")
            : "DeepL: disabled";
        TranslationStatusText.Text = $"Translation status: {llamaStatus} | {geminiStatus} | {deepLStatus}";
    }

    private void ReloadLlamaModelOptions(AppSettings settings)
    {
        if (LlamaModelBox == null)
        {
            return;
        }

        var fallback = SettingsUiController.NormalizeLlamaModelFileName(DefaultLlamaModelFileName);
        var selectedFromViewModel = _mainWindowViewModel.Settings.LlamaSelectedModelFileName;
        var selectedModel = string.IsNullOrWhiteSpace(selectedFromViewModel)
            ? settings.LlamaSelectedModelFileName
            : selectedFromViewModel;
        var selected = _llamaModelCatalog.NormalizeModelFileName(selectedModel, fallback);
        var modelFileNames = _llamaModelCatalog.GetAvailableModelFileNames(settings.LlamaGrpcProjectDir);
        var previousApplyingState = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            LlamaModelBox.Items.Clear();
            foreach (var fileName in modelFileNames)
            {
                LlamaModelBox.Items.Add(new ComboBoxItem
                {
                    Content = fileName,
                    Tag = fileName
                });
            }

            if (!modelFileNames.Contains(selected, StringComparer.OrdinalIgnoreCase))
            {
                // WHY: Keep broken selections visible so users can recover from missing model files.
                LlamaModelBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{selected} (missing)",
                    Tag = selected
                });
            }

            SetComboBoxByTag(LlamaModelBox, selected);
            if (LlamaModelBox.SelectedItem == null && LlamaModelBox.Items.Count > 0)
            {
                LlamaModelBox.SelectedIndex = 0;
            }
        }
        finally
        {
            _isApplyingSettings = previousApplyingState;
        }

        settings.LlamaSelectedModelFileName = GetSelectedTag(LlamaModelBox, fallback);
    }

    private static CTranslate2HostConfig BuildCTranslate2HostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeCTranslate2Settings(settings);
        return new CTranslate2HostConfig(
            settings.CTranslate2Device,
            settings.CTranslate2Precision,
            settings.CTranslate2ModelId,
            settings.CTranslate2ModelDir,
            settings.CTranslate2GrpcEndpoint,
            settings.CTranslate2GrpcHost,
            settings.CTranslate2GrpcPort,
            settings.CTranslate2GrpcProjectDir,
            settings.CTranslate2GrpcUvPath,
            settings.CTranslate2GrpcServerScript);
    }

    private static LlamaHostConfig BuildLlamaHostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeLlamaSettings(settings);
        return new LlamaHostConfig(
            settings.LlamaHost,
            settings.LlamaPort,
            settings.LlamaContextSize,
            settings.LlamaGpuLayers,
            settings.LlamaThreads,
            settings.LlamaParallel,
            settings.LlamaBatchSize,
            settings.LlamaMaxTokens,
            settings.LlamaTemperature,
            settings.LlamaTopP,
            settings.LlamaTopK,
            settings.LlamaRepeatPenalty,
            settings.LlamaSelectedModelFileName,
            settings.LlamaGrpcEndpoint,
            settings.LlamaGrpcHost,
            settings.LlamaGrpcPort,
            settings.LlamaGrpcProjectDir,
            settings.LlamaGrpcUvPath,
            settings.LlamaGrpcServerScript);
    }

    private void SwapLanguages()
    {
        var settingsViewModel = _mainWindowViewModel.Settings;
        var sourceTag = settingsViewModel.SourceLanguageTag;
        var sourceCustom = settingsViewModel.SourceLanguageCustom;
        settingsViewModel.SourceLanguageTag = settingsViewModel.TargetLanguageTag;
        settingsViewModel.SourceLanguageCustom = settingsViewModel.TargetLanguageCustom;
        settingsViewModel.TargetLanguageTag = sourceTag;
        settingsViewModel.TargetLanguageCustom = sourceCustom;
    }

    private static string GetSelectedTag(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return fallback;
    }

    private static void SetComboBoxByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string itemTag && itemTag == tag)
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }
    }


    private void ApplyTranslationPriority(AppSettings settings)
    {
        var ordered = NormalizeTranslationPriority(settings);
        settings.TranslationPriority = ordered.ToList();
        _translationPriority.Clear();
        foreach (var name in ordered)
        {
            _translationPriority.Add(name);
        }

        if (_translationPriority.Count > 0)
        {
            TranslationPriorityList.SelectedIndex = 0;
        }
    }

    private List<string> GetTranslationPriority()
    {
        if (_translationPriority.Count == 0)
        {
            return new List<string>(TranslationProviderNames.Defaults);
        }

        return _translationPriority.ToList();
    }

    private static List<string> NormalizeTranslationPriority(AppSettings settings)
    {
        var allowed = new HashSet<string>(TranslationProviderNames.Defaults, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = settings.TranslationPriority ?? new List<string>();
        foreach (var name in current)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!allowed.Contains(name))
            {
                continue;
            }

            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        foreach (var name in TranslationProviderNames.Defaults)
        {
            if (seen.Add(name))
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }

    private void MoveTranslationPriorityUp()
    {
        var index = TranslationPriorityList.SelectedIndex;
        if (index <= 0)
        {
            return;
        }

        var item = _translationPriority[index];
        _translationPriority.RemoveAt(index);
        _translationPriority.Insert(index - 1, item);
        TranslationPriorityList.SelectedIndex = index - 1;
        RequestSettingsSave();
    }

    private void MoveTranslationPriorityDown()
    {
        var index = TranslationPriorityList.SelectedIndex;
        if (index < 0 || index >= _translationPriority.Count - 1)
        {
            return;
        }

        var item = _translationPriority[index];
        _translationPriority.RemoveAt(index);
        _translationPriority.Insert(index + 1, item);
        TranslationPriorityList.SelectedIndex = index + 1;
        RequestSettingsSave();
    }

    private async Task ReloadLlamaModelsAsync()
    {
        var settings = _settingsService.Settings;
        ReloadLlamaModelOptions(settings);
        await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
    }

    private async Task RestartLlamaCppAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            SetBusyOverlay(true, "Restarting Llama.cpp...");
            _llamaGrpcHost?.Stop();
            _llamaHostConfig = null;

            // WHY: Manual restart is expected to keep Llama enabled after this action.
            var previousApplyingState = _isApplyingSettings;
            _isApplyingSettings = true;
            EnableLlamaCppCheck.IsChecked = true;
            _isApplyingSettings = previousApplyingState;

            await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
            AppendLog("Llama.cpp restarted.");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to restart Llama.cpp host.");
            ShowLoadFailure("Failed to restart Llama.cpp. See the logs for details.");
        }
        finally
        {
            SetBusyOverlay(false, null);
        }
    }

    private async Task StopLlamaServerAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            _llamaGrpcHost?.Stop();
            _llamaHostConfig = null;

            // WHY: Keep persisted settings consistent with the explicit stop action.
            var previousApplyingState = _isApplyingSettings;
            _isApplyingSettings = true;
            EnableLlamaCppCheck.IsChecked = false;
            _isApplyingSettings = previousApplyingState;

            await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
            AppendLog("llama-server stopped.");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to stop llama-server.");
            ShowLoadFailure("Failed to stop llama-server. See the logs for details.");
        }
    }

    private async Task RestartPaddleOcrHostsAsync()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_runCoordinator.IsRunning)
        {
            AppendLog("Restart skipped: OCR is running.");
            return;
        }

        try
        {
            SetBusyOverlay(true, "Applying OCR settings and restarting host...");
            await SaveSettingsImmediatelyAsync().ConfigureAwait(true);

            var settings = _settingsService.Settings;
            if (settings.OcrEngine == OcrEngineKind.Paddle)
            {
                _paddleGrpcHost?.Stop();
                _paddleVlGrpcHost?.Stop();
            }
            else if (settings.OcrEngine == OcrEngineKind.PaddleVllm)
            {
                _paddleVlGrpcHost?.Stop();
                _paddleGrpcHost?.Stop();
            }
            else
            {
                AppendLog("OCR host restart skipped: current OCR engine is WinRT.");
                return;
            }

            await EnsureResourceHostsAsync(settings).ConfigureAwait(true);
            AppendLog("OCR host restarted with latest settings.");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to restart OCR host.");
            ShowLoadFailure("Failed to restart OCR host. See the logs for details.");
        }
        finally
        {
            SetBusyOverlay(false, null);
        }
    }

    private void StopPaddleVlHost()
    {
        if (!IsLoaded)
        {
            return;
        }

        if (_runCoordinator.IsRunning)
        {
            AppendLog("Stop skipped: OCR is running.");
            return;
        }

        if (_paddleVlGrpcHost is not { IsRunning: true })
        {
            AppendLog("PaddleOCR-VL host stop skipped: host is not running.");
            return;
        }

        try
        {
            _paddleVlGrpcHost.Stop();
            AppendLog("PaddleOCR-VL host stopped.");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to stop PaddleOCR-VL host.");
            ShowLoadFailure("Failed to stop PaddleOCR-VL host. See the logs for details.");
        }
    }

    private void RequestSettingsSave()
    {
        _settingsChangeScheduler.RequestSave();
    }

    private Task FlushPendingSettingsSaveAsync()
    {
        return _settingsChangeScheduler.FlushAsync();
    }

    private async Task SaveSettingsImmediatelyAsync()
    {
        _settingsChangeScheduler.CancelPending();
        await SaveSettingsCoreAsync().ConfigureAwait(true);
    }

    private Task SaveSettingsCoreAsync()
    {
        return _settingsUiController.SaveFromUiAsync();
    }

    private void ApplyUiInputToSettings(AppSettings settings)
    {
        _mainWindowViewModel.Settings.ApplyTo(settings);
        settings.TranslationPriority = GetTranslationPriority();
    }

    private void ApplyRuntimeStateAfterSave(AppSettings settings)
    {
        // WHY: Rehydrate VM from normalized settings so invalid text input is corrected in bound controls.
        _mainWindowViewModel.Settings.LoadFrom(settings);
        _overlayWindow?.ApplyStyle(settings);
        UpdateLoggingState(settings.EnableLogging);
        _overlayPresenter?.UpdatePerfLogging(settings.EnableOcrPerfLog && settings.EnableLogging, settings.OcrPerfLogThresholdMs);
        UpdateRoiStatus(settings);
        UpdateTranslationStatus(settings);
    }

    private void PopulateHotkeyKeyBoxes()
    {
        var keys = BuildHotkeyKeyOptions();
        HotkeyRunOnceKeyBox.ItemsSource = keys;
        HotkeyToggleOverlayKeyBox.ItemsSource = keys;
        HotkeyForceRunKeyBox.ItemsSource = keys;
        HotkeyForceGeminiStrictKeyBox.ItemsSource = keys;
        HotkeyOcrOnlyKeyBox.ItemsSource = keys;
        HotkeyToggleSceneAutoTranslateKeyBox.ItemsSource = keys;
        HotkeySelectRoiKeyBox.ItemsSource = keys;
        HotkeyLockCaptureWindowKeyBox.ItemsSource = keys;
        HotkeyUnlockCaptureWindowKeyBox.ItemsSource = keys;
    }

    private static IReadOnlyList<string> BuildHotkeyKeyOptions()
    {
        var keys = new List<string>();
        for (var i = 1; i <= 12; i++)
        {
            keys.Add($"F{i}");
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            keys.Add(c.ToString());
        }

        return keys;
    }

    private void InitializeHotkeys(AppSettings settings)
    {
        var config = BuildHotkeyConfigFromSettings(settings);
        if (TryRegisterHotkeys(config))
        {
            _currentHotkeyConfig = config;
            return;
        }

        _currentHotkeyConfig = null;
        AppendLog("Some hotkeys failed to register. Available hotkeys remain active.");
    }

    private void TryUpdateHotkeys(AppSettings settings)
    {
        var config = BuildHotkeyConfigFromSettings(settings);
        if (_currentHotkeyConfig.HasValue && _currentHotkeyConfig.Value.Equals(config))
        {
            return;
        }

        if (TryRegisterHotkeys(config))
        {
            _currentHotkeyConfig = config;
            AppendLog($"Hotkey updated: RunOnce={FormatHotkey(config.RunOnceKey, config.RunOnceModifiers)}, " +
                      $"Toggle={FormatHotkey(config.ToggleOverlayKey, config.ToggleOverlayModifiers)}, " +
                      $"ForceRun={FormatHotkey(config.ForceRunKey, config.ForceRunModifiers)}, " +
                      $"ForceGeminiStrict={FormatHotkey(config.ForceGeminiStrictKey, config.ForceGeminiStrictModifiers)}, " +
                      $"OcrOnly={FormatHotkey(config.OcrOnlyKey, config.OcrOnlyModifiers)}, " +
                      $"SceneAutoTranslate={FormatHotkey(config.ToggleSceneAutoTranslateKey, config.ToggleSceneAutoTranslateModifiers)}, " +
                      $"Roi={FormatHotkey(config.SelectRoiKey, config.SelectRoiModifiers)}, " +
                      $"Lock={FormatHotkey(config.LockCaptureWindowKey, config.LockCaptureWindowModifiers)}, " +
                      $"Unlock={FormatHotkey(config.UnlockCaptureWindowKey, config.UnlockCaptureWindowModifiers)}.");
        }
        else
        {
            AppendLog("Some hotkeys failed to update. Other hotkeys remain active.");
        }
    }

    private bool TryRegisterHotkeys(HotkeyConfig config)
    {
        return _hotkeyController.TryRegisterBindings(BuildHotkeyRegistrations(config));
    }

    private IReadOnlyList<HotkeyBindingRegistration> BuildHotkeyRegistrations(HotkeyConfig config)
    {
        return new List<HotkeyBindingRegistration>
        {
            new("RunOnce", config.RunOnceKey, config.RunOnceModifiers, 1, OnHotkeyPressed),
            new("ToggleOverlay", config.ToggleOverlayKey, config.ToggleOverlayModifiers, 2, OnToggleOverlayHotkeyPressed),
            new("ForceRun", config.ForceRunKey, config.ForceRunModifiers, 3, OnForceRunHotkeyPressed),
            new("ForceGeminiStrict", config.ForceGeminiStrictKey, config.ForceGeminiStrictModifiers, 4,
                OnForceGeminiStrictHotkeyPressed),
            new("OverlayText", config.OcrOnlyKey, config.OcrOnlyModifiers, 5, OnOcrOnlyHotkeyPressed),
            new("SceneAutoTranslate", config.ToggleSceneAutoTranslateKey, config.ToggleSceneAutoTranslateModifiers, 9,
                OnToggleSceneAutoTranslateHotkeyPressed),
            new("SelectRoi", config.SelectRoiKey, config.SelectRoiModifiers, 6, OnSelectRoiHotkeyPressed),
            new("LockWindow", config.LockCaptureWindowKey, config.LockCaptureWindowModifiers, 7,
                OnLockCaptureWindowHotkeyPressed),
            new("UnlockWindow", config.UnlockCaptureWindowKey, config.UnlockCaptureWindowModifiers, 8,
                OnUnlockCaptureWindowHotkeyPressed)
        };
    }

    private static HotkeyConfig BuildHotkeyConfigFromSettings(AppSettings settings)
    {
        return new HotkeyConfig(
            ParseKey(settings.HotkeyRunOnceKey, Key.F8),
            ParseModifiers(settings.HotkeyRunOnceModifiers),
            ParseKey(settings.HotkeyToggleOverlayKey, Key.F9),
            ParseModifiers(settings.HotkeyToggleOverlayModifiers),
            ParseKey(settings.HotkeyForceRunKey, Key.F10),
            ParseModifiers(settings.HotkeyForceRunModifiers),
            ParseKey(settings.HotkeyForceGeminiStrictKey, Key.F10),
            ParseModifiers(settings.HotkeyForceGeminiStrictModifiers),
            ParseKey(settings.HotkeyOcrOnlyKey, Key.F11),
            ParseModifiers(settings.HotkeyOcrOnlyModifiers),
            ParseKey(settings.HotkeyToggleSceneAutoTranslateKey, Key.F5),
            ParseModifiers(settings.HotkeyToggleSceneAutoTranslateModifiers),
            ParseKey(settings.HotkeySelectRoiKey, Key.F6),
            ParseModifiers(settings.HotkeySelectRoiModifiers),
            ParseKey(settings.HotkeyLockCaptureWindowKey, Key.F7),
            ParseModifiers(settings.HotkeyLockCaptureWindowModifiers),
            ParseKey(settings.HotkeyUnlockCaptureWindowKey, Key.F7),
            ParseModifiers(settings.HotkeyUnlockCaptureWindowModifiers));
    }

    private static Key ParseKey(string value, Key fallback)
    {
        return Enum.TryParse(value, true, out Key parsed) && parsed != Key.None ? parsed : fallback;
    }

    private static ModifierKeys ParseModifiers(string value)
    {
        return Enum.TryParse(value, true, out ModifierKeys parsed) ? parsed : ModifierKeys.None;
    }

    private static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        if (modifiers == ModifierKeys.None)
        {
            return key.ToString();
        }

        return $"{FormatModifiers(modifiers)}+{key}";
    }

    private static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>(3);
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        return string.Join("+", parts);
    }

    private void OnOverlayShown() => _sceneChangeController.OnOverlayShown();

    private void OnOverlayHidden() => _sceneChangeController.OnOverlayHidden();

    private void OnOverlayUpdated() => _sceneChangeController.OnOverlayUpdated();

    private void InitializeAutoHideWatcher(AppSettings settings) => _sceneChangeController.Initialize(settings);

    private void UpdateAutoHideWatcher(AppSettings settings) => _sceneChangeController.UpdateWatcher(settings);

    private void ClearSceneChangeAutoTranslatePending(string reason) =>
        _sceneChangeController.ClearPendingAutoTranslate(reason);

    private Rect GetRoiBounds(AppSettings settings, Rect frameBounds)
    {
        if (!settings.EnableRoi)
        {
            return frameBounds;
        }

        if (settings.NormalizedRoi is { } normalized && !normalized.IsEmpty)
        {
            return normalized.ToAbsolute(frameBounds);
        }

        if (settings.Roi is null || settings.Roi.Value.IsEmpty)
        {
            return frameBounds;
        }

        var absolute = Rect.Intersect(frameBounds, settings.Roi.Value.ToRect());
        if (!absolute.IsEmpty)
        {
            settings.NormalizedRoi = NormalizedRect.FromAbsolute(absolute, frameBounds);
            // NOTE: Fire-and-forget migration to normalized ROI for DPI-safe persistence.
            _ = _settingsService.SaveAsync();
        }

        return absolute;
    }

    private void AppendLog(string message)
    {
        _uiLogController.AppendLog(message);
    }

    private void InitializeLogBuffer()
    {
        _uiLogController.Start();
    }

    private void FlushLogPayload(string payload)
    {
        if (LogBox == null)
        {
            return;
        }

        LogBox.AppendText(payload);
        _logLineCount += CountNewlines(payload);
        TrimLogLines(MaxLogLines);
        LogBox.ScrollToEnd();
    }

    private void TrimLogLines(int maxLines)
    {
        if (maxLines <= 0 || _logLineCount <= maxLines || LogBox == null)
        {
            return;
        }

        var removeLines = _logLineCount - maxLines;
        var text = LogBox.Text;
        var cutIndex = IndexOfNthNewline(text, removeLines);
        if (cutIndex < 0)
        {
            _logLineCount = CountLines(text);
            return;
        }

        LogBox.Text = text[(cutIndex + 1)..];
        _logLineCount = maxLines;
    }

    private static int IndexOfNthNewline(string text, int count)
    {
        if (string.IsNullOrEmpty(text) || count <= 0)
        {
            return -1;
        }

        var index = -1;
        var remaining = count;
        while (remaining > 0)
        {
            index = text.IndexOf('\n', index + 1);
            if (index < 0)
            {
                return -1;
            }

            remaining--;
        }

        return index;
    }

    private static int CountNewlines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = CountNewlines(text);
        return text.EndsWith("\n", StringComparison.Ordinal) ? lines : lines + 1;
    }

    private void UpdateLoggingState(bool enabled)
    {
        _uiLogController.SetEnabled(enabled);
        _logger?.SetEnabled(enabled);
    }

    private void OnOcrPreprocessPreviewReady(Bitmap bitmap)
    {
        try
        {
            var source = CreateBitmapSource(bitmap);
            Dispatcher.Invoke(() =>
            {
                OcrPreprocessPreviewImage.Source = source;
                OcrPreprocessPreviewHint.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to update OCR preprocess preview.");
        }
    }

    private async void OnTranslationStarted()
    {
        if (!_runCoordinator.IsRunning)
        {
            return;
        }

        CancelTranslationOverlay();
        _translationOverlayCts?.Dispose();
        _translationOverlayCts = new CancellationTokenSource();
        var token = _translationOverlayCts.Token;
        try
        {
            await Task.Delay(TranslationOverlayDelayMs, token).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !_runCoordinator.IsRunning)
        {
            return;
        }

        SetBusyOverlay(true, "Translating...");
    }

    private void OnTranslationCompleted()
    {
        CancelTranslationOverlay();
        if (!_runCoordinator.IsRunning)
        {
            return;
        }

        SetBusyOverlay(true, "OCR running...");
    }

    private static BitmapSource CreateBitmapSource(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        try
        {
            var stride = Math.Abs(data.Stride);
            var buffer = new byte[stride * bitmap.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            var source = BitmapSource.Create(
                bitmap.Width,
                bitmap.Height,
                bitmap.HorizontalResolution,
                bitmap.VerticalResolution,
                System.Windows.Media.PixelFormats.Pbgra32,
                null,
                buffer,
                stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private readonly record struct CTranslate2HostConfig(
        string Device,
        string Precision,
        string ModelId,
        string? ModelDir,
        string Endpoint,
        string Host,
        int Port,
        string ProjectDir,
        string UvPath,
        string ServerScript);

    private readonly record struct LlamaHostConfig(
        string Host,
        int Port,
        int ContextSize,
        int GpuLayers,
        int Threads,
        int Parallel,
        int BatchSize,
        int MaxTokens,
        double Temperature,
        double TopP,
        int TopK,
        double RepeatPenalty,
        string ModelFileName,
        string Endpoint,
        string GrpcHost,
        int GrpcPort,
        string ProjectDir,
        string UvPath,
        string ServerScript);

    private readonly record struct HotkeyConfig(
        Key RunOnceKey,
        ModifierKeys RunOnceModifiers,
        Key ToggleOverlayKey,
        ModifierKeys ToggleOverlayModifiers,
        Key ForceRunKey,
        ModifierKeys ForceRunModifiers,
        Key ForceGeminiStrictKey,
        ModifierKeys ForceGeminiStrictModifiers,
        Key OcrOnlyKey,
        ModifierKeys OcrOnlyModifiers,
        Key ToggleSceneAutoTranslateKey,
        ModifierKeys ToggleSceneAutoTranslateModifiers,
        Key SelectRoiKey,
        ModifierKeys SelectRoiModifiers,
        Key LockCaptureWindowKey,
        ModifierKeys LockCaptureWindowModifiers,
        Key UnlockCaptureWindowKey,
        ModifierKeys UnlockCaptureWindowModifiers)
    {
        public readonly IEnumerable<(string Name, Key Key, ModifierKeys Modifiers)> GetBindings()
        {
            yield return ("Run once", RunOnceKey, RunOnceModifiers);
            yield return ("Toggle overlay", ToggleOverlayKey, ToggleOverlayModifiers);
            yield return ("Force run", ForceRunKey, ForceRunModifiers);
            yield return ("Force Gemini (strict)", ForceGeminiStrictKey, ForceGeminiStrictModifiers);
            yield return ("Overlay text", OcrOnlyKey, OcrOnlyModifiers);
            yield return ("Scene auto-translate", ToggleSceneAutoTranslateKey, ToggleSceneAutoTranslateModifiers);
            yield return ("Select ROI", SelectRoiKey, SelectRoiModifiers);
            yield return ("Lock window", LockCaptureWindowKey, LockCaptureWindowModifiers);
            yield return ("Unlock window", UnlockCaptureWindowKey, UnlockCaptureWindowModifiers);
        }

        public static HotkeyConfig Default => new(
            Key.F8,
            ModifierKeys.None,
            Key.F9,
            ModifierKeys.None,
            Key.F10,
            ModifierKeys.None,
            Key.F10,
            ModifierKeys.Shift,
            Key.F11,
            ModifierKeys.None,
            Key.F5,
            ModifierKeys.None,
            Key.F6,
            ModifierKeys.None,
            Key.F7,
            ModifierKeys.None,
            Key.F7,
            ModifierKeys.Shift);
    }
}

