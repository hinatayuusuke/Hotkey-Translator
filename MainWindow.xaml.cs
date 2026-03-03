using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
    private readonly HashSet<string> _shownPrerequisiteDialogKeys = new(StringComparer.Ordinal);
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
    private readonly WinRtOcrLanguagePackCoordinator _winRtLanguagePackCoordinator;
    private readonly Dx11HookClientService _dx11HookClientService;
    private readonly IMagpieProcessService _magpieProcessService;
    private readonly IMagpieIpcClient _magpieIpcClient;
    private readonly MagpieSessionController _magpieSessionController;
    private PhashService? _phashService;
    private CancellationTokenSource? _translationOverlayCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private HotkeyConfig? _currentHotkeyConfig;
    private bool _isApplyingSettings;
    private bool _isSelectingRoi;
    private readonly DrawerLayoutController _drawerLayoutController;
    private readonly PreviewZoomCoordinator _previewZoomCoordinator;
    private readonly PreviewFrameDispatcher _previewFrameDispatcher;
    private readonly DispatcherTimer _mirrorOverlayTopmostTimer;
    private HwndSource? _mainHwndSource;
    private uint _wmMagpieScalingChanged;
    private bool _isClosing;
    private int _winRtInstallUiDepth;
    private bool _winRtInstallPrevIsBusy;
    private string _winRtInstallPrevMessage = string.Empty;
    private bool _winRtInstallPrevIsIndeterminate = true;
    private double _winRtInstallPrevPercent;
    private CancellationTokenSource? _winRtLanguagePackPrecheckCts;
    private int _winRtLanguagePackPrecheckVersion;
    private IReadOnlyList<string> _registeredTranslationProviderNames = Array.Empty<string>();
    private readonly bool _hookRoiTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_ROI_TRACE"), "1", StringComparison.Ordinal);
    private const int OverlayBaselineDelayMs = 150;
    private const int LogFlushIntervalMs = 150;
    private const int MaxLogLines = 1000;
    private const int TranslationOverlayDelayMs = 200;
    private const int SettingsSaveDebounceMs = 200;
    private const double DrawerAutoResizeTolerance = 12.0;
    private const double DrawerAutoResizeFallbackHeight = 300.0;
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";
    private const int MirrorOverlayTopmostResyncIntervalMs = 500;
    private const string FixedUvRelativePath = "Tools\\uv\\uv.exe";
    private const string FixedHookHostRelativePath = "Native\\HookHost\\bin\\HookHost.exe";
    private const string FixedMagpieCoreRelativePath = "Tools\\Magpie\\Magpie.Core.exe";
    private const string FixedLlamaServerRelativePath = "TranslationServiceLlama\\LlamaCpp\\llama-server.exe";

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
        _mirrorOverlayTopmostTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(MirrorOverlayTopmostResyncIntervalMs)
        };
        _mirrorOverlayTopmostTimer.Tick += OnMirrorOverlayTopmostTimerTick;
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
        _mainWindowViewModel.Settings.PropertyChanged += OnSettingsViewModelPropertyChanged;
        _hotkeyController = new HotkeyController(this, () => _logger, FormatHotkey);
        _dx11HookClientService = new Dx11HookClientService(() => _logger);
        _magpieProcessService = new MagpieProcessService();
        _magpieIpcClient = new MagpieIpcClient();
        _magpieSessionController = new MagpieSessionController(
            _windowBindingService,
            _magpieProcessService,
            _magpieIpcClient,
            () => _logger,
            AppendLog);
        _magpieSessionController.ActiveStateChanged += OnMirrorSessionActiveStateChanged;
        _uiLogViewAdapter = new UiLogViewAdapter(() => LogBox, MaxLogLines);
        _uiLogController = new UiLogController(Dispatcher, _uiLogViewAdapter.FlushPayload, LogFlushIntervalMs);
        _winRtLanguagePackCoordinator = new WinRtOcrLanguagePackCoordinator(
            () => _logger,
            new WindowsCapabilityInstaller(() => _logger),
            ConfirmWinRtLanguagePackInstall,
            ShowWinRtLanguagePackInstallError,
            ShowWinRtLanguagePackInstallSuccess,
            BeginWinRtLanguagePackInstallUi,
            UpdateWinRtLanguagePackInstallUi,
            EndWinRtLanguagePackInstallUi);
        SceneChangeController? sceneChangeController = null;
        _runCoordinator = new MainWindowRunCoordinator(
            _settingsService,
            this,
            () => _pipeline,
            _winRtLanguagePackCoordinator,
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
            ToggleMirrorFullscreenHotkeyAsync,
            AppendLog);
        sceneChangeController = _sceneChangeController;
        PopulateHotkeyKeyBoxes();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _wmMagpieScalingChanged = _magpieSessionController.MagpieScalingChangedMessageId;
        _mainHwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _mainHwndSource?.AddHook(WndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger = new AppLogger(AppendLog);
        InitializeLogBuffer();
        await _settingsService.LoadAsync().ConfigureAwait(true);
        var settings = _settingsService.Settings;
        var settingsChanged = _settingsUiController.NormalizeOnLoad(settings);
        var geminiClient = new GeminiClient(_httpClient, _logger);
        var translationProviders = new List<ITranslationProvider>
        {
            new LlamaGrpcTranslationProvider(_logger),
            new DeepLTranslationProvider(_httpClient, _logger),
            new GeminiTranslationProvider(geminiClient)
        };
        _registeredTranslationProviderNames = translationProviders.Select(provider => provider.Name).ToList();
        ApplySettingsToUi(settings);
        CheckAndShowPrerequisiteDialogs(settings);
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
        ApplyMirrorOverlayMapper();

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
        AppendLog("Ready. F5: toggle scene auto-translate. F6: select ROI. F8: run once. F9: toggle overlay. F10: force run. Shift+F10: force Gemini strict. F11: toggle overlay text. F7: lock window. Shift+F7: unlock window. Ctrl+F7: toggle mirror fullscreen.");
        _drawerLayoutController.SyncForCurrentState();
        ScheduleWinRtLanguagePackPrecheck();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_wmMagpieScalingChanged != 0 && (uint)msg == _wmMagpieScalingChanged)
        {
            var eventId = wParam.ToInt64();
            var payload = lParam.ToInt64();
            _logger?.Info($"stage=magpie_ipc event=scaling_changed wparam={eventId} lparam={payload}.");

            if (eventId == 0)
            {
                if (_magpieSessionController.TrySyncExternalStop("magpie_message", payload))
                {
                    EnsureMirrorOverlayTopmostTimerActive(false);
                }
            }
            else if (_magpieSessionController.IsActive)
            {
                EnsureMirrorOverlayTopMost("magpie_message");
                EnsureMirrorOverlayTopmostTimerActive(true);
            }
        }

        return IntPtr.Zero;
    }

    private void OnMirrorOverlayTopmostTimerTick(object? sender, EventArgs e)
    {
        if (!_magpieSessionController.IsActive)
        {
            EnsureMirrorOverlayTopmostTimerActive(false);
            return;
        }

        EnsureMirrorOverlayTopMost("timer");
    }

    private void EnsureMirrorOverlayTopmostTimerActive(bool active)
    {
        if (active)
        {
            if (!_mirrorOverlayTopmostTimer.IsEnabled)
            {
                _mirrorOverlayTopmostTimer.Start();
            }

            return;
        }

        if (_mirrorOverlayTopmostTimer.IsEnabled)
        {
            _mirrorOverlayTopmostTimer.Stop();
        }
    }

    private void EnsureMirrorOverlayTopMost(string source)
    {
        if (!_magpieSessionController.IsActive || _overlayWindow == null)
        {
            return;
        }

        if (_overlayWindow.TryPromoteTopMost(out var reason))
        {
            return;
        }

        _logger?.Info($"stage=overlay_topmost event=promote result=failed source={source} reason={reason ?? "unknown"}.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;
        EnsureMirrorOverlayTopmostTimerActive(false);
        _mirrorOverlayTopmostTimer.Tick -= OnMirrorOverlayTopmostTimerTick;
        if (_mainHwndSource != null)
        {
            _mainHwndSource.RemoveHook(WndProc);
            _mainHwndSource = null;
        }

        _previewZoomCoordinator.Dispose();
        _previewFrameDispatcher.Dispose();
        _drawerLayoutController.Reset();
        _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        _mainWindowViewModel.Settings.PropertyChanged -= OnSettingsViewModelPropertyChanged;
        _magpieSessionController.ActiveStateChanged -= OnMirrorSessionActiveStateChanged;
        _winRtLanguagePackPrecheckCts?.Cancel();
        _winRtLanguagePackPrecheckCts?.Dispose();
        _winRtLanguagePackPrecheckCts = null;
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
        _magpieSessionController.Dispose();
        _magpieProcessService.Dispose();
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

    private bool ConfirmWinRtLanguagePackInstall(string localeTag)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.Invoke(() => ConfirmWinRtLanguagePackInstall(localeTag));
        }

        var message =
            $"WinRT OCR language pack '{localeTag}' is not installed.{Environment.NewLine}{Environment.NewLine}" +
            "Install it now? This may require administrator permission and network access.";
        var result = MessageBox.Show(
            this,
            message,
            "OCR language pack required",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }

    private void ShowWinRtLanguagePackInstallError(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowWinRtLanguagePackInstallError(message));
            return;
        }

        MessageBox.Show(this, message, "OCR language pack required", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ShowWinRtLanguagePackInstallSuccess(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowWinRtLanguagePackInstallSuccess(message));
            return;
        }

        MessageBox.Show(this, message, "OCR language pack required", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BeginWinRtLanguagePackInstallUi(string localeTag)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => BeginWinRtLanguagePackInstallUi(localeTag));
            return;
        }

        if (_winRtInstallUiDepth == 0)
        {
            _winRtInstallPrevIsBusy = _mainWindowViewModel.RuntimeStatus.IsBusy;
            _winRtInstallPrevMessage = _mainWindowViewModel.RuntimeStatus.BusyMessage;
            _winRtInstallPrevIsIndeterminate = _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate;
            _winRtInstallPrevPercent = _mainWindowViewModel.RuntimeStatus.BusyProgressPercent;
        }

        _winRtInstallUiDepth++;
        _mainWindowViewModel.RuntimeStatus.IsBusy = true;
        _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = true;
        _mainWindowViewModel.RuntimeStatus.BusyProgressPercent = 0;
        _mainWindowViewModel.RuntimeStatus.BusyMessage = $"Installing OCR language pack ({localeTag})...";
        ShowWinRtLanguagePackStatusUi(
            $"Installing WinRT OCR language pack ({localeTag})...",
            Brushes.DimGray,
            showInstallButton: false,
            enableInstallButton: false);
    }

    private void UpdateWinRtLanguagePackInstallUi(CapabilityInstallProgress progress)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateWinRtLanguagePackInstallUi(progress));
            return;
        }

        _mainWindowViewModel.RuntimeStatus.IsBusy = true;
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            _mainWindowViewModel.RuntimeStatus.BusyMessage = progress.Message;
        }

        if (progress.Percent < 0)
        {
            _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = true;
            return;
        }

        _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = false;
        _mainWindowViewModel.RuntimeStatus.BusyProgressPercent = Math.Clamp(progress.Percent, 0, 100);
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            ShowWinRtLanguagePackStatusUi(
                progress.Message,
                Brushes.DimGray,
                showInstallButton: false,
                enableInstallButton: false);
        }
    }

    private void EndWinRtLanguagePackInstallUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(EndWinRtLanguagePackInstallUi);
            return;
        }

        if (_winRtInstallUiDepth <= 0)
        {
            return;
        }

        _winRtInstallUiDepth--;
        if (_winRtInstallUiDepth > 0)
        {
            return;
        }

        if (_winRtInstallPrevIsBusy)
        {
            _mainWindowViewModel.RuntimeStatus.IsBusy = true;
            _mainWindowViewModel.RuntimeStatus.BusyMessage = _winRtInstallPrevMessage;
            _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = _winRtInstallPrevIsIndeterminate;
            _mainWindowViewModel.RuntimeStatus.BusyProgressPercent = _winRtInstallPrevPercent;
            return;
        }

        _mainWindowViewModel.RuntimeStatus.IsBusy = false;
        _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = true;
        _mainWindowViewModel.RuntimeStatus.BusyProgressPercent = 0;
        ScheduleWinRtLanguagePackPrecheck();
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.OcrEngineTag) or
            nameof(SettingsViewModel.SourceLanguageTag) or
            nameof(SettingsViewModel.SourceLanguageCustom))
        {
            ScheduleWinRtLanguagePackPrecheck();
        }
    }

    private void ScheduleWinRtLanguagePackPrecheck()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(ScheduleWinRtLanguagePackPrecheck);
            return;
        }

        if (!IsLoaded || _isClosing)
        {
            return;
        }

        var version = Interlocked.Increment(ref _winRtLanguagePackPrecheckVersion);
        _winRtLanguagePackPrecheckCts?.Cancel();
        _winRtLanguagePackPrecheckCts?.Dispose();
        _winRtLanguagePackPrecheckCts = new CancellationTokenSource();
        var token = _winRtLanguagePackPrecheckCts.Token;
        _ = RefreshWinRtLanguagePackPrecheckUiAsync(version, token);
    }

    private async Task RefreshWinRtLanguagePackPrecheckUiAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(_mainWindowViewModel.Settings.OcrEngineTag, "WinRt", StringComparison.OrdinalIgnoreCase))
            {
                HideWinRtLanguagePackStatusUi();
                return;
            }

            ShowWinRtLanguagePackStatusUi(
                "WinRT OCR language pack: checking...",
                Brushes.DimGray,
                showInstallButton: false,
                enableInstallButton: false);
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested || version != _winRtLanguagePackPrecheckVersion)
            {
                return;
            }

            var sourceLanguage = ResolveSelectedSourceLanguageForPrecheck();
            var locale = WinRtLanguageResolver.ResolveOcrLocale(sourceLanguage);
            if (string.IsNullOrWhiteSpace(locale))
            {
                ShowWinRtLanguagePackStatusUi(
                    "WinRT OCR language pack: source language is not set.",
                    Brushes.DarkOrange,
                    showInstallButton: false,
                    enableInstallButton: false);
                return;
            }

            var supported = WinRtOcrLanguagePackCoordinator.IsLanguageSupportedForLocale(locale);
            if (cancellationToken.IsCancellationRequested || version != _winRtLanguagePackPrecheckVersion)
            {
                return;
            }

            if (supported)
            {
                ShowWinRtLanguagePackStatusUi(
                    $"WinRT OCR language pack: available ({locale}).",
                    Brushes.DimGray,
                    showInstallButton: false,
                    enableInstallButton: false);
                return;
            }

            ShowWinRtLanguagePackStatusUi(
                $"WinRT OCR language pack: missing ({locale}).",
                Brushes.DarkOrange,
                showInstallButton: true,
                enableInstallButton: _winRtInstallUiDepth == 0);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to evaluate WinRT language-pack precheck.");
            ShowWinRtLanguagePackStatusUi(
                "WinRT OCR language pack: precheck failed.",
                Brushes.DarkOrange,
                showInstallButton: true,
                enableInstallButton: _winRtInstallUiDepth == 0);
        }
    }

    private string ResolveSelectedSourceLanguageForPrecheck()
    {
        var selectedTag = (_mainWindowViewModel.Settings.SourceLanguageTag ?? string.Empty).Trim();
        if (string.Equals(selectedTag, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return (_mainWindowViewModel.Settings.SourceLanguageCustom ?? string.Empty).Trim();
        }

        return selectedTag;
    }

    private void ShowWinRtLanguagePackStatusUi(
        string message,
        Brush foreground,
        bool showInstallButton,
        bool enableInstallButton)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowWinRtLanguagePackStatusUi(message, foreground, showInstallButton, enableInstallButton));
            return;
        }

        WinRtLanguagePackStatusText.Text = message;
        WinRtLanguagePackStatusText.Foreground = foreground;
        WinRtLanguagePackStatusText.Visibility = Visibility.Visible;
        InstallWinRtLanguagePackButton.Visibility = showInstallButton ? Visibility.Visible : Visibility.Collapsed;
        InstallWinRtLanguagePackButton.IsEnabled = enableInstallButton;
    }

    private void HideWinRtLanguagePackStatusUi()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(HideWinRtLanguagePackStatusUi);
            return;
        }

        WinRtLanguagePackStatusText.Visibility = Visibility.Collapsed;
        InstallWinRtLanguagePackButton.Visibility = Visibility.Collapsed;
        InstallWinRtLanguagePackButton.IsEnabled = false;
    }

    private async void OnInstallWinRtLanguagePackClicked(object sender, RoutedEventArgs e)
    {
        if (_winRtInstallUiDepth > 0)
        {
            return;
        }

        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);
        var settings = _settingsService.Settings;
        if (settings.OcrEngine != OcrEngineKind.WinRt)
        {
            ScheduleWinRtLanguagePackPrecheck();
            return;
        }

        var result = await _winRtLanguagePackCoordinator
            .EnsureLanguagePackAsync(settings, CancellationToken.None, enforceSessionPromptLimit: false)
            .ConfigureAwait(true);
        switch (result.Status)
        {
            case WinRtLanguagePackStatus.Ready:
                AppendLog($"WinRT OCR language pack ready: {result.LocaleTag}.");
                break;
            case WinRtLanguagePackStatus.UserCanceled:
                AppendLog($"WinRT OCR language pack install canceled: {result.LocaleTag}.");
                break;
            case WinRtLanguagePackStatus.InstallFailed:
                AppendLog($"WinRT OCR language pack install failed: {result.LocaleTag}.");
                break;
        }

        ScheduleWinRtLanguagePackPrecheck();
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
        // WHY: Lock/unlock hotkeys persist settings without going through the UI save path,
        // so we must explicitly apply hook settings here to ensure injection/attach happens.
        await _dx11HookClientService.ApplySettingsAsync(_settingsService.Settings).ConfigureAwait(true);
    }

    private async void OnUnlockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleUnlockCaptureWindowHotkeyAsync().ConfigureAwait(true);
        ClearPinnedCaptureThumbnail("No fixed target");
        // WHY: Explicitly stop (detach) the hook when the fixed target is cleared, regardless of fallback settings.
        await _dx11HookClientService.StopAsync().ConfigureAwait(true);
    }

    private async void OnToggleMirrorFullscreenHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleToggleMirrorFullscreenHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnSelectRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleSelectRoiHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnToggleOverlayHotkeyPressed(object? sender, EventArgs e)
    {
        _hotkeyCommandController.HandleToggleOverlayHotkey();
        // WHY: WPF overlay and hook overlay should stay in sync by default to reduce confusion.
        // This does not persist settings; it only updates the hook runtime config mapping.
        var settings = _settingsService.Settings;
        if (settings.EnableDx11HookPipeline && settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowProcessId > 0)
        {
            var effectiveHookOverlayEnabled = settings.Dx11HookOverlayEnabled && _overlayEnabled;
            var published = _dx11HookClientService.TryPublishRuntimeConfig(
                settings.FixedCaptureWindowProcessId,
                settings.Dx11HookCaptureFpsLimit,
                effectiveHookOverlayEnabled,
                out var failureReason);
            _logger?.Info(
                $"stage=dx11_hook event=runtime_config_publish pid={settings.FixedCaptureWindowProcessId} " +
                $"fps_limit={settings.Dx11HookCaptureFpsLimit} overlay={effectiveHookOverlayEnabled} " +
                $"result={(published ? "ok" : "failed")} reason={(published ? "none" : failureReason ?? "unknown")}.");
            if (!published)
            {
                // WHY: F9 toggle should self-heal even when runtime publish misses; re-apply reattaches and rewrites config.
                await _dx11HookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
            }
        }
    }

    private void EnableOverlay()
    {
        if (_overlayPresenter == null || _overlayEnabled)
        {
            return;
        }

        var settings = _settingsService.Settings;
        TryClearHookOverlayForReshow(settings, "enable_overlay");
        _overlayEnabled = true;
        _overlayPresenter.SetEnabled(true, showLast: false);
        AppendLog("Overlay shown.");

        if (settings.EnableDx11HookPipeline && settings.EnableFixedCaptureWindow && settings.FixedCaptureWindowProcessId > 0)
        {
            var effectiveHookOverlayEnabled = settings.Dx11HookOverlayEnabled && _overlayEnabled;
            var published = _dx11HookClientService.TryPublishRuntimeConfig(
                settings.FixedCaptureWindowProcessId,
                settings.Dx11HookCaptureFpsLimit,
                effectiveHookOverlayEnabled,
                out var failureReason);
            _logger?.Info(
                $"stage=dx11_hook event=runtime_config_publish source=enable_overlay pid={settings.FixedCaptureWindowProcessId} " +
                $"fps_limit={settings.Dx11HookCaptureFpsLimit} overlay={effectiveHookOverlayEnabled} " +
                $"result={(published ? "ok" : "failed")} reason={(published ? "none" : failureReason ?? "unknown")}.");
            if (!published)
            {
                // WHY: F8/F10 run should restore hook overlay visibility after F9 hide, even when best-effort publish misses.
                _ = _dx11HookClientService.ApplySettingsAsync(settings);
            }
        }
    }

    private void TryClearHookOverlayForReshow(AppSettings settings, string source)
    {
        if (!settings.EnableDx11HookPipeline || !settings.Dx11HookOverlayEnabled)
        {
            return;
        }

        if (!settings.EnableFixedCaptureWindow || settings.FixedCaptureWindowProcessId <= 0 || _captureManager == null)
        {
            return;
        }

        var bounds = _captureManager.GetCaptureBounds(settings);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            _logger?.Info(
                $"stage=dx11_hook event=overlay_reshow_clear source={source} result=skip reason=invalid_bounds.");
            return;
        }

        var canvasW = (uint)Math.Max(1, Math.Round(bounds.Width));
        var canvasH = (uint)Math.Max(1, Math.Round(bounds.Height));
        var cleared = _dx11HookClientService.TryWriteOverlayV2(
            settings.FixedCaptureWindowProcessId,
            canvasW,
            canvasH,
            ReadOnlySpan<Dx11HookOverlayV2CommandWriter.TextBlockV2>.Empty,
            Array.Empty<byte>(),
            0,
            out var failureReason);
        _logger?.Info(
            $"stage=dx11_hook event=overlay_reshow_clear source={source} pid={settings.FixedCaptureWindowProcessId} " +
            $"canvas={canvasW}x{canvasH} result={(cleared ? "ok" : "failed")} " +
            $"reason={(cleared ? "none" : failureReason ?? "unknown")}.");
    }

    private async Task RunOnceAsync()
    {
        await RunOnceAsync(ForceRunOptions.None).ConfigureAwait(true);
    }

    private async Task ToggleMirrorFullscreenHotkeyAsync()
    {
        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);
        var settings = _settingsService.Settings;
        await _magpieSessionController.ToggleAsync(settings, SaveSettingsImmediatelyAsync).ConfigureAwait(true);
        // WHY: Mirror toggle may bind foreground window, so keep UI hotkey/settings panes in sync.
        _mainWindowViewModel.Settings.LoadFrom(settings);
        UpdateAutoTranslateBadgeVisibility(settings);
    }

    private async Task RunOnceAsync(ForceRunOptions options)
    {
        // WHY: Explicit actions should observe the latest UI edits before pipeline execution.
        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);
        CheckAndShowPrerequisiteDialogs(_settingsService.Settings);
        await _runCoordinator.RunOnceAsync(options).ConfigureAwait(true);
    }

    private void SetBusyOverlay(bool visible, string? message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusyOverlay(visible, message));
            return;
        }

        _mainWindowViewModel.RuntimeStatus.IsBusy = visible;
        if (!visible)
        {
            _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = true;
            _mainWindowViewModel.RuntimeStatus.BusyProgressPercent = 0;
        }
        else
        {
            _mainWindowViewModel.RuntimeStatus.BusyProgressIsIndeterminate = true;
        }

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
        if (_isSelectingRoi)
        {
            AppendLog("ROI selection already in progress.");
            return;
        }

        // WHY: Keep ROI selection behavior consistent with F8/F10 in hook-only mode.
        // If F9 hid overlay, ROI preview would otherwise stay invisible during selection.
        EnableOverlay();

        if (_captureManager == null)
        {
            return;
        }

        _isSelectingRoi = true;
        try
        {
            var settings = _settingsService.Settings;
            var roiEnabledAtStart = settings.EnableRoi;
            var bounds = _captureManager.GetCaptureBounds(settings);
            var selector = new RoiSelectorWindow(bounds);
            if (_hookRoiTraceEnabled)
            {
                _logger?.Info(
                    $"stage=hook_roi_preview event=selector_start bounds=[{bounds.X:0.##},{bounds.Y:0.##},{bounds.Width:0.##},{bounds.Height:0.##}]");
            }
            // WHY: In hook-only mode, WPF ROI selector frame is not visible over exclusive fullscreen.
            // Stream preview rect updates to Hook overlay so the user can see the ROI frame while dragging.
            void OnPreviewRectChanged(Rect? previewRect)
            {
                _pipeline?.UpdateHookRoiPreview(previewRect);
            }

            selector.PreviewRectChanged += OnPreviewRectChanged;
            _pipeline?.UpdateHookRoiPreview(null);
            var result = selector.ShowDialog();
            selector.PreviewRectChanged -= OnPreviewRectChanged;
            if (_hookRoiTraceEnabled)
            {
                _logger?.Info(
                    $"stage=hook_roi_preview event=selector_end result={(result == true ? "confirm" : "cancel")} " +
                    $"selected={(selector.SelectedRect.HasValue ? 1 : 0)}.");
            }
            if (result == true && selector.SelectedRect is { } rect)
            {
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
                return;
            }

            var hasNormalized = settings.NormalizedRoi is { } normalized && !normalized.IsEmpty;
            var hasAbsolute = settings.Roi is { } absolute && !absolute.IsEmpty;
            if (roiEnabledAtStart && (hasNormalized || hasAbsolute))
            {
                // WHY: Escape cancel should preserve the previous ROI selection if coordinates already exist.
                if (!settings.EnableRoi)
                {
                    settings.EnableRoi = true;
                    _mainWindowViewModel.Settings.LoadFrom(settings);
                    UpdateRoiStatus(settings);
                    await _settingsService.SaveAsync().ConfigureAwait(true);
                }

                AppendLog("ROI selection canceled. Keeping previous ROI.");
                return;
            }

            // WHY: If ROI was disabled when selection started, cancel must return to disabled state.
            var changed = settings.EnableRoi ||
                          (!hasNormalized && settings.NormalizedRoi is not null) ||
                          (!hasAbsolute && settings.Roi is not null);
            settings.EnableRoi = false;
            if (!hasNormalized && !hasAbsolute)
            {
                settings.Roi = null;
                settings.NormalizedRoi = null;
            }
            if (changed)
            {
                _mainWindowViewModel.Settings.LoadFrom(settings);
                UpdateRoiStatus(settings);
                await _settingsService.SaveAsync().ConfigureAwait(true);
            }

            AppendLog(hasNormalized || hasAbsolute
                ? "ROI selection canceled. ROI kept but disabled."
                : "ROI selection canceled. ROI disabled (no previous ROI).");
        }
        finally
        {
            _pipeline?.UpdateHookRoiPreview(null);
            _isSelectingRoi = false;
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
        ScheduleWinRtLanguagePackPrecheck();
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
        var modelFileNames = _llamaModelCatalog.GetAvailableModelFileNames();
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
        var ordered = NormalizeTranslationPriority(settings.TranslationPriority, _registeredTranslationProviderNames);
        settings.TranslationPriority = ordered.ToList();
        _mainWindowViewModel.ResetTranslationPriority(ordered);
    }

    private static List<string> NormalizeTranslationPriority(
        IReadOnlyList<string>? currentPriority,
        IReadOnlyList<string> registeredProviderNames)
    {
        return TranslationFallbackService.NormalizePriority(currentPriority, registeredProviderNames);
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
            _mainWindowViewModel.GetTranslationPriorityOrDefault(_registeredTranslationProviderNames);
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
        _magpieSessionController.ApplySettings(settings);
        ApplyMirrorOverlayMapper();
        _ = _dx11HookClientService.ApplySettingsAsync(settings);
        CheckAndShowPrerequisiteDialogs(settings);
        ScheduleWinRtLanguagePackPrecheck();
    }

    private void CheckAndShowPrerequisiteDialogs(AppSettings settings)
    {
        if (settings.EnableGemini && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowPrerequisiteDialogOnce(
                "api_gemini_missing",
                "Gemini is enabled but Gemini API key is empty.\nSet the Gemini API key in settings or disable Gemini.");
        }

        if (settings.EnableDeepL && string.IsNullOrWhiteSpace(settings.DeepLApiKey))
        {
            ShowPrerequisiteDialogOnce(
                "api_deepl_missing",
                "DeepL is enabled but DeepL API key is empty.\nSet the DeepL API key in settings or disable DeepL.");
        }

        if (IsUvRequired(settings))
        {
            ShowMissingBinaryDialogIfNeeded("bin_uv_missing", FixedUvRelativePath, "OCR/Llama gRPC");
        }

        if (settings.EnableDx11HookPipeline)
        {
            ShowMissingBinaryDialogIfNeeded("bin_hookhost_missing", FixedHookHostRelativePath, "DX11 hook");
        }

        if (settings.EnableMirrorFullscreenMode)
        {
            ShowMissingBinaryDialogIfNeeded("bin_magpie_missing", FixedMagpieCoreRelativePath, "mirror fullscreen");
        }

        if (settings.EnableLlamaCppTranslation)
        {
            ShowMissingBinaryDialogIfNeeded("bin_llamaserver_missing", FixedLlamaServerRelativePath, "Llama.cpp translation");
        }
    }

    private static bool IsUvRequired(AppSettings settings)
    {
        if (settings.EnableLlamaCppTranslation)
        {
            return true;
        }

        if (settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost)
        {
            return true;
        }

        if (settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost)
        {
            return true;
        }

        return settings.OcrEngine == OcrEngineKind.Ndl && settings.EnableNdlGrpcHost;
    }

    private void ShowMissingBinaryDialogIfNeeded(string key, string relativePath, string featureName)
    {
        var fullPath = ResolveAppRelativePath(relativePath);
        if (File.Exists(fullPath))
        {
            return;
        }

        ShowPrerequisiteDialogOnce(
            key,
            $"Required file for {featureName} is missing:\n{fullPath}\n\nPlace the file at this path or disable {featureName}.");
    }

    private static string ResolveAppRelativePath(string relativePath)
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativePath));
    }

    private void ShowPrerequisiteDialogOnce(string key, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowPrerequisiteDialogOnce(key, message));
            return;
        }

        if (!_shownPrerequisiteDialogKeys.Add(key))
        {
            return;
        }

        MessageBox.Show(this, message, "Configuration required", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        HotkeyToggleMirrorFullscreenKeyBox.ItemsSource = keys;
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
                      $"Unlock={FormatHotkey(config.UnlockCaptureWindowKey, config.UnlockCaptureWindowModifiers)}, " +
                      $"Mirror={FormatHotkey(config.ToggleMirrorFullscreenKey, config.ToggleMirrorFullscreenModifiers)}.");
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
                OnUnlockCaptureWindowHotkeyPressed),
            new("MirrorFullscreen", config.ToggleMirrorFullscreenKey, config.ToggleMirrorFullscreenModifiers, 10,
                OnToggleMirrorFullscreenHotkeyPressed)
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
            ParseModifiers(settings.HotkeyUnlockCaptureWindowModifiers),
            ParseKey(settings.HotkeyToggleMirrorFullscreenKey, Key.F7),
            ParseModifiers(settings.HotkeyToggleMirrorFullscreenModifiers));
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
        if (_magpieSessionController.IsActive)
        {
            EnsureMirrorOverlayTopMost("overlay_shown");
        }
    }

    private void OnOverlayHidden() => _sceneChangeController.OnOverlayHidden();

    private void OnOverlayUpdated()
    {
        _sceneChangeController.OnOverlayUpdated();
        UpdateAutoTranslateBadgeVisibility(_settingsService.Settings);
        if (_magpieSessionController.IsActive)
        {
            EnsureMirrorOverlayTopMost("overlay_updated");
        }
    }

    private void OnMirrorSessionActiveStateChanged(bool isActive)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnMirrorSessionActiveStateChanged(isActive));
            return;
        }

        ApplyMirrorOverlayMapper();
        EnsureMirrorOverlayTopmostTimerActive(isActive);
        if (isActive)
        {
            EnsureMirrorOverlayTopMost("mirror_state_changed");
        }
        else
        {
            ResetRoiForMirrorStop();
        }
    }

    private void ApplyMirrorOverlayMapper()
    {
        if (_overlayPresenter == null)
        {
            return;
        }

        // WHY: Mirror mode now captures from Magpie scaling window directly, so source->mirror mapping
        // would become a double transform and shift overlay positions.
        _overlayPresenter.SetScreenRectMapper(null);
    }

    private void ResetRoiForMirrorStop()
    {
        if (_isClosing)
        {
            return;
        }

        var settings = _settingsService.Settings;
        var alreadyReset = !settings.EnableRoi && settings.Roi is null && settings.NormalizedRoi is null;
        if (alreadyReset)
        {
            return;
        }

        settings.EnableRoi = false;
        settings.Roi = null;
        settings.NormalizedRoi = null;
        _mainWindowViewModel.Settings.LoadFrom(settings);
        UpdateRoiStatus(settings);
        AppendLog("Mirror stopped: ROI reset (disabled).");
        _ = PersistMirrorRoiResetAsync();
    }

    private async Task PersistMirrorRoiResetAsync()
    {
        try
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to persist ROI reset after mirror stop.");
        }
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
        ModifierKeys UnlockCaptureWindowModifiers,
        Key ToggleMirrorFullscreenKey,
        ModifierKeys ToggleMirrorFullscreenModifiers)
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
            yield return ("Mirror fullscreen", ToggleMirrorFullscreenKey, ToggleMirrorFullscreenModifiers);
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
            ModifierKeys.Shift,
            Key.F7,
            ModifierKeys.Control);
    }
}



