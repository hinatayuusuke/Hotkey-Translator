using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private readonly SettingsService _settingsService;
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
    private readonly BusyOverlayController _busyOverlayController;
    private readonly AppThemeController _appThemeController = new();
    private readonly WinRtLanguagePackUiController _winRtLanguagePackUiController;
    private readonly OneOcrVendorUiController _oneOcrVendorUiController;
    private readonly LauncherSessionTargetState _launcherSessionTargetState;
    private readonly GraphicsHookClientService _graphicsHookClientService;
    private readonly GraphicsHookLauncherService _graphicsHookLauncherService;
    private readonly LauncherTargetSignatureRegistry _launcherTargetSignatureRegistry;
    private readonly LauncherDiscoveryResolver _launcherDiscoveryResolver;
    private readonly LauncherTargetResolver _launcherTargetResolver;
    private readonly IMagpieProcessService _magpieProcessService;
    private readonly IMagpieIpcClient _magpieIpcClient;
    private readonly MagpieSessionController _magpieSessionController;
    private PhashService? _phashService;
    private CancellationTokenSource? _translationOverlayCts;
    private CancellationTokenSource? _graphicsHookLauncherResolveCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private HotkeyConfig? _currentHotkeyConfig;
    private bool _currentHotkeyRawInput;
    private bool _isApplyingSettings;
    private bool _isSelectingRoi;
    private readonly DrawerLayoutController _drawerLayoutController;
    private readonly PreviewZoomCoordinator _previewZoomCoordinator;
    private readonly PreviewFrameDispatcher _previewFrameDispatcher;
    private readonly DispatcherTimer _mirrorOverlayTopmostTimer;
    private readonly DispatcherTimer _roiPresetPreviewClearTimer;
    private readonly Queue<string> _pendingUiLogMessages = new();
    private HwndSource? _mainHwndSource;
    private uint _wmMagpieScalingChanged;
    private bool _isClosing;
    private bool _startupHookLaunchHandled;
    private bool _isApplyingRoiPresetSlotSelection;
    private IReadOnlyList<string> _registeredTranslationProviderNames = Array.Empty<string>();
    private readonly bool _hookRoiTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("HT_HOOK_ROI_TRACE"), "1", StringComparison.Ordinal);
    private const int OverlayBaselineDelayMs = 150;
    private const int LogFlushIntervalMs = 150;
    private const int MaxLogLines = 1000;
    private const int TranslationOverlayDelayMs = 200;
    private const int SettingsSaveDebounceMs = 200;
    private const int GraphicsHookLauncherDiscoveryTimeoutMs = 60000;
    private const int GraphicsHookLauncherSignatureResolveTimeoutMs = 3000;
    private const double DrawerAutoResizeTolerance = 12.0;
    private const double DrawerAutoResizeFallbackHeight = 300.0;
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";
    private const string DefaultVisionLlmModelFileName = "Qwen3.5-4B-Q4_K_M.gguf";
    private const string DefaultVisionLlmMmprojFileName = "mmproj-Qwen3.5-4B-BF16.gguf";
    private const int MirrorOverlayTopmostResyncIntervalMs = 500;
    private const int RoiPresetSlotCount = 10;
    private const int RoiPresetPreviewDurationMs = 1000;
    private const string FixedUvRelativePath = "Tools\\uv\\uv.exe";
    private const string FixedHookHostRelativePath = "Native\\HookHost\\bin\\HookHost.exe";
    private const string FixedHookHostX86RelativePath = "Native\\HookHost\\bin\\x86\\HookHost.exe";
    private const string FixedHookAgentDx9RelativePath = "Native\\HookHost\\bin\\HookAgentDx9.dll";
    private const string FixedHookAgentDx9X86RelativePath = "Native\\HookHost\\bin\\x86\\HookAgentDx9.dll";
    private const string FixedHookAgentDx11RelativePath = "Native\\HookHost\\bin\\HookAgentDx11.dll";
    private const string FixedHookAgentDx11X86RelativePath = "Native\\HookHost\\bin\\x86\\HookAgentDx11.dll";
    private const string FixedHookAgentVulkanRelativePath = "Native\\HookHost\\bin\\HookAgentVulkan.dll";
    private const string FixedHookAgentVulkanX86RelativePath = "Native\\HookHost\\bin\\x86\\HookAgentVulkan.dll";
    private const string FixedMagpieCoreRelativePath = "Tools\\Magpie\\Magpie.Core.exe";
    private const string FixedLlamaServerRelativePath = "TranslationServiceLlama\\LlamaCpp\\llama-server.exe";
    private static LocalizationService Localizer => LocalizationService.Instance;

    public MainWindow()
        : this(new SettingsService())
    {
    }

    public MainWindow(SettingsService settingsService)
    {
        _settingsService = settingsService;
        LocalizationService.Instance.ApplyUiLanguage(_settingsService.Settings.UiLanguage);
        _settingsUiController = new SettingsUiController(_settingsService, this, () => _logger, ApplyViewModelInputToSettings);
        // WHY: XAML initialization can raise ValueChanged handlers before constructor finishes.
        _settingsChangeScheduler = new SettingsChangeScheduler(
            Dispatcher,
            SaveSettingsCoreAsync,
            TimeSpan.FromMilliseconds(SettingsSaveDebounceMs),
            ex => _logger?.Error(ex, "Failed to save settings from debounce scheduler."));
        InitializeComponent();
        _appThemeController.Apply(_settingsService.Settings, this);
        var roiPresetSlotOptions = BuildRoiPresetSlotOptions();
        OverviewControl.RoiPresetSlotItemsSource = roiPresetSlotOptions;
        _mirrorOverlayTopmostTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(MirrorOverlayTopmostResyncIntervalMs)
        };
        _mirrorOverlayTopmostTimer.Tick += OnMirrorOverlayTopmostTimerTick;
        _roiPresetPreviewClearTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(RoiPresetPreviewDurationMs)
        };
        _roiPresetPreviewClearTimer.Tick += OnRoiPresetPreviewClearTimerTick;
        _mainWindowViewModel = new MainWindowViewModel(
            new SettingsViewModel(_settingsChangeScheduler),
            new RuntimeStatusViewModel(),
            RunOnceAsync,
            () => _runCoordinator?.CancelCurrentRun(),
            SelectRoiAsync,
            SwapLanguages,
            RequestSettingsSave,
            ReloadLlamaModelsAsync,
            RestartLlamaCppAsync,
            StopLlamaServerAsync,
            ReloadVisionLlmModelsAsync,
            RestartVisionLlmAsync,
            StopVisionLlmAsync,
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
            () => RuntimeLogsControl.DrawerActualHeight,
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
        Localizer.LanguageChanged += OnLocalizationLanguageChanged;
        _busyOverlayController = new BusyOverlayController(Dispatcher, _mainWindowViewModel.RuntimeStatus);
        _hotkeyController = new HotkeyController(this, () => _logger, FormatHotkey);
        _launcherSessionTargetState = new LauncherSessionTargetState(() => _logger);
        _graphicsHookClientService = new GraphicsHookClientService(() => _logger, _launcherSessionTargetState);
        _graphicsHookLauncherService = new GraphicsHookLauncherService();
        _launcherTargetSignatureRegistry = new LauncherTargetSignatureRegistry();
        _launcherDiscoveryResolver = new LauncherDiscoveryResolver(() => _logger, _launcherSessionTargetState);
        _launcherTargetResolver = new LauncherTargetResolver(() => _logger);
        _magpieProcessService = new MagpieProcessService();
        _magpieIpcClient = new MagpieIpcClient();
        _magpieSessionController = new MagpieSessionController(
            _windowBindingService,
            _magpieProcessService,
            _magpieIpcClient,
            () => _logger,
            AppendLog);
        _magpieSessionController.ActiveStateChanged += OnMirrorSessionActiveStateChanged;
        _uiLogViewAdapter = new UiLogViewAdapter(() => RuntimeLogsControl.LogTextBox, MaxLogLines);
        _uiLogController = new UiLogController(Dispatcher, _uiLogViewAdapter.FlushPayload, LogFlushIntervalMs);
        FlushPendingUiLogs();
        _winRtLanguagePackUiController = new WinRtLanguagePackUiController(
            this,
            Dispatcher,
            _mainWindowViewModel.Settings,
            () => _settingsService.Settings,
            () => _logger,
            () => _winRtLanguagePackCoordinator!,
            _busyOverlayController,
            FlushPendingSettingsSaveAsync,
            AppendLog,
            () => IsLoaded,
            () => _isClosing,
            OverviewControl.WinRtLanguagePackStatusTextBlock,
            OverviewControl.InstallWinRtLanguagePackButtonElement);
        _winRtLanguagePackCoordinator = new WinRtOcrLanguagePackCoordinator(
            () => _logger,
            new WindowsCapabilityInstaller(() => _logger),
            _winRtLanguagePackUiController.ConfirmInstall,
            _winRtLanguagePackUiController.ShowInstallError,
            _winRtLanguagePackUiController.ShowInstallSuccess,
            _winRtLanguagePackUiController.BeginInstallUi,
            _winRtLanguagePackUiController.UpdateInstallUi,
            _winRtLanguagePackUiController.EndInstallUi);
        _oneOcrVendorUiController = new OneOcrVendorUiController(
            this,
            Dispatcher,
            _busyOverlayController,
            () => _logger,
            AppendLog);
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
            SelectFixedOverlayFrameAsync,
            () => ChangeRoiPresetByOffsetAsync(1, "hotkey"),
            () => ChangeRoiPresetByOffsetAsync(-1, "hotkey"),
            (offset, options) => RunRoiPresetWithOffsetAsync(offset, options),
            () => _overlayPresenter,
            () => _overlayEnabled,
            enabled => _overlayEnabled = enabled,
            ToggleMirrorFullscreenHotkeyAsync,
            AppendLog);
        sceneChangeController = _sceneChangeController;
        PopulateHotkeyKeyBoxes();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _appThemeController.Apply(_settingsService.Settings, this);
        _wmMagpieScalingChanged = _magpieSessionController.MagpieScalingChangedMessageId;
        _mainHwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _mainHwndSource?.AddHook(WndProc);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger = new AppLogger(AppendLog);
        InitializeLogBuffer();
        if (!_settingsService.IsLoaded)
        {
            await _settingsService.LoadAsync().ConfigureAwait(true);
        }
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
        var bootstrapConfirmation = await ConfirmResourceBootstrapAsync(settings, ResourceBootstrapIntent.AppLoad).ConfigureAwait(true);
        settingsChanged |= bootstrapConfirmation.SettingsChanged;
        if (bootstrapConfirmation.Approved)
        {
            settingsChanged |= await _resourceHostFacade.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
        }
        else
        {
            AppendLog("Resource host startup skipped because setup/download was canceled.");
        }
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
        _captureManager = new CaptureManager(frameGate, _logger, _launcherSessionTargetState);
        UpdateAutoTranslateBadgeVisibility(settings);
        _ocrEngine = new OcrEngine(_httpClient, _logger);
        var ocrDiff = new OcrDiffService { IouThreshold = settings.OcrIouThreshold };
        _phashService = new PhashService();
        var normalization = new NormalizationService();
        var ocrPreprocess = new OcrPreprocessService();
        var lineGrouper = new OcrLineGrouper(_logger);
        _sceneTextSnapshotService = new SceneTextSnapshotService(_captureManager, _ocrEngine, lineGrouper, _logger);
        var userGlossaryService = new UserGlossaryService(_settingsService.UserGlossaryDirectoryPath, _logger);
        var keyBuilder = new CacheKeyBuilder(userGlossaryService);
        var translationService = new TranslationFallbackService(translationProviders, _logger);

        _pipeline = new PipelineOrchestrator(
            _captureManager,
            _graphicsHookClientService,
            _launcherSessionTargetState,
            _ocrEngine,
            ocrDiff,
            _phashService,
            normalization,
            ocrPreprocess,
            lineGrouper,
            _cacheRepository,
            keyBuilder,
            translationService,
            userGlossaryService,
            geminiClient,
            _overlayPresenter,
            _settingsService,
            _logger);
        _pipeline.OcrPreprocessPreviewReady += OnOcrPreprocessPreviewReady;
        _pipeline.TranslationStarted += OnTranslationStarted;
        _pipeline.TranslationCompleted += OnTranslationCompleted;

        InitializeHotkeys(settings);
        InitializeAutoHideWatcher(settings);
        await _graphicsHookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
        AppendLog("Ready. F6: select ROI. F7: lock window. Shift+F7: unlock window. Ctrl+Shift+F7: mirror fullscreen. F8: run once. F9: toggle overlay. F10: force run. Other hotkeys: Disabled by default.");
        await TryHandleStartupHookLaunchAsync(settings).ConfigureAwait(true);
        _drawerLayoutController.SyncForCurrentState();
        _winRtLanguagePackUiController.Start();
        _winRtLanguagePackUiController.SchedulePrecheck();
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
        _launcherSessionTargetState.Clear("window_closed");
        EnsureMirrorOverlayTopmostTimerActive(false);
        _mirrorOverlayTopmostTimer.Tick -= OnMirrorOverlayTopmostTimerTick;
        _roiPresetPreviewClearTimer.Stop();
        _roiPresetPreviewClearTimer.Tick -= OnRoiPresetPreviewClearTimerTick;
        if (_mainHwndSource != null)
        {
            _mainHwndSource.RemoveHook(WndProc);
            _mainHwndSource = null;
        }

        _previewZoomCoordinator.Dispose();
        _previewFrameDispatcher.Dispose();
        _drawerLayoutController.Reset();
        _mainWindowViewModel.PropertyChanged -= OnMainWindowViewModelPropertyChanged;
        Localizer.LanguageChanged -= OnLocalizationLanguageChanged;
        _magpieSessionController.ActiveStateChanged -= OnMirrorSessionActiveStateChanged;
        _winRtLanguagePackUiController.Dispose();
        _runCoordinator.Dispose();
        _settingsChangeScheduler.CancelPending();
        _settingsChangeScheduler.Dispose();
        _translationOverlayCts?.Cancel();
        _translationOverlayCts?.Dispose();
        _graphicsHookLauncherResolveCts?.Cancel();
        _graphicsHookLauncherResolveCts?.Dispose();
        try
        {
            _graphicsHookClientService.StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // WHY: Shutdown path should continue even if hook host pipe is unavailable.
            _logger?.Error(ex, "Failed to stop Graphics hook client.");
        }
        _graphicsHookClientService.Dispose();
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

        MessageBox.Show(this, message, Localizer.GetString("Dialog_LoadFailed_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async void OnResetAllSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            Localizer.GetString("Dialog_ResetAll_Message"),
            Localizer.GetString("Dialog_ResetAll_Title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            AppendLog("Reset all settings canceled.");
            return;
        }

        _mainWindowViewModel.Settings.CancelPendingSave();
        var defaults = SettingsService.CreateDefaultSettings();
        _settingsUiController.NormalizeOnLoad(defaults);
        // WHY: Route reset through the normal save pipeline so validation, host setup, and rollback
        // behavior stay identical to a regular settings edit.
        _mainWindowViewModel.Settings.LoadFrom(defaults);
        var saved = await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
        AppendLog(saved
            ? "All settings reset to defaults."
            : "Reset all settings did not complete.");
    }

    private async void OnInstallWinRtLanguagePackClicked(object sender, RoutedEventArgs e)
    {
        await _winRtLanguagePackUiController.InstallNowAsync().ConfigureAwait(true);
    }

    private async void OnSelectFixedOverlayFrameClicked(object sender, RoutedEventArgs e)
    {
        await SelectFixedOverlayFrameAsync().ConfigureAwait(true);
    }

    private async void OnBrowseGraphicsHookLauncherExeClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable files (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            Title = "Select Graphics Hook target executable"
        };

        var current = (_mainWindowViewModel.Settings.GraphicsHookLauncherExePath ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            try
            {
                dialog.InitialDirectory = Path.GetDirectoryName(current);
                dialog.FileName = Path.GetFileName(current);
            }
            catch
            {
                // NOTE: Invalid path input should not break file dialog interaction.
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _mainWindowViewModel.Settings.GraphicsHookLauncherExePath = dialog.FileName;
        await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
        AppendLog($"stage=graphics_hook event=launcher_path_selected path=\"{dialog.FileName}\".");
    }

    private async void OnLaunchGraphicsHookLauncherClicked(object sender, RoutedEventArgs e)
    {
        await LaunchConfiguredGraphicsHookTargetAsync().ConfigureAwait(true);
    }

    private void OnCopySteamLaunchOptionsClicked(object sender, RoutedEventArgs e)
    {
        var launchOptions = BuildSteamLaunchOptions();
        if (string.IsNullOrWhiteSpace(launchOptions))
        {
            AppendLog("stage=graphics_hook event=steam_launch_options_copy_failed reason=process_path_unresolved.");
            return;
        }

        try
        {
            Clipboard.SetText(launchOptions);
            AppendLog($"stage=graphics_hook event=steam_launch_options_copied value=\"{launchOptions}\".");
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to copy Steam launch options to clipboard.");
            AppendLog("stage=graphics_hook event=steam_launch_options_copy_failed reason=clipboard_set_failed.");
        }
    }

    private static string BuildSteamLaunchOptions()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(processPath))
        {
            return string.Empty;
        }

        return $"\"{processPath}\" --hook-launch -- %command%";
    }

    private async Task LaunchConfiguredGraphicsHookTargetAsync()
    {
        await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
        var settings = _settingsService.Settings;
        if (!IsGraphicsHookLauncherModeEnabled(settings))
        {
            AppendLog("Graphics Hook launcher start skipped: enable Graphics hook pipeline + Graphics Hook launcher.");
            return;
        }

        var exePath = (settings.GraphicsHookLauncherExePath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            AppendLog($"Graphics Hook launcher start failed: executable not found. path=\"{exePath}\".");
            return;
        }

        if (!_graphicsHookLauncherService.TryLaunchSuspended(
                exePath,
                settings.GraphicsHookLauncherArgs,
                out var launched,
                out var launchFailureReason) ||
            launched == null)
        {
            AppendLog($"stage=graphics_hook event=launcher_fail reason={launchFailureReason ?? "launch_failed"}.");
            return;
        }

        await AttachAndResumeLaunchedProcessAsync(
                launched,
                exePath,
                settings.GraphicsHookLauncherArgs ?? string.Empty,
                "settings_ui")
            .ConfigureAwait(true);
    }

    private async Task TryHandleStartupHookLaunchAsync(AppSettings settings)
    {
        if (_startupHookLaunchHandled)
        {
            return;
        }

        _startupHookLaunchHandled = true;
        var startupArgs = Environment.GetCommandLineArgs();
        if (!TryParseHookLaunchTargetTokens(startupArgs, out var targetCommandTokens, out var parseFailureReason))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(parseFailureReason))
        {
            AppendLog($"stage=graphics_hook event=launcher_fail source=launch_options reason={parseFailureReason}.");
            return;
        }

        if (!settings.EnableGraphicsHookPipeline)
        {
            AppendLog("stage=graphics_hook event=launcher_skip source=launch_options reason=graphics_hook_pipeline_disabled.");
            return;
        }

        if (!_graphicsHookLauncherService.TryLaunchSuspendedFromCommandTokens(
                targetCommandTokens,
                out var launched,
                out var resolvedExePath,
                out var resolvedArgs,
                out var launchFailureReason) ||
            launched == null ||
            string.IsNullOrWhiteSpace(resolvedExePath))
        {
            AppendLog($"stage=graphics_hook event=launcher_fail source=launch_options reason={launchFailureReason ?? "launch_failed"}.");
            return;
        }

        AppendLog($"stage=graphics_hook event=launcher_cli_detected source=launch_options api={settings.GraphicsHookApi}.");
        await AttachAndResumeLaunchedProcessAsync(
                launched,
                resolvedExePath,
                resolvedArgs ?? string.Empty,
                "launch_options")
            .ConfigureAwait(true);
    }

    private async Task AttachAndResumeLaunchedProcessAsync(
        GraphicsHookLauncherService.SuspendedProcess launched,
        string targetExePath,
        string targetArgsForLog,
        string source)
    {
        using (launched)
        {
            var settings = _settingsService.Settings;
            var expectedProcessName = Path.GetFileNameWithoutExtension(targetExePath) ?? string.Empty;
            _launcherSessionTargetState.Clear("launcher_restart");
            AppendLog(
                $"stage=graphics_hook event=launcher_start source={source} pid={launched.ProcessId} exe=\"{targetExePath}\" args=\"{targetArgsForLog}\".");
            var provisionalAttached = await TryApplyProvisionalLauncherAttachAsync(
                    settings,
                    launched.ProcessId,
                    expectedProcessName,
                    source)
                .ConfigureAwait(true);

            if (!launched.Resume(out var resumeFailureReason))
            {
                AppendLog(
                    $"stage=graphics_hook event=launcher_fail source={source} pid={launched.ProcessId} reason={resumeFailureReason ?? "resume_failed"}.");
                if (launched.TryTerminate(1, out var terminateReason))
                {
                    AppendLog(
                        $"stage=graphics_hook event=launcher_cleanup source={source} pid={launched.ProcessId} action=terminate result=ok.");
                }
                else
                {
                    AppendLog(
                        $"stage=graphics_hook event=launcher_cleanup source={source} pid={launched.ProcessId} action=terminate result=failed reason={terminateReason ?? "unknown"}.");
                }

                _launcherSessionTargetState.Clear("launcher_resume_failed");
                return;
            }

            AppendLog($"stage=graphics_hook event=launcher_resume source={source} pid={launched.ProcessId}.");
            _graphicsHookLauncherResolveCts?.Cancel();
            _graphicsHookLauncherResolveCts?.Dispose();
            _graphicsHookLauncherResolveCts = new CancellationTokenSource();
            await ResolveAndAttachLauncherTargetAsync(
                    launched.ProcessId,
                    targetExePath,
                    source,
                    provisionalAttached,
                    _graphicsHookLauncherResolveCts.Token)
                .ConfigureAwait(true);
        }
    }

    private async Task ResolveAndAttachLauncherTargetAsync(
        int bootstrapPid,
        string targetExePath,
        string source,
        bool provisionalAttached,
        CancellationToken cancellationToken)
    {
        var expectedProcessName = Path.GetFileNameWithoutExtension(targetExePath) ?? string.Empty;
        var settings = _settingsService.Settings;
        GraphicsHookLauncherTargetSignature? signature = null;

        try
        {
            signature = await _launcherTargetSignatureRegistry
                .TryLoadAsync(expectedProcessName, targetExePath, cancellationToken)
                .ConfigureAwait(true);

            if (signature != null)
            {
                AppendLog(
                    $"stage=graphics_hook event=signature_reused source={source} exe=\"{expectedProcessName}\" key=\"{signature.Key}\".");
                var fastPath = await _launcherTargetResolver
                    .ResolveAsync(
                        signature,
                        expectedProcessName,
                        targetExePath,
                        TimeSpan.FromMilliseconds(GraphicsHookLauncherSignatureResolveTimeoutMs),
                        cancellationToken)
                    .ConfigureAwait(true);
                if (fastPath.Success)
                {
                    var persistResolvedTarget = ShouldPersistResolvedLauncherTarget(fastPath);
                    await CommitResolvedLauncherTargetAsync(
                            settings,
                            fastPath,
                            signature,
                            bootstrapPid,
                            source,
                            provisionalAttached,
                            persistSettings: persistResolvedTarget)
                        .ConfigureAwait(true);
                    return;
                }

                AppendLog(
                    $"stage=graphics_hook event=signature_reuse_failed source={source} pid={bootstrapPid} reason={fastPath.Reason}.");
            }

            var discovery = await _launcherDiscoveryResolver
                .DiscoverAsync(
                    expectedProcessName,
                    targetExePath,
                    TimeSpan.FromMilliseconds(GraphicsHookLauncherDiscoveryTimeoutMs),
                    cancellationToken)
                .ConfigureAwait(true);
            if (!discovery.Success)
            {
                AppendLog(
                    $"stage=graphics_hook event=launcher_fail source={source} pid={bootstrapPid} reason={discovery.Reason}.");
            }
            else
            {
                signature = BuildDiscoveredSignature(discovery, targetExePath, settings.GraphicsHookApi);
                var persistResolvedTarget = ShouldPersistResolvedLauncherTarget(discovery);
                AppendLog(
                    $"stage=graphics_hook event=discovery_signature_prepare source={source} pid={discovery.ProcessId} hwnd=0x{discovery.Hwnd.ToInt64():X} class=\"{discovery.WindowClass}\" classLen={discovery.WindowClass.Length} title=\"{discovery.WindowTitle}\" titleLen={discovery.WindowTitle.Length}.");
                if (persistResolvedTarget)
                {
                    await _launcherTargetSignatureRegistry
                        .SaveOrUpdateAsync(signature, cancellationToken)
                        .ConfigureAwait(true);
                    AppendLog(
                        $"stage=graphics_hook event=discovery_saved source={source} pid={discovery.ProcessId} key=\"{signature.Key}\" class=\"{signature.WindowClassAllowList.FirstOrDefault() ?? string.Empty}\" classCount={signature.WindowClassAllowList.Length} title=\"{signature.WindowTitleContainsAny.FirstOrDefault() ?? string.Empty}\" titleCount={signature.WindowTitleContainsAny.Length}.");
                }
                await CommitResolvedLauncherTargetAsync(
                        settings,
                        discovery,
                        signature,
                        bootstrapPid,
                        source,
                        provisionalAttached,
                        persistSettings: persistResolvedTarget)
                    .ConfigureAwait(true);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // WHY: Launcher resolution is canceled when the app closes or a new launcher request supersedes the current one.
            return;
        }
        catch (Exception ex)
        {
            _logger?.Error(
                ex,
                $"stage=graphics_hook event=launcher_resolution_failed source={source} bootstrapPid={bootstrapPid}.");
        }

        await CommitProvisionalLauncherTargetAsync(
                settings,
                bootstrapPid,
                targetExePath,
                source,
                provisionalAttached)
            .ConfigureAwait(true);
    }

    private async Task<bool> TryApplyProvisionalLauncherAttachAsync(
        AppSettings baseSettings,
        int provisionalPid,
        string processName,
        string source)
    {
        try
        {
            _launcherSessionTargetState.SetProvisional(provisionalPid, processName, source);
            var provisionalSettings = BuildLauncherAttachSettings(baseSettings, provisionalPid, processName);
            await _graphicsHookClientService.ApplySettingsAsync(provisionalSettings).ConfigureAwait(true);
            AppendLog(
                $"stage=graphics_hook event=launcher_provisional_attach source={source} pid={provisionalPid} api={provisionalSettings.GraphicsHookApi}.");
            return true;
        }
        catch (Exception ex)
        {
            _launcherSessionTargetState.Clear("provisional_attach_failed");
            _logger?.Error(
                ex,
                $"stage=graphics_hook event=launcher_provisional_attach_failed source={source} pid={provisionalPid}.");
            return false;
        }
    }

    private async Task CommitResolvedLauncherTargetAsync(
        AppSettings settings,
        LauncherTargetResolutionResult resolution,
        GraphicsHookLauncherTargetSignature? signature,
        int bootstrapPid,
        string source,
        bool provisionalAttached,
        bool persistSettings)
    {
        if (resolution.Hwnd == IntPtr.Zero)
        {
            // WHY: A process-only match is not enough to bind GraphicsHook capture.
            // Keep the provisional attach alive, but do not persist unresolved metadata.
            await CommitProvisionalLauncherTargetAsync(
                    settings,
                    bootstrapPid,
                    resolution.ExePath,
                    source,
                    provisionalAttached)
                .ConfigureAwait(true);
            return;
        }

        _launcherSessionTargetState.Confirm(resolution, source);
        var state = resolution.ProcessId != bootstrapPid
            ? "handoff"
            : resolution.Hwnd != IntPtr.Zero
                ? "same_pid_window_confirmed"
                : "same_pid_provisional_confirmed";
        var requiresReattach = !provisionalAttached || resolution.ProcessId != bootstrapPid;

        AppendLog(
            $"stage=graphics_hook event=launcher_resolution_result source={source} state={state} pid={resolution.ProcessId} hwnd=0x{resolution.Hwnd.ToInt64():X} reason={resolution.Reason}.");
        AppendLog(
            $"stage=graphics_hook event=launcher_bound source={source} pid={resolution.ProcessId} api={settings.GraphicsHookApi} hwnd=0x{resolution.Hwnd.ToInt64():X}.");

        if (persistSettings)
        {
            ApplyLauncherTargetToSettings(settings, resolution, signature);
            _mainWindowViewModel.Settings.LoadFrom(settings);
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }

        if (requiresReattach)
        {
            if (resolution.ProcessId != bootstrapPid)
            {
                AppendLog(
                    $"stage=graphics_hook event=launcher_handoff_attach source={source} oldPid={bootstrapPid} newPid={resolution.ProcessId} reason={resolution.Reason}.");
            }

            await _graphicsHookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
        }

        AppendLog(
            $"stage=graphics_hook event=launcher_attach_ok source={source} pid={resolution.ProcessId}.");
        AppendLog(
            $"stage=graphics_hook event=launcher_handoff_commit source={source} pid={resolution.ProcessId} persisted={(persistSettings ? 1 : 0)}.");
    }

    private async Task CommitProvisionalLauncherTargetAsync(
        AppSettings settings,
        int bootstrapPid,
        string targetExePath,
        string source,
        bool provisionalAttached)
    {
        var provisionalResolution = new LauncherTargetResolutionResult(
            true,
            bootstrapPid,
            IntPtr.Zero,
            Path.GetFileNameWithoutExtension(targetExePath) ?? string.Empty,
            targetExePath,
            string.Empty,
            string.Empty,
            0,
            0,
            false,
            "provisional_only");
        _launcherSessionTargetState.SetProvisional(bootstrapPid, provisionalResolution.ProcessName, source);
        AppendLog(
            $"stage=graphics_hook event=launcher_resolution_result source={source} state=provisional_only pid={bootstrapPid} reason=discovery_unconfirmed.");
        AppendLog(
            $"stage=graphics_hook event=launcher_bound source={source} pid={bootstrapPid} api={settings.GraphicsHookApi} hwnd=0x0.");

        if (!provisionalAttached)
        {
            await _graphicsHookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
        }

        AppendLog(
            $"stage=graphics_hook event=launcher_attach_ok source={source} pid={bootstrapPid}.");
        AppendLog(
            $"stage=graphics_hook event=launcher_handoff_commit source={source} pid={bootstrapPid} persisted=0.");
    }

    private static AppSettings BuildLauncherAttachSettings(
        AppSettings source,
        int processId,
        string processName)
    {
        return new AppSettings
        {
            CaptureMode = Hotkey_Translator.Models.CaptureMode.ActiveWindow,
            EnableGraphicsHookPipeline = source.EnableGraphicsHookPipeline,
            EnableGraphicsHookLauncher = source.EnableGraphicsHookLauncher,
            EnableFixedCaptureWindow = true,
            FixedCaptureWindowHandle = 0,
            FixedCaptureWindowProcessId = processId,
            FixedCaptureWindowProcessName = processName,
            FixedCaptureWindowClassName = string.Empty,
            FixedCaptureWindowTitle = string.Empty,
            GraphicsHookApi = source.GraphicsHookApi,
            GraphicsHookCaptureFpsLimit = source.GraphicsHookCaptureFpsLimit,
            GraphicsHookOverlayEnabled = source.GraphicsHookOverlayEnabled,
            GraphicsHookFallbackOnError = source.GraphicsHookFallbackOnError,
            EnableGraphicsHookPerfDiagLog = source.EnableGraphicsHookPerfDiagLog,
            EnableGraphicsHookDiagFileSink = source.EnableGraphicsHookDiagFileSink,
            GraphicsHookPipeName = source.GraphicsHookPipeName
        };
    }

    private static bool ShouldPersistResolvedLauncherTarget(LauncherTargetResolutionResult resolution)
    {
        if (!resolution.Success || resolution.Hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (resolution.MonitorSizedWindowObserved)
        {
            return true;
        }

        return resolution.Reason.StartsWith("signature_", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyLauncherTargetToSettings(
        AppSettings settings,
        LauncherTargetResolutionResult resolution,
        GraphicsHookLauncherTargetSignature? signature)
    {
        settings.EnableFixedCaptureWindow = true;
        settings.FixedCaptureWindowHandle = resolution.Hwnd.ToInt64();
        settings.FixedCaptureWindowProcessId = resolution.ProcessId;
        settings.FixedCaptureWindowProcessName = !string.IsNullOrWhiteSpace(resolution.ProcessName)
            ? resolution.ProcessName
            : Path.GetFileNameWithoutExtension(signature?.ExeName ?? string.Empty) ?? string.Empty;
        settings.FixedCaptureWindowClassName = resolution.Hwnd != IntPtr.Zero
            ? GetResolvedOrSignatureMetadataValue(resolution.WindowClass, signature?.WindowClassAllowList)
            : string.Empty;
        settings.FixedCaptureWindowTitle = resolution.Hwnd != IntPtr.Zero
            ? GetResolvedOrSignatureMetadataValue(resolution.WindowTitle, signature?.WindowTitleContainsAny)
            : string.Empty;
    }

    private static string GetResolvedOrSignatureMetadataValue(
        string resolvedValue,
        IReadOnlyList<string>? signatureValues)
    {
        if (IsUsableLauncherMetadataValue(resolvedValue))
        {
            return resolvedValue;
        }

        if (signatureValues == null)
        {
            return string.Empty;
        }

        foreach (var candidate in signatureValues)
        {
            if (IsUsableLauncherMetadataValue(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsUsableLauncherMetadataValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }

    private static GraphicsHookLauncherTargetSignature BuildDiscoveredSignature(
        LauncherTargetResolutionResult resolution,
        string targetExePath,
        GraphicsHookApiKind api)
    {
        var exeName = Path.GetFileNameWithoutExtension(targetExePath) ?? string.Empty;
        return new GraphicsHookLauncherTargetSignature
        {
            Key = BuildLauncherSignatureKey(exeName, targetExePath),
            ExeName = exeName,
            ExePathSuffix = targetExePath,
            PreferredApi = api,
            WindowClassAllowList = !IsUsableLauncherMetadataValue(resolution.WindowClass)
                ? Array.Empty<string>()
                : [resolution.WindowClass],
            WindowTitleContainsAny = !IsUsableLauncherMetadataValue(resolution.WindowTitle)
                ? Array.Empty<string>()
                : [resolution.WindowTitle],
            RequireVisibleTopLevel = true,
            RequireOwnerlessWindow = true,
            RequireExclusiveFullscreen = resolution.MonitorSizedWindowObserved,
            MinClientWidth = Math.Max(640, resolution.Width),
            MinClientHeight = Math.Max(360, resolution.Height),
            LearnedFromDiscovery = true,
            LearnedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static string BuildLauncherSignatureKey(string exeName, string exePath)
    {
        var source = !string.IsNullOrWhiteSpace(exePath)
            ? Path.GetFileNameWithoutExtension(exePath) ?? exeName
            : exeName;
        if (string.IsNullOrWhiteSpace(source))
        {
            return "launcher-target";
        }

        var builder = new StringBuilder(source.Length);
        foreach (var ch in source.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
            else if (builder.Length == 0 || builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        return builder.ToString().Trim('-');
    }

    private static bool TryParseHookLaunchTargetTokens(
        IReadOnlyList<string> startupArgs,
        out IReadOnlyList<string> targetCommandTokens,
        out string? failureReason)
    {
        targetCommandTokens = Array.Empty<string>();
        failureReason = null;
        if (startupArgs == null || startupArgs.Count <= 1)
        {
            return false;
        }

        var hasHookFlag = false;
        for (var i = 1; i < startupArgs.Count; i++)
        {
            var arg = startupArgs[i];
            if (string.Equals(arg, "--hook-launch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--hook", StringComparison.OrdinalIgnoreCase))
            {
                hasHookFlag = true;
                break;
            }
        }

        if (!hasHookFlag)
        {
            return false;
        }

        var separatorIndex = -1;
        for (var i = 1; i < startupArgs.Count; i++)
        {
            if (string.Equals(startupArgs[i], "--", StringComparison.Ordinal))
            {
                separatorIndex = i;
                break;
            }
        }

        if (separatorIndex < 0)
        {
            failureReason = "launch_options_separator_missing";
            return true;
        }

        if (separatorIndex + 1 >= startupArgs.Count)
        {
            failureReason = "launch_options_target_command_empty";
            return true;
        }

        targetCommandTokens = startupArgs.Skip(separatorIndex + 1).ToArray();
        return true;
    }

    private void SyncSettingsAfterHostFailure(AppSettings settings, bool updateTranslationStatus)
    {
        _mainWindowViewModel.Settings.LoadFrom(settings);
        if (updateTranslationStatus)
        {
            UpdateTranslationStatus(settings);
        }

        UpdateAutoTranslateBadgeVisibility(settings);
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleRunOnceHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnForceRunHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleForceRunHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnRunNextRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleRunRoiPresetHotkeyAsync(1).ConfigureAwait(true);
    }

    private async void OnRunNextNextRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleRunRoiPresetHotkeyAsync(2).ConfigureAwait(true);
    }

    private async void OnForceRunNextRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleForceRunRoiPresetHotkeyAsync(1).ConfigureAwait(true);
    }

    private async void OnForceRunNextNextRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleForceRunRoiPresetHotkeyAsync(2).ConfigureAwait(true);
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
        var settings = _settingsService.Settings;
        if (IsGraphicsHookLauncherModeEnabled(settings))
        {
            AppendLog("Capture window lock blocked: Graphics Hook launcher mode is enabled.");
            return;
        }

        var spec = await _hotkeyCommandController.HandleLockCaptureWindowHotkeyAsync().ConfigureAwait(true);
        UpdatePinnedThumbnailFromLockResult(spec);
        // WHY: Lock/unlock hotkeys persist settings without going through the UI save path,
        // so we must explicitly apply hook settings here to ensure injection/attach happens.
        await _graphicsHookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
    }

    private async void OnUnlockCaptureWindowHotkeyPressed(object? sender, EventArgs e)
    {
        var settings = _settingsService.Settings;
        if (IsGraphicsHookLauncherModeEnabled(settings))
        {
            AppendLog("Capture window unlock blocked: Graphics Hook launcher mode is enabled.");
            return;
        }

        await _hotkeyCommandController.HandleUnlockCaptureWindowHotkeyAsync().ConfigureAwait(true);
        ClearPinnedCaptureThumbnail("No fixed target");
        // WHY: Explicitly stop (detach) the hook when the fixed target is cleared, regardless of fallback settings.
        await _graphicsHookClientService.StopAsync().ConfigureAwait(true);
    }

    private async void OnToggleMirrorFullscreenHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleToggleMirrorFullscreenHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnSelectRoiHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleSelectRoiHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnSelectFixedOverlayFrameHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleSelectFixedOverlayFrameHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnNextRoiPresetHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandleNextRoiPresetHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnPreviousRoiPresetHotkeyPressed(object? sender, EventArgs e)
    {
        await _hotkeyCommandController.HandlePreviousRoiPresetHotkeyAsync().ConfigureAwait(true);
    }

    private async void OnToggleOverlayHotkeyPressed(object? sender, EventArgs e)
    {
        _hotkeyCommandController.HandleToggleOverlayHotkey();
        // WHY: WPF overlay and hook overlay should stay in sync by default to reduce confusion.
        // This does not persist settings; it only updates the hook runtime config mapping.
        var settings = _settingsService.Settings;
        var targetPid = ResolveEffectiveGraphicsHookPid(settings);
        if (settings.EnableGraphicsHookPipeline && targetPid > 0)
        {
            var effectiveHookOverlayEnabled =
                IsHookOverlaySupportedApi(settings.GraphicsHookApi) &&
                settings.GraphicsHookOverlayEnabled &&
                _overlayEnabled;
            var published = _graphicsHookClientService.TryPublishRuntimeConfig(
                targetPid,
                settings.GraphicsHookCaptureFpsLimit,
                effectiveHookOverlayEnabled,
                out var failureReason);
            _logger?.Info(
                $"stage=graphics_hook event=runtime_config_publish pid={targetPid} " +
                $"fps_limit={settings.GraphicsHookCaptureFpsLimit} overlay={effectiveHookOverlayEnabled} " +
                $"result={(published ? "ok" : "failed")} reason={(published ? "none" : failureReason ?? "unknown")}.");
            if (!published)
            {
                // WHY: F9 toggle should self-heal even when runtime publish misses; re-apply reattaches and rewrites config.
                await _graphicsHookClientService.ApplySettingsAsync(settings).ConfigureAwait(true);
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

        var targetPid = ResolveEffectiveGraphicsHookPid(settings);
        if (settings.EnableGraphicsHookPipeline && targetPid > 0)
        {
            var effectiveHookOverlayEnabled =
                IsHookOverlaySupportedApi(settings.GraphicsHookApi) &&
                settings.GraphicsHookOverlayEnabled &&
                _overlayEnabled;
            var published = _graphicsHookClientService.TryPublishRuntimeConfig(
                targetPid,
                settings.GraphicsHookCaptureFpsLimit,
                effectiveHookOverlayEnabled,
                out var failureReason);
            _logger?.Info(
                $"stage=graphics_hook event=runtime_config_publish source=enable_overlay pid={targetPid} " +
                $"fps_limit={settings.GraphicsHookCaptureFpsLimit} overlay={effectiveHookOverlayEnabled} " +
                $"result={(published ? "ok" : "failed")} reason={(published ? "none" : failureReason ?? "unknown")}.");
            if (!published)
            {
                // WHY: F8/F10 run should restore hook overlay visibility after F9 hide, even when best-effort publish misses.
                _ = _graphicsHookClientService.ApplySettingsAsync(settings);
            }
        }
    }

    private void TryClearHookOverlayForReshow(AppSettings settings, string source)
    {
        if (!settings.EnableGraphicsHookPipeline ||
            !IsHookOverlaySupportedApi(settings.GraphicsHookApi) ||
            !settings.GraphicsHookOverlayEnabled)
        {
            return;
        }

        var targetPid = ResolveEffectiveGraphicsHookPid(settings);
        if (targetPid <= 0 || _captureManager == null)
        {
            return;
        }

        var bounds = _captureManager.GetCaptureBounds(settings);
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
        {
            _logger?.Info(
                $"stage=graphics_hook event=overlay_reshow_clear source={source} result=skip reason=invalid_bounds.");
            return;
        }

        var canvasW = (uint)Math.Max(1, Math.Round(bounds.Width));
        var canvasH = (uint)Math.Max(1, Math.Round(bounds.Height));
        var cleared = _graphicsHookClientService.TryWriteOverlayV2(
            targetPid,
            canvasW,
            canvasH,
            ReadOnlySpan<GraphicsHookOverlayV2CommandWriter.TextBlockV2>.Empty,
            Array.Empty<byte>(),
            0,
            out var failureReason);
        _logger?.Info(
            $"stage=graphics_hook event=overlay_reshow_clear source={source} pid={targetPid} " +
            $"canvas={canvasW}x{canvasH} result={(cleared ? "ok" : "failed")} " +
            $"reason={(cleared ? "none" : failureReason ?? "unknown")}.");
    }

    private static bool IsHookOverlaySupportedApi(GraphicsHookApiKind api)
    {
        return api is GraphicsHookApiKind.Dx9 or GraphicsHookApiKind.Dx11 or GraphicsHookApiKind.Vulkan;
    }

    private static bool IsGraphicsHookLauncherModeEnabled(AppSettings settings)
    {
        return settings.EnableGraphicsHookPipeline &&
               settings.EnableGraphicsHookLauncher;
    }

    private int ResolveEffectiveGraphicsHookPid(AppSettings settings)
    {
        return _launcherSessionTargetState.ResolveEffectiveProcessId(settings);
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

    private static IReadOnlyList<string> BuildRoiPresetSlotOptions()
    {
        return Enumerable.Range(1, RoiPresetSlotCount)
            .Select(index => $"Slot {index}")
            .ToList();
    }

    private async void OnRoiPresetSlotBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingRoiPresetSlotSelection || !IsLoaded || _isClosing)
        {
            return;
        }

        if (sender is not ComboBox comboBox)
        {
            return;
        }

        var slotIndex = comboBox.SelectedIndex;
        if (slotIndex < 0)
        {
            return;
        }

        await ApplyRoiPresetSlotAsync(slotIndex, "ui").ConfigureAwait(true);
    }

    private void OnRoiPresetPreviewClearTimerTick(object? sender, EventArgs e)
    {
        _roiPresetPreviewClearTimer.Stop();
        _pipeline?.UpdateHookRoiPreview(null);
    }

    private async Task ChangeRoiPresetByOffsetAsync(int offset, string trigger)
    {
        var settings = _settingsService.Settings;
        EnsureRoiPresetSlots(settings);
        var currentIndex = Math.Clamp(settings.ActiveRoiPresetIndex, 0, RoiPresetSlotCount - 1);
        var nextIndex = (currentIndex + offset + RoiPresetSlotCount) % RoiPresetSlotCount;
        await ApplyRoiPresetSlotAsync(nextIndex, trigger).ConfigureAwait(true);
    }

    private async Task ApplyRoiPresetSlotAsync(int slotIndex, string trigger)
    {
        var settings = _settingsService.Settings;
        EnsureRoiPresetSlots(settings);
        slotIndex = Math.Clamp(slotIndex, 0, RoiPresetSlotCount - 1);
        settings.ActiveRoiPresetIndex = slotIndex;

        var preset = settings.RoiPresets[slotIndex];
        Rect previewRectScreen = Rect.Empty;
        if (preset.NormalizedRoi is { } normalized && !normalized.IsEmpty)
        {
            settings.NormalizedRoi = normalized;
            settings.EnableRoi = preset.EnableRoi;
            if (_captureManager != null)
            {
                var frameBounds = _captureManager.GetCaptureBounds(settings);
                previewRectScreen = normalized.ToAbsolute(frameBounds);
                settings.Roi = previewRectScreen.IsEmpty ? null : SerializableRect.FromRect(previewRectScreen);
            }

            ShowTransientRoiPreview(previewRectScreen);
            AppendLog($"ROI slot {slotIndex + 1} applied ({trigger}).");
        }
        else
        {
            AppendLog($"ROI slot {slotIndex + 1} selected ({trigger}). Slot is empty.");
        }

        SyncRoiPresetSlotUi(settings);
        _mainWindowViewModel.Settings.LoadFrom(settings);
        UpdateRoiStatus(settings);
        await _settingsService.SaveAsync().ConfigureAwait(true);
    }

    private void SyncRoiPresetSlotUi(AppSettings settings)
    {
        EnsureRoiPresetSlots(settings);
        _isApplyingRoiPresetSlotSelection = true;
        try
        {
            var selectedIndex = Math.Clamp(settings.ActiveRoiPresetIndex, 0, RoiPresetSlotCount - 1);
            OverviewControl.SelectedRoiPresetSlotIndex = selectedIndex;
        }
        finally
        {
            _isApplyingRoiPresetSlotSelection = false;
        }
    }

    private void ShowTransientRoiPreview(Rect rectScreen)
    {
        if (rectScreen.IsEmpty || rectScreen.Width <= 0 || rectScreen.Height <= 0)
        {
            return;
        }

        _overlayPresenter?.ShowRoiPreview(rectScreen, RoiPresetPreviewDurationMs);
        _pipeline?.UpdateHookRoiPreview(rectScreen);
        _roiPresetPreviewClearTimer.Stop();
        _roiPresetPreviewClearTimer.Start();
    }

    private void TryRefreshOverlayFromLastData()
    {
        if (_pipeline == null)
        {
            return;
        }

        // WHY: Display-target changes should take effect on the latest overlay immediately without forcing a rerun.
        _pipeline.TrySetOverlayTextMode(_overlayTextMode, out _, allowModeUpdateWithoutData: true);
    }

    private static void EnsureRoiPresetSlots(AppSettings settings)
    {
        settings.RoiPresets ??= new List<RoiPreset>();
        for (var i = settings.RoiPresets.Count; i < RoiPresetSlotCount; i++)
        {
            settings.RoiPresets.Add(new RoiPreset { SlotIndex = i });
        }

        if (settings.RoiPresets.Count > RoiPresetSlotCount)
        {
            settings.RoiPresets.RemoveRange(RoiPresetSlotCount, settings.RoiPresets.Count - RoiPresetSlotCount);
        }

        for (var i = 0; i < settings.RoiPresets.Count; i++)
        {
            settings.RoiPresets[i].SlotIndex = i;
            if (settings.RoiPresets[i].NormalizedRoi is { } normalized)
            {
                settings.RoiPresets[i].NormalizedRoi = normalized.Clamp();
            }
        }

        settings.ActiveRoiPresetIndex = Math.Clamp(settings.ActiveRoiPresetIndex, 0, RoiPresetSlotCount - 1);
    }

    private async Task RunOnceAsync(ForceRunOptions options)
    {
        // WHY: Explicit actions should observe the latest UI edits before pipeline execution.
        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);
        CheckAndShowPrerequisiteDialogs(_settingsService.Settings);
        await _runCoordinator.RunOnceAsync(options).ConfigureAwait(true);
    }

    private async Task RunRoiPresetWithOffsetAsync(int offset, ForceRunOptions options)
    {
        await FlushPendingSettingsSaveAsync().ConfigureAwait(true);

        var settings = _settingsService.Settings;
        EnsureRoiPresetSlots(settings);
        var currentIndex = Math.Clamp(settings.ActiveRoiPresetIndex, 0, RoiPresetSlotCount - 1);
        var targetIndex = (currentIndex + offset + RoiPresetSlotCount) % RoiPresetSlotCount;
        var preset = settings.RoiPresets[targetIndex];
        if (preset.NormalizedRoi is not { } normalized || normalized.IsEmpty)
        {
            AppendLog($"ROI slot {targetIndex + 1} run skipped: slot is empty.");
            return;
        }

        var originalEnableRoi = settings.EnableRoi;
        var originalNormalizedRoi = settings.NormalizedRoi;
        var originalRoi = settings.Roi;
        try
        {
            // WHY: Slot hotkeys should target a saved ROI for one run without changing the user's active slot/UI state.
            settings.EnableRoi = true;
            settings.NormalizedRoi = normalized.Clamp();
            if (_captureManager != null)
            {
                var frameBounds = _captureManager.GetCaptureBounds(settings);
                var previewRectScreen = settings.NormalizedRoi.Value.ToAbsolute(frameBounds);
                settings.Roi = previewRectScreen.IsEmpty ? null : SerializableRect.FromRect(previewRectScreen);
            }
            else
            {
                settings.Roi = null;
            }

            // WHY: Run-next-slot hotkeys are execution shortcuts, not ROI editing actions. Re-showing
            // the saved ROI frame here adds flicker without helping target selection.
            var modeLabel = options.IsEnabled ? "force run" : "run once";
            AppendLog($"ROI slot {targetIndex + 1}: {modeLabel}.");
            CheckAndShowPrerequisiteDialogs(settings);
            await _runCoordinator.RunOnceAsync(options).ConfigureAwait(true);
        }
        finally
        {
            settings.EnableRoi = originalEnableRoi;
            settings.NormalizedRoi = originalNormalizedRoi;
            settings.Roi = originalRoi;
        }
    }

    private void SetBusyOverlay(bool visible, string? message)
    {
        _busyOverlayController.SetBusyOverlay(visible, message);
    }

    private void SetBusyOverlayCancelable(bool visible)
    {
        _mainWindowViewModel.RuntimeStatus.CanCancelCurrentRun = visible;
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
    void IMainWindowViewBridge.SetBusyOverlayCancelable(bool visible) => SetBusyOverlayCancelable(visible);
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
    Task<ResourceBootstrapConfirmationResult> ISettingsUiBridge.ConfirmResourceBootstrapAsync(AppSettings settings, ResourceBootstrapIntent intent) =>
        ConfirmResourceBootstrapAsync(settings, intent);
    bool ISettingsUiBridge.EnsureOneOcrVendorAvailable(AppSettings settings) =>
        _oneOcrVendorUiController.EnsureVendorAvailable(settings);
    Task<bool> ISettingsUiBridge.EnsureResourceHostsAsync(AppSettings settings) => _resourceHostFacade.EnsureResourceHostsAsync(settings);
    Task ISettingsUiBridge.PersistSettingsAsync() => _settingsService.SaveAsync();
    bool ISettingsUiBridge.HasHotkeyConflicts => _mainWindowViewModel.Settings.HasHotkeyConflicts;
    bool ISettingsUiBridge.TryValidateResourceHostBudget(AppSettings settings, out string? message) =>
        _resourceHostFacade.TryValidateBudget(settings, out message);
    void ISettingsUiBridge.SyncSettingsToView(AppSettings settings, bool updateTranslationStatus) =>
        SyncSettingsAfterHostFailure(settings, updateTranslationStatus);
    void ISettingsUiBridge.ShowLoadFailure(string message) => ShowLoadFailure(message);
    void ISettingsUiBridge.AppendLog(string message) => AppendLog(message);
    void ISettingsUiBridge.TryUpdateHotkeys(AppSettings settings) => TryUpdateHotkeys(settings);
    void ISettingsUiBridge.UpdateAutoHideWatcher(AppSettings settings) => UpdateAutoHideWatcher(settings);

    void ISettingsUiBridge.ClearSceneChangeAutoTranslatePending(string reason) =>
        ClearSceneChangeAutoTranslatePending(reason);

    private Task<ResourceBootstrapConfirmationResult> ConfirmResourceBootstrapAsync(
        AppSettings settings,
        ResourceBootstrapIntent intent)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher.InvokeAsync(() => ConfirmResourceBootstrapAsync(settings, intent)).Task.Unwrap();
        }

        var plan = _resourceHostFacade.BuildBootstrapPlan(settings, intent);
        if (!plan.RequiresConfirmation)
        {
            return Task.FromResult(new ResourceBootstrapConfirmationResult(Approved: true, SettingsChanged: false));
        }

        var message = BuildResourceBootstrapConfirmationMessage(plan, intent);
        var result = MessageBox.Show(
            this,
            message,
            Localizer.GetString("Dialog_DownloadConfirmation_Title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            AppendLog("Resource setup/download canceled by user.");
            return Task.FromResult(new ResourceBootstrapConfirmationResult(Approved: false, SettingsChanged: false));
        }

        var settingsChanged = false;
        var approvals = settings.ApprovedResourceBootstrapKeys ??= new List<string>();
        foreach (var approvalKey in plan.Items
                     .Select(static item => item.ApprovalKey)
                     .Where(static key => !string.IsNullOrWhiteSpace(key)))
        {
            if (approvals.Contains(approvalKey!, StringComparer.Ordinal))
            {
                continue;
            }

            approvals.Add(approvalKey!);
            settingsChanged = true;
        }

        return Task.FromResult(new ResourceBootstrapConfirmationResult(Approved: true, SettingsChanged: settingsChanged));
    }

    private string BuildResourceBootstrapConfirmationMessage(
        ResourceBootstrapPlan plan,
        ResourceBootstrapIntent intent)
    {
        var builder = new StringBuilder();
        builder.AppendLine(Localizer.GetString("ResourceBootstrap_Intro"));
        builder.AppendLine();

        foreach (var item in plan.Items)
        {
            var category = item.IsDefinite
                ? Localizer.GetString("ResourceBootstrap_WillRun")
                : Localizer.GetString("ResourceBootstrap_MayRun");
            builder.Append("- ");
            builder.Append(item.DisplayName);
            builder.Append(": ");
            builder.Append(category);
            builder.Append(' ');
            builder.Append(item.Detail);
            if (item.KnownDownloadBytes is > 0)
            {
                builder.Append(' ');
                builder.Append(Localizer.GetString("ResourceBootstrap_KnownModelDownload", FormatByteSize(item.KnownDownloadBytes.Value)));
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.Append(intent == ResourceBootstrapIntent.SettingsSave
            ? Localizer.GetString("ResourceBootstrap_SettingsSaveTail")
            : Localizer.GetString("ResourceBootstrap_AppLoadTail"));
        return builder.ToString().TrimEnd();
    }

    private static string FormatByteSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }

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

    private async Task<(bool? Result, Rect? SelectedRect, NormalizedRect? SelectedNormalizedRect)> RunRectSelectionAsync(
        AppSettings settings,
        string selectionSource)
    {
        if (_captureManager == null)
        {
            return (null, null, null);
        }

        var bounds = _captureManager.GetCaptureBounds(settings);
        var selector = new RoiSelectorWindow(bounds);
        RawInputMouseSession? rawInputMouseSession = null;
        bool previewHookSubscribed = false;
        bool? result = null;
        Action<Rect?> onPreviewRectChanged = previewRect => _pipeline?.UpdateHookRoiPreview(previewRect);
        try
        {
            if (settings.EnableRawInputHotkeys)
            {
                rawInputMouseSession = new RawInputMouseSession(
                    selector,
                    selector.BeginExternalDragFromScreen,
                    selector.UpdateExternalDragFromScreen,
                    selector.EndExternalDragFromScreen);
                if (rawInputMouseSession.TryStart(out var failureReason))
                {
                    // WHY: During screen-region selection in RawInput mode, consume pointer updates from WM_INPUT only.
                    // This avoids missing drag updates in focus-sensitive game windows.
                    selector.SetExternalPointerInputEnabled(true);
                    _logger?.Info($"stage=rawinput_mouse event=start source={selectionSource} result=ok.");
                }
                else
                {
                    rawInputMouseSession.Dispose();
                    rawInputMouseSession = null;
                    _logger?.Info(
                        $"stage=rawinput_mouse event=start source={selectionSource} result=failed reason={failureReason ?? "unknown"}.");
                }
            }

            if (_hookRoiTraceEnabled)
            {
                _logger?.Info(
                    $"stage=hook_roi_preview event=selector_start source={selectionSource} " +
                    $"bounds=[{bounds.X:0.##},{bounds.Y:0.##},{bounds.Width:0.##},{bounds.Height:0.##}]");
            }

            // WHY: In hook-only mode, the WPF selection frame is not visible over exclusive fullscreen.
            // Stream preview rect updates to Hook overlay so the user can see the frame while dragging.
            selector.PreviewRectChanged += onPreviewRectChanged;
            previewHookSubscribed = true;
            _pipeline?.UpdateHookRoiPreview(null);
            result = selector.ShowDialog();
            selector.PreviewRectChanged -= onPreviewRectChanged;
            previewHookSubscribed = false;

            if (_hookRoiTraceEnabled)
            {
                _logger?.Info(
                    $"stage=hook_roi_preview event=selector_end source={selectionSource} " +
                    $"result={(result == true ? "confirm" : "cancel")} selected={(selector.SelectedRect.HasValue ? 1 : 0)}.");
            }

            return (result, selector.SelectedRect, selector.SelectedNormalizedRect);
        }
        finally
        {
            if (previewHookSubscribed)
            {
                selector.PreviewRectChanged -= onPreviewRectChanged;
            }

            selector.SetExternalPointerInputEnabled(false);
            rawInputMouseSession?.Dispose();
            _pipeline?.UpdateHookRoiPreview(null);
        }
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
            var selection = await RunRectSelectionAsync(settings, "f6_roi").ConfigureAwait(true);
            if (selection.Result == true && selection.SelectedRect is { } rect)
            {
                EnsureRoiPresetSlots(settings);
                settings.Roi = SerializableRect.FromRect(rect);
                settings.NormalizedRoi = selection.SelectedNormalizedRect;
                var roiWasDisabled = !settings.EnableRoi;
                settings.EnableRoi = true;
                var activeSlotIndex = Math.Clamp(settings.ActiveRoiPresetIndex, 0, RoiPresetSlotCount - 1);
                var preset = settings.RoiPresets[activeSlotIndex];
                preset.NormalizedRoi = selection.SelectedNormalizedRect;
                preset.EnableRoi = true;
                _mainWindowViewModel.Settings.LoadFrom(settings);

                SyncRoiPresetSlotUi(settings);
                UpdateRoiStatus(settings);
                await _settingsService.SaveAsync().ConfigureAwait(true);
                if (roiWasDisabled)
                {
                    AppendLog("ROI enabled automatically.");
                }

                AppendLog($"ROI updated and saved to slot {activeSlotIndex + 1}.");
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
            _isSelectingRoi = false;
        }
    }

    private async Task SelectFixedOverlayFrameAsync()
    {
        if (_isSelectingRoi)
        {
            AppendLog("Overlay frame selection already in progress.");
            return;
        }

        EnableOverlay();
        if (_captureManager == null)
        {
            return;
        }

        _isSelectingRoi = true;
        try
        {
            var settings = _settingsService.Settings;
            var selection = await RunRectSelectionAsync(settings, "fixed_overlay_frame").ConfigureAwait(true);
            if (selection.Result == true && selection.SelectedRect is { } rect)
            {
                settings.FixedOverlayNormalizedRect = selection.SelectedNormalizedRect;
                _mainWindowViewModel.Settings.LoadFrom(settings);
                await _settingsService.SaveAsync().ConfigureAwait(true);
                ShowTransientRoiPreview(rect);
                TryRefreshOverlayFromLastData();
                var message = settings.FixedOverlayPlacementMode == FixedOverlayPlacementMode.CustomFrame
                    ? "Fixed overlay user frame updated."
                    : "Fixed overlay user frame updated. Switch display target to 'User frame' to use it.";
                AppendLog(message);
                return;
            }

            AppendLog("Fixed overlay user frame selection canceled.");
        }
        finally
        {
            _isSelectingRoi = false;
        }
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        _isApplyingSettings = true;
        _appThemeController.Apply(settings, this);
        EnsureRoiPresetSlots(settings);
        ReloadLlamaModelOptions(settings);
        ReloadVisionLlmModelOptions(settings);
        ApplyTranslationPriority(settings);
        UpdateTranslationStatus(settings);
        _mainWindowViewModel.Settings.LoadFrom(settings);
        SyncRoiPresetSlotUi(settings);
        UpdateLoggingState(settings.EnableLogging);
        UpdateRoiStatus(settings);
        _isApplyingSettings = false;
        UpdateAutoTranslateBadgeVisibility(settings);
        _winRtLanguagePackUiController.SchedulePrecheck();
    }

    private void UpdateRoiStatus(AppSettings settings)
    {
        EnsureRoiPresetSlots(settings);
        var slotLabel = $"slot {settings.ActiveRoiPresetIndex + 1}";
        if (!settings.EnableRoi)
        {
            _mainWindowViewModel.RuntimeStatus.RoiStatusMessage = Localizer.GetString("Runtime_RoiStatus_Disabled", slotLabel);
            return;
        }

        if (settings.NormalizedRoi is null || settings.NormalizedRoi.Value.IsEmpty)
        {
            _mainWindowViewModel.RuntimeStatus.RoiStatusMessage = Localizer.GetString("Runtime_RoiStatus_NotSet", slotLabel);
            return;
        }

        var roi = settings.NormalizedRoi.Value;
        _mainWindowViewModel.RuntimeStatus.RoiStatusMessage =
            Localizer.GetString("Runtime_RoiStatus_Coordinates", slotLabel, roi.X, roi.Y, roi.Width, roi.Height);
    }

    private void UpdateTranslationStatus(AppSettings settings)
    {
        var llamaStatus = settings.EnableLlamaCppTranslation
            ? Localizer.GetString("Runtime_ProviderStatus_Enabled", "Llama")
            : Localizer.GetString("Runtime_ProviderStatus_Disabled", "Llama");
        var geminiStatus = settings.EnableGemini
            ? (string.IsNullOrWhiteSpace(settings.ApiKey)
                ? Localizer.GetString("Runtime_ProviderStatus_KeyMissing", "Gemini")
                : Localizer.GetString("Runtime_ProviderStatus_Enabled", "Gemini"))
            : Localizer.GetString("Runtime_ProviderStatus_Disabled", "Gemini");
        var deepLStatus = settings.EnableDeepL
            ? (string.IsNullOrWhiteSpace(settings.DeepLApiKey)
                ? Localizer.GetString("Runtime_ProviderStatus_KeyMissing", "DeepL")
                : Localizer.GetString("Runtime_ProviderStatus_Enabled", "DeepL"))
            : Localizer.GetString("Runtime_ProviderStatus_Disabled", "DeepL");
        _mainWindowViewModel.RuntimeStatus.TranslationStatusMessage =
            Localizer.GetString("Runtime_TranslationStatus_Summary", llamaStatus, geminiStatus, deepLStatus);
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        UpdateTranslationStatus(_settingsService.Settings);
        UpdateRoiStatus(_settingsService.Settings);
        _winRtLanguagePackUiController.SchedulePrecheck();
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

    private void ReloadVisionLlmModelOptions(AppSettings settings)
    {
        var modelFallback = SettingsHostNormalizer.NormalizeVisionLlmModelFileName(DefaultVisionLlmModelFileName);
        var mmprojFallback = SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(DefaultVisionLlmMmprojFileName);
        var selectedModel = string.IsNullOrWhiteSpace(_mainWindowViewModel.Settings.VisionLlmSelectedModelFileName)
            ? settings.VisionLlmSelectedModelFileName
            : _mainWindowViewModel.Settings.VisionLlmSelectedModelFileName;
        var selectedMmproj = string.IsNullOrWhiteSpace(_mainWindowViewModel.Settings.VisionLlmSelectedMmprojFileName)
            ? settings.VisionLlmSelectedMmprojFileName
            : _mainWindowViewModel.Settings.VisionLlmSelectedMmprojFileName;
        var normalizedModel = SettingsHostNormalizer.NormalizeVisionLlmModelFileName(selectedModel);
        var normalizedMmproj = SettingsHostNormalizer.NormalizeVisionLlmMmprojFileName(selectedMmproj);
        var fileNames = _llamaModelCatalog.GetAvailableModelFileNames();
        var modelOptions = fileNames
            .Where(fileName => !fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .Select(fileName => new LlamaModelOption(fileName, fileName))
            .ToList();
        var mmprojOptions = fileNames
            .Where(fileName => fileName.Contains("mmproj", StringComparison.OrdinalIgnoreCase))
            .Select(fileName => new LlamaModelOption(fileName, fileName))
            .ToList();
        if (!string.IsNullOrWhiteSpace(normalizedModel) &&
            !modelOptions.Any(option => string.Equals(option.Value, normalizedModel, StringComparison.OrdinalIgnoreCase)))
        {
            // WHY: Keep missing selections visible so users can repair broken model setups from the UI.
            modelOptions.Add(new LlamaModelOption(normalizedModel, $"{normalizedModel} (missing)"));
        }

        if (!string.IsNullOrWhiteSpace(normalizedMmproj) &&
            !mmprojOptions.Any(option => string.Equals(option.Value, normalizedMmproj, StringComparison.OrdinalIgnoreCase)))
        {
            // WHY: mmproj pairs can go missing independently of the GGUF model file.
            mmprojOptions.Add(new LlamaModelOption(normalizedMmproj, $"{normalizedMmproj} (missing)"));
        }

        var previousApplyingState = _isApplyingSettings;
        _isApplyingSettings = true;
        try
        {
            _mainWindowViewModel.ResetVisionLlmModelOptions(modelOptions);
            _mainWindowViewModel.ResetVisionLlmMmprojOptions(mmprojOptions);

            var resolvedModel = modelOptions.Count == 0
                ? modelFallback
                : normalizedModel;
            if (modelOptions.Count > 0 &&
                !modelOptions.Any(option => string.Equals(option.Value, normalizedModel, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedModel = modelOptions[0].Value;
            }

            var resolvedMmproj = mmprojOptions.Count == 0
                ? mmprojFallback
                : normalizedMmproj;
            if (mmprojOptions.Count > 0 &&
                !mmprojOptions.Any(option => string.Equals(option.Value, normalizedMmproj, StringComparison.OrdinalIgnoreCase)))
            {
                resolvedMmproj = mmprojOptions[0].Value;
            }

            _mainWindowViewModel.Settings.VisionLlmSelectedModelFileName = resolvedModel;
            _mainWindowViewModel.Settings.VisionLlmSelectedMmprojFileName = resolvedMmproj;
            settings.VisionLlmSelectedModelFileName = resolvedModel;
            settings.VisionLlmSelectedMmprojFileName = resolvedMmproj;
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

    private async Task ReloadVisionLlmModelsAsync()
    {
        var settings = _settingsService.Settings;
        ReloadVisionLlmModelOptions(settings);
        await SaveSettingsImmediatelyAsync().ConfigureAwait(true);
    }

    private Task RestartVisionLlmAsync() => _resourceHostCommandController.RestartVisionLlmAsync();

    private Task StopVisionLlmAsync()
    {
        _resourceHostCommandController.StopVisionLlmHost();
        return Task.CompletedTask;
    }

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

    private async Task<bool> SaveSettingsImmediatelyAsync()
    {
        _settingsChangeScheduler.CancelPending();
        return await _settingsUiController.SaveFromUiAsync().ConfigureAwait(true);
    }

    private async Task SaveSettingsCoreAsync()
    {
        await _settingsUiController.SaveFromUiAsync().ConfigureAwait(true);
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
        _appThemeController.Apply(settings, this);
        EnsureRoiPresetSlots(settings);
        _mainWindowViewModel.Settings.LoadFrom(settings);
        SyncRoiPresetSlotUi(settings);
        _overlayWindow?.ApplyStyle(settings);
        TryRefreshOverlayFromLastData();
        UpdateLoggingState(settings.EnableLogging);
        _overlayPresenter?.UpdatePerfLogging(settings.EnableOcrPerfLog && settings.EnableLogging, settings.OcrPerfLogThresholdMs);
        UpdateAutoTranslateBadgeVisibility(settings);
        UpdateRoiStatus(settings);
        UpdateTranslationStatus(settings);
        _magpieSessionController.ApplySettings(settings);
        ApplyMirrorOverlayMapper();
        if (!IsGraphicsHookLauncherModeEnabled(settings))
        {
            _launcherSessionTargetState.Clear("launcher_mode_disabled");
        }
        _ = _graphicsHookClientService.ApplySettingsAsync(settings);
        CheckAndShowPrerequisiteDialogs(settings);
        _winRtLanguagePackUiController.SchedulePrecheck();
    }

    private void CheckAndShowPrerequisiteDialogs(AppSettings settings)
    {
        if (settings.EnableGemini && string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            ShowPrerequisiteDialogOnce(
                "api_gemini_missing",
                Localizer.GetString("ResourceBootstrap_Prerequisite_GeminiMissing"));
        }

        if (settings.EnableDeepL && string.IsNullOrWhiteSpace(settings.DeepLApiKey))
        {
            ShowPrerequisiteDialogOnce(
                "api_deepl_missing",
                Localizer.GetString("ResourceBootstrap_Prerequisite_DeepLMissing"));
        }

        if (IsUvRequired(settings))
        {
            ShowMissingBinaryDialogIfNeeded("bin_uv_missing", FixedUvRelativePath, "OCR/Llama gRPC");
        }

        if (settings.EnableGraphicsHookPipeline)
        {
            ShowMissingBinaryDialogIfNeeded("bin_hookhost_missing", FixedHookHostRelativePath, "Graphics hook");
            if (settings.GraphicsHookApi is GraphicsHookApiKind.Dx9 or GraphicsHookApiKind.Dx11 or GraphicsHookApiKind.Vulkan)
            {
                // WHY: Runtime chooses host/agent by target bitness, so both x86/x64 payloads must be present for supported APIs.
                ShowMissingBinaryDialogIfNeeded("bin_hookhost_x86_missing", FixedHookHostX86RelativePath, "Graphics hook (x86 host)");
            }

            if (settings.GraphicsHookApi == GraphicsHookApiKind.Dx9)
            {
                ShowMissingBinaryDialogIfNeeded("bin_hook_dx9_x64_missing", FixedHookAgentDx9RelativePath, "Graphics hook DX9 agent (x64)");
                ShowMissingBinaryDialogIfNeeded("bin_hook_dx9_x86_missing", FixedHookAgentDx9X86RelativePath, "Graphics hook DX9 agent (x86)");
            }
            else if (settings.GraphicsHookApi == GraphicsHookApiKind.Dx11)
            {
                ShowMissingBinaryDialogIfNeeded("bin_hook_dx11_x64_missing", FixedHookAgentDx11RelativePath, "Graphics hook DX11 agent (x64)");
                ShowMissingBinaryDialogIfNeeded("bin_hook_dx11_x86_missing", FixedHookAgentDx11X86RelativePath, "Graphics hook DX11 agent (x86)");
            }
            else if (settings.GraphicsHookApi == GraphicsHookApiKind.Vulkan)
            {
                ShowMissingBinaryDialogIfNeeded("bin_hook_vulkan_x64_missing", FixedHookAgentVulkanRelativePath, "Graphics hook Vulkan agent (x64)");
                ShowMissingBinaryDialogIfNeeded("bin_hook_vulkan_x86_missing", FixedHookAgentVulkanX86RelativePath, "Graphics hook Vulkan agent (x86)");
            }
        }

        if (settings.EnableMirrorFullscreenMode)
        {
            ShowMissingBinaryDialogIfNeeded("bin_magpie_missing", FixedMagpieCoreRelativePath, "mirror fullscreen");
        }

        if (settings.EnableLlamaCppTranslation)
        {
            ShowMissingBinaryDialogIfNeeded("bin_llamaserver_missing", FixedLlamaServerRelativePath, "Llama.cpp translation");
        }

        if (settings.OcrEngine == OcrEngineKind.VisionLlm && settings.EnableVisionLlmGrpcHost)
        {
            ShowMissingBinaryDialogIfNeeded("bin_llamaserver_vision_missing", FixedLlamaServerRelativePath, "VisionLLM OCR");
        }

        if (IsOneOcrRequired(settings))
        {
            ShowMissingBinaryDialogIfNeeded("bin_oneocr_helper_missing", settings.OneOcrHelperRelativePath, "OneOCR helper");
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

        if (settings.OcrEngine == OcrEngineKind.Ndl && settings.EnableNdlGrpcHost)
        {
            return true;
        }

        return settings.OcrEngine == OcrEngineKind.VisionLlm && settings.EnableVisionLlmGrpcHost;
    }

    private static bool IsOneOcrRequired(AppSettings settings)
    {
        if (!settings.EnableOneOcrHelper)
        {
            return false;
        }

        if (settings.OcrEngine == OcrEngineKind.OneOcr)
        {
            return true;
        }

        return settings.OcrEngine == OcrEngineKind.VisionLlm &&
               settings.EnableVisionGeometryHybridOcr &&
               settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.OneOcr;
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
            Localizer.GetString("ResourceBootstrap_Prerequisite_MissingFile", featureName, fullPath));
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

        MessageBox.Show(this, message, Localizer.GetString("Dialog_ConfigurationRequired_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void PopulateHotkeyKeyBoxes()
    {
        // WHY: Hotkey combo boxes now live inside HotkeysControl, so the shared key list is handed to the child once.
        HotkeysControl.HotkeyKeyOptions = BuildHotkeyKeyOptions();
    }

    private static IReadOnlyList<string> BuildHotkeyKeyOptions()
    {
        return HotkeyDefaults.KeyOptions;
    }

    private void InitializeHotkeys(AppSettings settings)
    {
        var config = BuildHotkeyConfigFromSettings(settings);
        var useRawInput = settings.EnableRawInputHotkeys;
        if (TryRegisterHotkeys(config, useRawInput))
        {
            _currentHotkeyConfig = config;
            _currentHotkeyRawInput = useRawInput;
            return;
        }

        _currentHotkeyConfig = null;
        _currentHotkeyRawInput = false;
        AppendLog("Some hotkeys failed to register. Available hotkeys remain active.");
    }

    private void TryUpdateHotkeys(AppSettings settings)
    {
        var config = BuildHotkeyConfigFromSettings(settings);
        var useRawInput = settings.EnableRawInputHotkeys;
        if (_currentHotkeyConfig.HasValue &&
            _currentHotkeyConfig.Value.Equals(config) &&
            _currentHotkeyRawInput == useRawInput)
        {
            return;
        }

        if (TryRegisterHotkeys(config, useRawInput))
        {
            _currentHotkeyConfig = config;
            _currentHotkeyRawInput = useRawInput;
            var backend = useRawInput ? "RawInput" : "RegisterHotKey";
            AppendLog($"Hotkey updated: RunOnce={FormatHotkey(config.RunOnceKey, config.RunOnceModifiers)}, " +
                      $"RunNextRoi={FormatHotkey(config.RunNextRoiKey, config.RunNextRoiModifiers)}, " +
                      $"RunNextNextRoi={FormatHotkey(config.RunNextNextRoiKey, config.RunNextNextRoiModifiers)}, " +
                      $"Toggle={FormatHotkey(config.ToggleOverlayKey, config.ToggleOverlayModifiers)}, " +
                      $"ForceRun={FormatHotkey(config.ForceRunKey, config.ForceRunModifiers)}, " +
                      $"ForceRunNextRoi={FormatHotkey(config.ForceRunNextRoiKey, config.ForceRunNextRoiModifiers)}, " +
                      $"ForceRunNextNextRoi={FormatHotkey(config.ForceRunNextNextRoiKey, config.ForceRunNextNextRoiModifiers)}, " +
                      $"ForceGeminiStrict={FormatHotkey(config.ForceGeminiStrictKey, config.ForceGeminiStrictModifiers)}, " +
                      $"OcrOnly={FormatHotkey(config.OcrOnlyKey, config.OcrOnlyModifiers)}, " +
                      $"SceneAutoTranslate={FormatHotkey(config.ToggleSceneAutoTranslateKey, config.ToggleSceneAutoTranslateModifiers)}, " +
                      $"Roi={FormatHotkey(config.SelectRoiKey, config.SelectRoiModifiers)}, " +
                      $"FixedOverlayFrame={FormatHotkey(config.SelectFixedOverlayFrameKey, config.SelectFixedOverlayFrameModifiers)}, " +
                      $"RoiNext={FormatHotkey(config.NextRoiPresetKey, config.NextRoiPresetModifiers)}, " +
                      $"RoiPrev={FormatHotkey(config.PreviousRoiPresetKey, config.PreviousRoiPresetModifiers)}, " +
                      $"Lock={FormatHotkey(config.LockCaptureWindowKey, config.LockCaptureWindowModifiers)}, " +
                      $"Unlock={FormatHotkey(config.UnlockCaptureWindowKey, config.UnlockCaptureWindowModifiers)}, " +
                      $"Mirror={FormatHotkey(config.ToggleMirrorFullscreenKey, config.ToggleMirrorFullscreenModifiers)}, " +
                      $"Backend={backend}.");
        }
        else
        {
            AppendLog("Some hotkeys failed to update. Other hotkeys remain active.");
        }
    }

    private bool TryRegisterHotkeys(HotkeyConfig config, bool useRawInputBackend)
    {
        return _hotkeyController.TryRegisterBindings(BuildHotkeyRegistrations(config), useRawInputBackend);
    }

    private IReadOnlyList<HotkeyBindingRegistration> BuildHotkeyRegistrations(HotkeyConfig config)
    {
        var registrations = new List<HotkeyBindingRegistration>();

        void AddIfEnabled(string name, Key key, ModifierKeys modifiers, int id, EventHandler handler)
        {
            if (key == Key.None)
            {
                return;
            }

            registrations.Add(new HotkeyBindingRegistration(name, key, modifiers, id, handler));
        }

        // WHY: Disabled hotkeys intentionally stay out of the registration list so users can opt into them selectively.
        AddIfEnabled("RunOnce", config.RunOnceKey, config.RunOnceModifiers, 1, OnHotkeyPressed);
        AddIfEnabled("RunNextRoi", config.RunNextRoiKey, config.RunNextRoiModifiers, 13, OnRunNextRoiHotkeyPressed);
        AddIfEnabled("RunNextNextRoi", config.RunNextNextRoiKey, config.RunNextNextRoiModifiers, 14, OnRunNextNextRoiHotkeyPressed);
        AddIfEnabled("ToggleOverlay", config.ToggleOverlayKey, config.ToggleOverlayModifiers, 2, OnToggleOverlayHotkeyPressed);
        AddIfEnabled("ForceRun", config.ForceRunKey, config.ForceRunModifiers, 3, OnForceRunHotkeyPressed);
        AddIfEnabled("ForceRunNextRoi", config.ForceRunNextRoiKey, config.ForceRunNextRoiModifiers, 15, OnForceRunNextRoiHotkeyPressed);
        AddIfEnabled("ForceRunNextNextRoi", config.ForceRunNextNextRoiKey, config.ForceRunNextNextRoiModifiers, 16, OnForceRunNextNextRoiHotkeyPressed);
        AddIfEnabled("ForceGeminiStrict", config.ForceGeminiStrictKey, config.ForceGeminiStrictModifiers, 4, OnForceGeminiStrictHotkeyPressed);
        AddIfEnabled("OverlayText", config.OcrOnlyKey, config.OcrOnlyModifiers, 5, OnOcrOnlyHotkeyPressed);
        AddIfEnabled("SceneAutoTranslate", config.ToggleSceneAutoTranslateKey, config.ToggleSceneAutoTranslateModifiers, 9, OnToggleSceneAutoTranslateHotkeyPressed);
        AddIfEnabled("SelectRoi", config.SelectRoiKey, config.SelectRoiModifiers, 6, OnSelectRoiHotkeyPressed);
        AddIfEnabled("SelectFixedOverlayFrame", config.SelectFixedOverlayFrameKey, config.SelectFixedOverlayFrameModifiers, 17, OnSelectFixedOverlayFrameHotkeyPressed);
        AddIfEnabled("NextRoiPreset", config.NextRoiPresetKey, config.NextRoiPresetModifiers, 11, OnNextRoiPresetHotkeyPressed);
        AddIfEnabled("PreviousRoiPreset", config.PreviousRoiPresetKey, config.PreviousRoiPresetModifiers, 12, OnPreviousRoiPresetHotkeyPressed);
        AddIfEnabled("LockWindow", config.LockCaptureWindowKey, config.LockCaptureWindowModifiers, 7, OnLockCaptureWindowHotkeyPressed);
        AddIfEnabled("UnlockWindow", config.UnlockCaptureWindowKey, config.UnlockCaptureWindowModifiers, 8, OnUnlockCaptureWindowHotkeyPressed);
        AddIfEnabled("MirrorFullscreen", config.ToggleMirrorFullscreenKey, config.ToggleMirrorFullscreenModifiers, 10, OnToggleMirrorFullscreenHotkeyPressed);

        return registrations;
    }

    private static HotkeyConfig BuildHotkeyConfigFromSettings(AppSettings settings)
    {
        return new HotkeyConfig(
            ParseKey(settings.HotkeyRunOnceKey),
            ParseModifiers(settings.HotkeyRunOnceModifiers),
            ParseKey(settings.HotkeyRunNextRoiKey),
            ParseModifiers(settings.HotkeyRunNextRoiModifiers),
            ParseKey(settings.HotkeyRunNextNextRoiKey),
            ParseModifiers(settings.HotkeyRunNextNextRoiModifiers),
            ParseKey(settings.HotkeyToggleOverlayKey),
            ParseModifiers(settings.HotkeyToggleOverlayModifiers),
            ParseKey(settings.HotkeyForceRunKey),
            ParseModifiers(settings.HotkeyForceRunModifiers),
            ParseKey(settings.HotkeyForceRunNextRoiKey),
            ParseModifiers(settings.HotkeyForceRunNextRoiModifiers),
            ParseKey(settings.HotkeyForceRunNextNextRoiKey),
            ParseModifiers(settings.HotkeyForceRunNextNextRoiModifiers),
            ParseKey(settings.HotkeyForceGeminiStrictKey),
            ParseModifiers(settings.HotkeyForceGeminiStrictModifiers),
            ParseKey(settings.HotkeyOcrOnlyKey),
            ParseModifiers(settings.HotkeyOcrOnlyModifiers),
            ParseKey(settings.HotkeyToggleSceneAutoTranslateKey),
            ParseModifiers(settings.HotkeyToggleSceneAutoTranslateModifiers),
            ParseKey(settings.HotkeySelectRoiKey),
            ParseModifiers(settings.HotkeySelectRoiModifiers),
            ParseKey(settings.HotkeySelectFixedOverlayFrameKey),
            ParseModifiers(settings.HotkeySelectFixedOverlayFrameModifiers),
            ParseKey(settings.HotkeyNextRoiPresetKey),
            ParseModifiers(settings.HotkeyNextRoiPresetModifiers),
            ParseKey(settings.HotkeyPreviousRoiPresetKey),
            ParseModifiers(settings.HotkeyPreviousRoiPresetModifiers),
            ParseKey(settings.HotkeyLockCaptureWindowKey),
            ParseModifiers(settings.HotkeyLockCaptureWindowModifiers),
            ParseKey(settings.HotkeyUnlockCaptureWindowKey),
            ParseModifiers(settings.HotkeyUnlockCaptureWindowModifiers),
            ParseKey(settings.HotkeyToggleMirrorFullscreenKey),
            ParseModifiers(settings.HotkeyToggleMirrorFullscreenModifiers));
    }

    private static Key ParseKey(string value)
    {
        var normalized = HotkeyDefaults.NormalizeStoredKey(value);
        if (HotkeyDefaults.IsDisabledKey(normalized))
        {
            return Key.None;
        }

        return Enum.TryParse(normalized, true, out Key parsed) && parsed != Key.None
            ? parsed
            : Key.None;
    }

    private static ModifierKeys ParseModifiers(string value)
    {
        return Enum.TryParse(value, true, out ModifierKeys parsed) ? parsed : ModifierKeys.None;
    }

    private static string FormatHotkey(Key key, ModifierKeys modifiers)
    {
        if (key == Key.None)
        {
            return "Disabled";
        }

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
        // WHY: Settings saves should refresh mapper state without resurrecting the last hidden overlay.
        _overlayPresenter.SetScreenRectMapper(null, refreshLastOverlay: false);
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
        if (_uiLogController is null)
        {
            _pendingUiLogMessages.Enqueue(message);
            return;
        }

        _uiLogController.AppendLog(message);
    }

    private void FlushPendingUiLogs()
    {
        if (_uiLogController is null)
        {
            return;
        }

        while (_pendingUiLogMessages.Count > 0)
        {
            _uiLogController.AppendLog(_pendingUiLogMessages.Dequeue());
        }
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
        Key RunNextRoiKey,
        ModifierKeys RunNextRoiModifiers,
        Key RunNextNextRoiKey,
        ModifierKeys RunNextNextRoiModifiers,
        Key ToggleOverlayKey,
        ModifierKeys ToggleOverlayModifiers,
        Key ForceRunKey,
        ModifierKeys ForceRunModifiers,
        Key ForceRunNextRoiKey,
        ModifierKeys ForceRunNextRoiModifiers,
        Key ForceRunNextNextRoiKey,
        ModifierKeys ForceRunNextNextRoiModifiers,
        Key ForceGeminiStrictKey,
        ModifierKeys ForceGeminiStrictModifiers,
        Key OcrOnlyKey,
        ModifierKeys OcrOnlyModifiers,
        Key ToggleSceneAutoTranslateKey,
        ModifierKeys ToggleSceneAutoTranslateModifiers,
        Key SelectRoiKey,
        ModifierKeys SelectRoiModifiers,
        Key SelectFixedOverlayFrameKey,
        ModifierKeys SelectFixedOverlayFrameModifiers,
        Key NextRoiPresetKey,
        ModifierKeys NextRoiPresetModifiers,
        Key PreviousRoiPresetKey,
        ModifierKeys PreviousRoiPresetModifiers,
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
            yield return ("Run next ROI slot", RunNextRoiKey, RunNextRoiModifiers);
            yield return ("Run next+1 ROI slot", RunNextNextRoiKey, RunNextNextRoiModifiers);
            yield return ("Toggle overlay", ToggleOverlayKey, ToggleOverlayModifiers);
            yield return ("Force run", ForceRunKey, ForceRunModifiers);
            yield return ("Force run next ROI slot", ForceRunNextRoiKey, ForceRunNextRoiModifiers);
            yield return ("Force run next+1 ROI slot", ForceRunNextNextRoiKey, ForceRunNextNextRoiModifiers);
            yield return ("Gemini Image Translate", ForceGeminiStrictKey, ForceGeminiStrictModifiers);
            yield return ("Overlay text", OcrOnlyKey, OcrOnlyModifiers);
            yield return ("Scene auto-translate", ToggleSceneAutoTranslateKey, ToggleSceneAutoTranslateModifiers);
            yield return ("Select ROI", SelectRoiKey, SelectRoiModifiers);
            yield return ("Select user frame", SelectFixedOverlayFrameKey, SelectFixedOverlayFrameModifiers);
            yield return ("Next ROI slot", NextRoiPresetKey, NextRoiPresetModifiers);
            yield return ("Previous ROI slot", PreviousRoiPresetKey, PreviousRoiPresetModifiers);
            yield return ("Lock window", LockCaptureWindowKey, LockCaptureWindowModifiers);
            yield return ("Unlock window", UnlockCaptureWindowKey, UnlockCaptureWindowModifiers);
            yield return ("Mirror fullscreen", ToggleMirrorFullscreenKey, ToggleMirrorFullscreenModifiers);
        }

    }
}

