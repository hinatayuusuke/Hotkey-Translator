using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.Application;
using Hotkey_Translator.Services.Hook;
using Hotkey_Translator.Services.Settings;
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
    private readonly HotkeyController _hotkeyController;
    private readonly UiLogViewAdapter _uiLogViewAdapter;
    private readonly UiLogController _uiLogController;
    private readonly SceneChangeController _sceneChangeController;
    private readonly SettingsUiController _settingsUiController;
    private readonly ResourceHostFacade _resourceHostFacade;
    private readonly ResourceHostCommandController _resourceHostCommandController;
    private readonly HotkeyCommandController _hotkeyCommandController;
    private readonly SettingsChangeScheduler _settingsChangeScheduler;
    private readonly MainWindowViewModel _mainWindowViewModel;
    private readonly MainWindowRunCoordinator _runCoordinator;
    private readonly Dx11HookClientService _dx11HookClientService;
    private PhashService? _phashService;
    private CancellationTokenSource? _translationOverlayCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private HotkeyConfig? _currentHotkeyConfig;
    private bool _isApplyingSettings;
    private readonly DrawerLayoutController _drawerLayoutController;
    private readonly PreviewZoomCoordinator _previewZoomCoordinator;
    private readonly PreviewFrameDispatcher _previewFrameDispatcher;
    private const int OverlayBaselineDelayMs = 150;
    private const int LogFlushIntervalMs = 150;
    private const int MaxLogLines = 1000;
    private const int TranslationOverlayDelayMs = 200;
    private const int SettingsSaveDebounceMs = 200;
    private const double DrawerAutoResizeTolerance = 12.0;
    private const double DrawerAutoResizeFallbackHeight = 300.0;
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";

    public MainWindow()
    {
        _settingsUiController = new SettingsUiController(_settingsService, this, () => _logger, ApplyViewModelInputToSettings);
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
            RequestSettingsSave,
            ReloadLlamaModelsAsync,
            RestartLlamaCppAsync,
            StopLlamaServerAsync,
            RestartPaddleOcrHostsAsync,
            StopPaddleVlHost,
            SaveSettingsImmediatelyAsync);
        _resourceHostFacade = new ResourceHostFacade(
            () => _logger,
            SetBusyOverlay,
            SyncSettingsAfterHostFailure,
            ShowLoadFailure);
        DataContext = _mainWindowViewModel;
        _drawerLayoutController = new DrawerLayoutController(
            this,
            Dispatcher,
            () => _mainWindowViewModel.IsBottomPanelOpen,
            () => BottomDrawerBorder.ActualHeight,
            DrawerAutoResizeFallbackHeight,
            DrawerAutoResizeTolerance);
        _drawerLayoutController.SyncStartupState();
        _previewZoomCoordinator = new PreviewZoomCoordinator(this);
        _previewFrameDispatcher = new PreviewFrameDispatcher(
            Dispatcher,
            ShouldRenderBottomPreviewPane,
            ApplyPreviewBitmapSource,
            ex => _logger?.Error(ex, "Failed to update OCR preprocess preview."));
        _mainWindowViewModel.PropertyChanged += OnMainWindowViewModelPropertyChanged;
        _hotkeyController = new HotkeyController(this, () => _logger, FormatHotkey);
        _dx11HookClientService = new Dx11HookClientService(() => _logger);
        _uiLogViewAdapter = new UiLogViewAdapter(() => LogBox, MaxLogLines);
        _uiLogController = new UiLogController(Dispatcher, _uiLogViewAdapter.FlushPayload, LogFlushIntervalMs);
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
        _resourceHostCommandController = new ResourceHostCommandController(
            _resourceHostFacade,
            () => IsLoaded,
            () => _runCoordinator.IsRunning,
            () => _settingsService.Settings,
            SyncSettingsAfterHostFailure,
            SaveSettingsImmediatelyAsync,
            SetBusyOverlay,
            AppendLog,
            ShowLoadFailure,
            () => _logger);
        _hotkeyCommandController = new HotkeyCommandController(
            () => _runCoordinator.HasRunOnce,
            RunOnceAsync,
            options => RunOnceAsync(options),
            () => _pipeline,
            () => _overlayTextMode,
            mode => _overlayTextMode = mode,
            () => _settingsService.Settings,
            settings =>
            {
                _mainWindowViewModel.Settings.LoadFrom(settings);
                UpdateAutoTranslateBadgeVisibility(settings);
            },
            UpdateAutoHideWatcher,
            ClearSceneChangeAutoTranslatePending,
            _windowBindingService,
            () => _settingsService.SaveAsync(),
            SelectRoiAsync,
            () => _overlayPresenter,
            () => _overlayEnabled,
            enabled => _overlayEnabled = enabled,
            AppendLog);
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
        settingsChanged |= await _resourceHostFacade.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
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
        UpdateAutoTranslateBadgeVisibility(settings);
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
            _dx11HookClientService,
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
        await _dx11HookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
        AppendLog("Ready. F5: toggle scene auto-translate. F6: select ROI. F8: run once. F9: toggle overlay. F10: force run. Shift+F10: force Gemini strict. F11: toggle overlay text. F7: lock window. Shift+F7: unlock window.");
        _drawerLayoutController.SyncForCurrentState();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewZoomCoordinator.Dispose();
        _previewFrameDispatcher.Dispose();
        _drawerLayoutController.Reset();
        _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        _runCoordinator.Dispose();
        _settingsChangeScheduler.CancelPending();
        _settingsChangeScheduler.Dispose();
        _translationOverlayCts?.Cancel();
        _translationOverlayCts?.Dispose();
        try
        {
            _dx11HookClientService.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // WHY: Shutdown path should continue even if hook host pipe is unavailable.
            _logger?.Error(ex, "Failed to stop DX11 hook client.");
        }
        _dx11HookClientService.Dispose();
        _hotkeyController.Dispose();
        _uiLogController.Dispose();
        _sceneChangeController.Dispose();
        _cacheRepository?.Dispose();
        _ocrEngine?.Dispose();
        _httpClient.Dispose();
        _resourceHostFacade.Dispose();
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

    private void ShowLoadFailure(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowLoadFailure(message));
            return;
        }

        MessageBox.Show(this, message, "Load failed", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void SyncSettingsAfterHostFailure(AppSettings settings, bool updateTranslationStatus)
    {
        _mainWindowViewModel.Settings.LoadFrom(settings);
        if (updateTranslationStatus)
        {
            UpdateTranslationStatus(settings);
        }
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleRunOnceHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnForceRunHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleForceRunHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnForceGeminiStrictHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleForceGeminiStrictHotkeyAsync().ConfigureAwait(true);
    }

    private void OnOcrOnlyHotkeyPressed(object? sender, EventArgs e)
    {
        _hotkeyCommandController.HandleOverlayTextHotkey();
    }

    private async void OnToggleSceneAutoTranslateHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleToggleSceneAutoTranslateHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnLockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        var spec = await _hotkeyCommandController.HandleLockCaptureWindowHotkeyAsync().ConfigureAwait(true);
        UpdatePinnedThumbnailFromLockResult(spec);
    }

    private async void OnUnlockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleUnlockCaptureWindowHotkeyAsync().ConfigureAwait(true);
        ClearPinnedCaptureThumbnail("No fixed target");
    }

    private async void OnSelectRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleSelectRoiHotkeyAsync().ConfigureAwait(true);
    }

    private void OnToggleOverlayHotkeyPressed(object? sender, EventArgs e)
    {
        _hotkeyCommandController.HandleToggleOverlayHotkey();
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
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusyOverlay(visible, message));
            return;
        }

        _mainWindowViewModel.RuntimeStatus.IsBusy = visible;
        if (!string.IsNullOrWhiteSpace(message))
        {
            _mainWindowViewModel.RuntimeStatus.BusyMessage = message;
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

    void ISettingsUiBridge.ApplyRuntimeStateAfterSave(AppSettings settings) => ApplyRuntimeStateAfterSave(settings);
    Task<bool> ISettingsUiBridge.EnsureResourceHostsAsync(AppSettings settings) => _resourceHostFacade.EnsureResourceHostsAsync(settings);
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
            _mainWindowViewModel.Settings.LoadFrom(settings);

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
        UpdateAutoTranslateBadgeVisibility(settings);
    }

    private void UpdateRoiStatus(AppSettings settings)
    {
        if (!settings.EnableRoi)
        {
            _mainWindowViewModel.RuntimeStatus.RoiStatusMessage = "ROI: disabled";
            return;
        }

        if (settings.NormalizedRoi is null || settings.NormalizedRoi.Value.IsEmpty)
        {
            _mainWindowViewModel.RuntimeStatus.RoiStatusMessage = "ROI: not set";
            return;
        }

        var roi = settings.NormalizedRoi.Value;
        _mainWindowViewModel.RuntimeStatus.RoiStatusMessage =
            $"ROI: {roi.X:0.000},{roi.Y:0.000} {roi.Width:0.000}x{roi.Height:0.000}";
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
        _mainWindowViewModel.RuntimeStatus.TranslationStatusMessage =
            $"Translation status: {llamaStatus} | {geminiStatus} | {deepLStatus}";
    }

    private void UpdateAutoTranslateBadgeVisibility(AppSettings settings)
    {
        if (_overlayPresenter == null)
        {
            return;
        }

        var visible = settings.EnableSceneChangeAutoTranslate && settings.ShowAutoTranslateBadgeIcon;
        _overlayPresenter.SetAutoTranslateBadgeVisible(visible, ResolveAutoTranslateBadgeAnchorScreenRect(settings));
    }

    private Rect ResolveAutoTranslateBadgeAnchorScreenRect(AppSettings settings)
    {
        if (_captureManager == null)
        {
            return Rect.Empty;
        }

        var captureBounds = _captureManager.GetCaptureBounds(settings);
        if (!captureBounds.IsEmpty)
        {
            return captureBounds;
        }

        // WHY: Keep badge visible even when capture bounds cannot be resolved.
        return ResolveSpinnerAnchorScreenRect(settings);
    }

    private void ReloadLlamaModelOptions(AppSettings settings)
    {
        var fallback = SettingsHostNormalizer.NormalizeLlamaModelFileName(DefaultLlamaModelFileName);
        var selectedFromViewModel = _mainWindowViewModel.Settings.LlamaSelectedModelFileName;
        var selectedModel = string.IsNullOrWhiteSpace(selectedFromViewModel)
            ? settings.LlamaSelectedModelFileName
            : selectedFromViewModel;
        var selected = _llamaModelCatalog.NormalizeModelFileName(selectedModel, fallback);
        var modelFileNames = _llamaModelCatalog.GetAvailableModelFileNames(settings.LlamaGrpcProjectDir);
        var modelOptions = modelFileNames
            .Select(fileName => new LlamaModelOption(fileName, fileName))
            .ToList();
        if (!modelFileNames.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            // WHY: Keep broken selections visible so users can recover from missing model files.
            modelOptions.Add(new LlamaModelOption(selected, $"{selected} (missing)"));
        }

        var previousApplyingState = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            _mainWindowViewModel.ResetLlamaModelOptions(modelOptions);
            var resolvedSelection = modelOptions.Count == 0
                ? fallback
                : selected;
            if (modelOptions.Count > 0 &&
                !modelOptions.Any(option => string.Equals(option.Value, selected, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedSelection = modelOptions[0].Value;
            }

            _mainWindowViewModel.Settings.LlamaSelectedModelFileName = resolvedSelection;
            settings.LlamaSelectedModelFileName = resolvedSelection;
        }
        finally
        {
            _isApplyingSettings = previousApplyingState;
        }
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

    private void ApplyTranslationPriority(AppSettings settings)
    {
        var ordered = NormalizeTranslationPriority(settings);
        settings.TranslationPriority = ordered.ToList();
        _mainWindowViewModel.ResetTranslationPriority(ordered);
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

    private async Task ReloadLlamaModelsAsync()
    {
        var settings = _settingsService.Settings;
        ReloadLlamaModelOptions(settings);
        await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
    }

    private Task RestartLlamaCppAsync() => _resourceHostCommandController.RestartLlamaCppAsync();

    private Task StopLlamaServerAsync() => _resourceHostCommandController.StopLlamaServerAsync();

    private Task RestartPaddleOcrHostsAsync() => _resourceHostCommandController.RestartPaddleOcrHostsAsync();

    private void StopPaddleVlHost() => _resourceHostCommandController.StopPaddleVlHost();

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

    private void ApplyViewModelInputToSettings(AppSettings settings)
    {
        _mainWindowViewModel.Settings.ApplyTo(settings);
        settings.TranslationPriority =
            _mainWindowViewModel.GetTranslationPriorityOrDefault(TranslationProviderNames.Defaults);
    }

    private void ApplyRuntimeStateAfterSave(AppSettings settings)
    {
        // WHY: Rehydrate VM from normalized settings so invalid text input is corrected in bound controls.
        _mainWindowViewModel.Settings.LoadFrom(settings);
        _overlayWindow?.ApplyStyle(settings);
        UpdateLoggingState(settings.EnableLogging);
        _overlayPresenter?.UpdatePerfLogging(settings.EnableOcrPerfLog && settings.EnableLogging, settings.OcrPerfLogThresholdMs);
        UpdateAutoTranslateBadgeVisibility(settings);
        UpdateRoiStatus(settings);
        UpdateTranslationStatus(settings);
        _ = _dx11HookClientService.ApplySettingsAsync(settings);
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

    private void OnOverlayShown()
    {
        _sceneChangeController.OnOverlayShown();
        UpdateAutoTranslateBadgeVisibility(_settingsService.Settings);
    }

    private void OnOverlayHidden() => _sceneChangeController.OnOverlayHidden();

    private void OnOverlayUpdated()
    {
        _sceneChangeController.OnOverlayUpdated();
        UpdateAutoTranslateBadgeVisibility(_settingsService.Settings);
    }

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

    private void UpdateLoggingState(bool enabled)
    {
        _uiLogController.SetEnabled(enabled);
        _logger?.SetEnabled(enabled);
    }

    private async void OnTranslationStarted()
    {
        if (!_runCoordinator.IsRunning || !_runCoordinator.ShouldShowCenterBusyForCurrentRun)
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
        if (!_runCoordinator.IsRunning || !_runCoordinator.ShouldShowCenterBusyForCurrentRun)
        {
            return;
        }

        SetBusyOverlay(true, "OCR running...");
    }

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

