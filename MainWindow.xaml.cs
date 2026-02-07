using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.UI;
using AppCaptureMode = Hotkey_Translator.Models.CaptureMode;

namespace Hotkey_Translator;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly LlamaModelCatalog _llamaModelCatalog = new();
    private readonly HttpClient _httpClient = new();
    private OverlayWindow? _overlayWindow;
    private OverlayPresenter? _overlayPresenter;
    private CacheRepository? _cacheRepository;
    private CaptureManager? _captureManager;
    private PipelineOrchestrator? _pipeline;
    private PaddleGrpcHost? _paddleGrpcHost;
    private CTranslate2GrpcHost? _ct2GrpcHost;
    private LlamaGrpcHost? _llamaGrpcHost;
    private HotkeyManager? _hotkeyManager;
    private HotkeyManager? _overlayToggleHotkeyManager;
    private HotkeyManager? _forceRunHotkeyManager;
    private HotkeyManager? _ocrOnlyHotkeyManager;
    private PhashService? _phashService;
    private DispatcherTimer? _autoHideTimer;
    private bool _autoHideTickInProgress;
    private ulong? _autoHideLastHash;
    private CancellationTokenSource? _autoHideBaselineCts;
    private bool _autoHideBaselinePending;
    private int _autoHideBaselineVersion;
    private CancellationTokenSource? _runCts;
    private int _runInProgress;
    private CancellationTokenSource? _translationOverlayCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private bool _overlayVisible;
    private bool _hasRunOnce;
    private OverlayTextMode _overlayTextMode = OverlayTextMode.Translated;
    private HotkeyConfig? _currentHotkeyConfig;
    private readonly ObservableCollection<string> _translationPriority = new();
    private bool _isApplyingSettings;
    private volatile bool _loggingEnabled = true;
    private readonly ConcurrentQueue<string> _logQueue = new();
    private DispatcherTimer? _logFlushTimer;
    private bool _logFlushPending;
    private int _logLineCount;
    private const int OverlayBaselineDelayMs = 150;
    private const int LogFlushIntervalMs = 150;
    private const int MaxLogLines = 1000;
    private const int TranslationOverlayDelayMs = 200;
    private const string DefaultLlamaModelFileName = "qwen3-1_7b-instruct-q4_k_m.gguf";
    private CTranslate2HostConfig? _ct2HostConfig;
    private LlamaHostConfig? _llamaHostConfig;
    private readonly SemaphoreSlim _resourceLoadGate = new(1, 1);

    public MainWindow()
    {
        InitializeComponent();
        PopulateHotkeyKeyBoxes();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger = new AppLogger(AppendLog);
        InitializeLogBuffer();
        await _settingsService.LoadAsync().ConfigureAwait(true);
        ApplySettingsToUi(_settingsService.Settings);
        TranslationPriorityList.ItemsSource = _translationPriority;
        EnsureSettingsCategorySelection();
        var settingsChanged = await EnsureResourceHostsAsync(_settingsService.Settings).ConfigureAwait(true);
        if (settingsChanged)
        {
            await _settingsService.SaveAsync().ConfigureAwait(true);
        }

        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplyStyle(_settingsService.Settings);
        _overlayPresenter = new OverlayPresenter(_overlayWindow, _logger);
        _overlayPresenter.Shown += OnOverlayShown;
        _overlayPresenter.Hidden += OnOverlayHidden;
        _overlayPresenter.Updated += OnOverlayUpdated;
        _overlayPresenter.UpdatePerfLogging(_settingsService.Settings.EnableOcrPerfLog && _settingsService.Settings.EnableLogging,
            _settingsService.Settings.OcrPerfLogThresholdMs);
        _overlayPresenter.Show();

        _cacheRepository = new CacheRepository(_settingsService.CachePath);
        var frameGate = new FrameGate();
        _captureManager = new CaptureManager(frameGate, _logger);
        var ocrEngine = new OcrEngine(_httpClient, _logger);
        var ocrDiff = new OcrDiffService { IouThreshold = _settingsService.Settings.OcrIouThreshold };
        _phashService = new PhashService();
        var normalization = new NormalizationService();
        var ocrPreprocess = new OcrPreprocessService();
        var lineGrouper = new OcrLineGrouper();
        var keyBuilder = new CacheKeyBuilder();
        var geminiClient = new GeminiClient(_httpClient, _logger);
        var translationProviders = new List<ITranslationProvider>
        {
            new LlamaGrpcTranslationProvider(_logger),
            new CTranslate2GrpcTranslationProvider(_logger),
            new DeepLTranslationProvider(_httpClient, _logger),
            new GeminiTranslationProvider(geminiClient)
        };
        var translationService = new TranslationFallbackService(translationProviders, _logger);

        _pipeline = new PipelineOrchestrator(
            _captureManager,
            ocrEngine,
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

        InitializeHotkeys(_settingsService.Settings);
        InitializeAutoHideWatcher(_settingsService.Settings);
        AppendLog("Ready. F8: run once. F9: toggle overlay. F10: force run. F11: toggle overlay text.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _translationOverlayCts?.Cancel();
        _translationOverlayCts?.Dispose();
        _hotkeyManager?.Dispose();
        _overlayToggleHotkeyManager?.Dispose();
        _forceRunHotkeyManager?.Dispose();
        _ocrOnlyHotkeyManager?.Dispose();
        _autoHideBaselineCts?.Cancel();
        _autoHideBaselineCts?.Dispose();
        if (_autoHideTimer != null)
        {
            _autoHideTimer.Stop();
            _autoHideTimer.Tick -= OnAutoHideTick;
        }
        if (_logFlushTimer != null)
        {
            _logFlushTimer.Stop();
            _logFlushTimer.Tick -= OnLogFlushTick;
        }
        _cacheRepository?.Dispose();
        _httpClient.Dispose();
        _paddleGrpcHost?.Stop();
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
        if (!ShouldLoadPaddle(settings) && !ShouldLoadCTranslate2(settings) && !ShouldLoadLlama(settings))
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

    private static bool ShouldLoadCTranslate2(AppSettings settings)
    {
        return settings.EnableCTranslate2 && !settings.EnableLlamaCppTranslation;
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
        if (!_hasRunOnce)
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

    private async void OnRunOnce(object sender, RoutedEventArgs e)
    {
        await RunOnceAsync().ConfigureAwait(true);
    }

    private async Task RunOnceAsync()
    {
        await RunOnceAsync(ForceRunOptions.None).ConfigureAwait(true);
    }

    private async Task RunOnceAsync(ForceRunOptions options)
    {
        if (_pipeline == null)
        {
            return;
        }

        if (Interlocked.Exchange(ref _runInProgress, 1) == 1)
        {
            AppendLog("Run skipped: OCR already running.");
            return;
        }

        var isFirstRun = !_hasRunOnce;
        _hasRunOnce = true;
        EnableOverlay();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        SetBusyOverlay(true, isFirstRun ? "Initializing OCR..." : "OCR running...");
        try
        {
            await _pipeline.RunOnceAsync(_runCts.Token, options).ConfigureAwait(true);
        }
        finally
        {
            SetBusyOverlay(false, null);
            Interlocked.Exchange(ref _runInProgress, 0);
            _translationOverlayCts?.Cancel();
        }
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

    private async void OnSelectRoi(object sender, RoutedEventArgs e)
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
            _settingsService.Settings.Roi = SerializableRect.FromRect(rect);
            _settingsService.Settings.NormalizedRoi = selector.SelectedNormalizedRect;
            UpdateRoiStatus(_settingsService.Settings);
            await _settingsService.SaveAsync().ConfigureAwait(true);
            AppendLog("ROI updated.");
        }
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        _isApplyingSettings = true;
        CaptureModeBox.SelectedIndex = settings.CaptureMode == AppCaptureMode.Screen ? 0 : 1;
        SetComboBoxByTag(CaptureProviderBox, settings.PreferredCaptureProvider.ToString());
        CaptureProviderFixedCheck.IsChecked = settings.CaptureProviderMode == CaptureProviderMode.Fixed;
        ApplyLanguageSelection(SourceLangCombo, SourceLangCustom, settings.SourceLanguage);
        ApplyLanguageSelection(TargetLangCombo, TargetLangCustom, settings.TargetLanguage);
        EnableRoiCheck.IsChecked = settings.EnableRoi;
        SetComboBoxByTag(OcrEngineBox, settings.OcrEngine switch
        {
            OcrEngineKind.Paddle => "Paddle",
            _ => "WinRt"
        });
        SetComboBoxByTag(PaddleDetectionModelBox, settings.PaddleTextDetectionModelName);
        SetComboBoxByTag(PaddleRecognitionModelBox, settings.PaddleTextRecognitionModelName);
        EnablePaddleConfidenceFilterCheck.IsChecked = settings.EnablePaddleConfidenceFilter;
        PaddleConfidenceThresholdSlider.Value = settings.PaddleConfidenceThreshold;
        EnableCTranslate2Check.IsChecked = settings.EnableCTranslate2;
        SetComboBoxByTag(CTranslate2DeviceBox, settings.CTranslate2Device);
        EnableLlamaCppCheck.IsChecked = settings.EnableLlamaCppTranslation;
        ReloadLlamaModelOptions(settings);
        LlamaHostBox.Text = settings.LlamaHost;
        LlamaPortBox.Text = settings.LlamaPort.ToString();
        LlamaContextSizeBox.Text = settings.LlamaContextSize.ToString();
        LlamaGpuLayersBox.Text = settings.LlamaGpuLayers.ToString();
        LlamaThreadsBox.Text = settings.LlamaThreads.ToString();
        LlamaParallelBox.Text = settings.LlamaParallel.ToString();
        LlamaBatchSizeBox.Text = settings.LlamaBatchSize.ToString();
        LlamaMaxTokensBox.Text = settings.LlamaMaxTokens.ToString();
        LlamaTemperatureBox.Text = settings.LlamaTemperature.ToString("0.###");
        LlamaTopPBox.Text = settings.LlamaTopP.ToString("0.###");
        LlamaTopKBox.Text = settings.LlamaTopK.ToString();
        LlamaRepeatPenaltyBox.Text = settings.LlamaRepeatPenalty.ToString("0.###");
        EnableDeepLCheck.IsChecked = settings.EnableDeepL;
        DeepLApiKeyBox.Password = settings.DeepLApiKey ?? string.Empty;
        DeepLEndpointBox.Text = settings.DeepLEndpoint;
        EnableGeminiCheck.IsChecked = settings.EnableGemini;
        ApplyTranslationPriority(settings);
        UpdateTranslationStatus(settings);
        ApiKeyBox.Password = settings.ApiKey ?? string.Empty;
        ApplyHotkeySettingsToUi(settings);
        PhashThresholdBox.Text = settings.PhashThreshold.ToString();
        IouThresholdBox.Text = settings.OcrIouThreshold.ToString("0.00");
        EnableOcrPerfLogCheck.IsChecked = settings.EnableOcrPerfLog;
        OcrPerfLogThresholdBox.Text = settings.OcrPerfLogThresholdMs.ToString();
        EnableLoggingCheck.IsChecked = settings.EnableLogging;
        EnableOcrBinarizationCheck.IsChecked = settings.EnableOcrBinarization;
        EnableOcrAutoThresholdCheck.IsChecked = settings.EnableOcrAutoThreshold;
        EnableOcrAutoInvertCheck.IsChecked = settings.EnableOcrAutoInvert;
        EnableOcrGammaCheck.IsChecked = settings.EnableOcrGamma;
        EnableOcrTwoPassCheck.IsChecked = settings.EnableOcrTwoPass;
        OcrTwoPassPreferAutoCheck.IsChecked = settings.OcrTwoPassPreferAuto;
        OcrBinarizationThresholdSlider.Value = settings.OcrBinarizationThreshold;
        OcrGammaSlider.Value = settings.OcrGamma;
        EnableOcrDownsamplingCheck.IsChecked = settings.EnableOcrDownsampling;
        OcrDownsampleScaleSlider.Value = settings.OcrDownsampleScale;
        OcrTwoPassLowThresholdSlider.Value = settings.OcrTwoPassLowThreshold;
        OcrTwoPassHighThresholdSlider.Value = settings.OcrTwoPassHighThreshold;
        OverlayFontSizeSlider.Value = settings.OverlayFontSize;
        OverlayBackgroundOpacitySlider.Value = settings.OverlayBackgroundOpacity;
        EnableFixedRoiOverlayCheck.IsChecked = settings.EnableFixedRoiOverlay;
        EnableOverlayShortLineShrinkCheck.IsChecked = settings.EnableOverlayShortLineShrink;
        EnableSceneChangeAutoHideCheck.IsChecked = settings.EnableSceneChangeAutoHide;
        EnableSceneChangeTextWeightedCheck.IsChecked = settings.EnableSceneChangeTextWeighted;
        SceneChangeThresholdSlider.Value = settings.SceneChangeThreshold;
        SceneChangeWatchIntervalSlider.Value = settings.SceneChangeWatchIntervalMs;
        SceneChangeWatchPhashSlider.Value = settings.SceneChangeWatchPhashThreshold;
        UpdateLoggingState(settings.EnableLogging);
        UpdateOcrBinarizationThresholdValue();
        UpdateOcrGammaValue();
        UpdateOcrDownsampleScaleValue();
        UpdateOcrTwoPassThresholdValues();
        UpdateOverlayFontSizeValue();
        UpdateOverlayBackgroundOpacityValue();
        UpdateOcrPreprocessControls(settings);
        UpdatePaddleConfidenceThresholdValue();
        UpdateSceneChangeThresholdValue();
        UpdateSceneChangeControls(settings);
        UpdateSceneChangeWatchValues();
        UpdateRoiStatus(settings);
        UpdateLanguageCustomVisibility();
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
        var ct2Status = settings.EnableCTranslate2 ? "CTranslate2: enabled" : "CTranslate2: disabled";
        var geminiStatus = settings.EnableGemini
            ? (string.IsNullOrWhiteSpace(settings.ApiKey) ? "Gemini: key missing" : "Gemini: enabled")
            : "Gemini: disabled";
        var deepLStatus = settings.EnableDeepL
            ? (string.IsNullOrWhiteSpace(settings.DeepLApiKey) ? "DeepL: key missing" : "DeepL: enabled")
            : "DeepL: disabled";
        TranslationStatusText.Text = $"Translation status: {llamaStatus} | {ct2Status} | {geminiStatus} | {deepLStatus}";
    }

    private void ReloadLlamaModelOptions(AppSettings settings)
    {
        if (LlamaModelBox == null)
        {
            return;
        }

        var fallback = NormalizeLlamaModelFileName(DefaultLlamaModelFileName);
        var selected = _llamaModelCatalog.NormalizeModelFileName(settings.LlamaSelectedModelFileName, fallback);
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

    private static void NormalizeCTranslate2Settings(AppSettings settings)
    {
        settings.CTranslate2Device = NormalizeCTranslate2Device(settings.CTranslate2Device);
        settings.CTranslate2Precision = ResolveCTranslate2Precision(settings.CTranslate2Device);
        settings.EnableCTranslate2AutoDownload = true;
        if (string.IsNullOrWhiteSpace(settings.CTranslate2ModelId))
        {
            settings.CTranslate2ModelId = "entai2965/nllb-200-distilled-600M-ctranslate2";
        }
    }

    private static void NormalizeLlamaSettings(AppSettings settings)
    {
        settings.LlamaHost = string.IsNullOrWhiteSpace(settings.LlamaHost)
            ? "127.0.0.1"
            : settings.LlamaHost.Trim();
        settings.LlamaPort = settings.LlamaPort <= 0 ? 8088 : settings.LlamaPort;
        settings.LlamaContextSize = Math.Max(256, settings.LlamaContextSize);
        settings.LlamaGpuLayers = Math.Max(0, settings.LlamaGpuLayers);
        settings.LlamaThreads = Math.Max(1, settings.LlamaThreads);
        settings.LlamaParallel = Math.Max(1, settings.LlamaParallel);
        settings.LlamaBatchSize = Math.Max(1, settings.LlamaBatchSize);
        settings.LlamaMaxTokens = Math.Max(1, settings.LlamaMaxTokens);
        settings.LlamaTemperature = Math.Clamp(settings.LlamaTemperature, 0.0, 2.0);
        settings.LlamaTopP = Math.Clamp(settings.LlamaTopP, 0.0, 1.0);
        settings.LlamaTopK = Math.Max(0, settings.LlamaTopK);
        settings.LlamaRepeatPenalty = Math.Clamp(settings.LlamaRepeatPenalty, 0.5, 2.0);
        settings.LlamaGrpcHost = string.IsNullOrWhiteSpace(settings.LlamaGrpcHost)
            ? "127.0.0.1"
            : settings.LlamaGrpcHost.Trim();
        settings.LlamaGrpcPort = settings.LlamaGrpcPort <= 0 ? 50071 : settings.LlamaGrpcPort;
        settings.LlamaSelectedModelFileName = NormalizeLlamaModelFileName(settings.LlamaSelectedModelFileName);
    }

    private static string NormalizeLlamaModelFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(fileName) ||
               !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? DefaultLlamaModelFileName
            : fileName;
    }

    private static string NormalizeCTranslate2Device(string? device)
    {
        var normalized = (device ?? string.Empty).Trim();
        if (normalized.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
        {
            return "gpu";
        }

        return "cpu";
    }

    private static string ResolveCTranslate2Precision(string? device)
    {
        var normalized = NormalizeCTranslate2Device(device);
        return normalized == "gpu" ? "fp16" : "int8";
    }

    private static CTranslate2HostConfig BuildCTranslate2HostConfig(AppSettings settings)
    {
        NormalizeCTranslate2Settings(settings);
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
        NormalizeLlamaSettings(settings);
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

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLanguageCustomVisibility();
        _ = SaveSettingsAsync();
    }

    private async void OnHotkeySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private void OnSwapLanguages(object sender, RoutedEventArgs e)
    {
        var sourceIsCustom = IsCustomSelected(SourceLangCombo);
        var targetIsCustom = IsCustomSelected(TargetLangCombo);
        var sourceTag = GetSelectedLanguageTag(SourceLangCombo);
        var targetTag = GetSelectedLanguageTag(TargetLangCombo);
        var sourceCustom = SourceLangCustom.Text;
        var targetCustom = TargetLangCustom.Text;

        if (targetIsCustom)
        {
            SelectLanguageByTag(SourceLangCombo, "custom");
            SourceLangCustom.Text = targetCustom;
        }
        else
        {
            SelectLanguageByTag(SourceLangCombo, targetTag);
        }

        if (sourceIsCustom)
        {
            SelectLanguageByTag(TargetLangCombo, "custom");
            TargetLangCustom.Text = sourceCustom;
        }
        else
        {
            SelectLanguageByTag(TargetLangCombo, sourceTag);
        }

        UpdateLanguageCustomVisibility();
        _ = SaveSettingsAsync();
    }

    private void UpdateLanguageCustomVisibility()
    {
        SourceLangCustom.Visibility = IsCustomSelected(SourceLangCombo) ? Visibility.Visible : Visibility.Collapsed;
        TargetLangCustom.Visibility = IsCustomSelected(TargetLangCombo) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyHotkeySettingsToUi(AppSettings settings)
    {
        SetHotkeyKey(HotkeyRunOnceKeyBox, settings.HotkeyRunOnceKey);
        SetHotkeyKey(HotkeyToggleOverlayKeyBox, settings.HotkeyToggleOverlayKey);
        SetHotkeyKey(HotkeyForceRunKeyBox, settings.HotkeyForceRunKey);
        SetHotkeyKey(HotkeyOcrOnlyKeyBox, settings.HotkeyOcrOnlyKey);

        SetHotkeyModifiers(settings.HotkeyRunOnceModifiers, HotkeyRunOnceCtrl, HotkeyRunOnceAlt, HotkeyRunOnceShift);
        SetHotkeyModifiers(settings.HotkeyToggleOverlayModifiers, HotkeyToggleOverlayCtrl, HotkeyToggleOverlayAlt, HotkeyToggleOverlayShift);
        SetHotkeyModifiers(settings.HotkeyForceRunModifiers, HotkeyForceRunCtrl, HotkeyForceRunAlt, HotkeyForceRunShift);
        SetHotkeyModifiers(settings.HotkeyOcrOnlyModifiers, HotkeyOcrOnlyCtrl, HotkeyOcrOnlyAlt, HotkeyOcrOnlyShift);
    }

    private void ApplyHotkeySettingsFromUi(AppSettings settings)
    {
        settings.HotkeyRunOnceKey = GetHotkeyKey(HotkeyRunOnceKeyBox);
        settings.HotkeyRunOnceModifiers = GetHotkeyModifiers(HotkeyRunOnceCtrl, HotkeyRunOnceAlt, HotkeyRunOnceShift);
        settings.HotkeyToggleOverlayKey = GetHotkeyKey(HotkeyToggleOverlayKeyBox);
        settings.HotkeyToggleOverlayModifiers = GetHotkeyModifiers(HotkeyToggleOverlayCtrl, HotkeyToggleOverlayAlt, HotkeyToggleOverlayShift);
        settings.HotkeyForceRunKey = GetHotkeyKey(HotkeyForceRunKeyBox);
        settings.HotkeyForceRunModifiers = GetHotkeyModifiers(HotkeyForceRunCtrl, HotkeyForceRunAlt, HotkeyForceRunShift);
        settings.HotkeyOcrOnlyKey = GetHotkeyKey(HotkeyOcrOnlyKeyBox);
        settings.HotkeyOcrOnlyModifiers = GetHotkeyModifiers(HotkeyOcrOnlyCtrl, HotkeyOcrOnlyAlt, HotkeyOcrOnlyShift);
    }

    private static bool IsCustomSelected(ComboBox comboBox)
    {
        return GetSelectedLanguageTag(comboBox) == "custom";
    }

    private static string GetSelectedLanguageTag(ComboBox comboBox)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return string.Empty;
    }

    private static string GetSelectedTag(ComboBox comboBox, string fallback)
    {
        if (comboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag;
        }

        return fallback;
    }

    private static void SelectLanguageByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string itemTag && itemTag == tag)
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }

        if (comboBox.Items.Count > 0)
        {
            comboBox.SelectedIndex = 0;
        }
    }

    private static void ApplyLanguageSelection(ComboBox comboBox, TextBox customBox, string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        var matched = false;
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem comboItem && comboItem.Tag is string itemTag &&
                itemTag.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                matched = true;
                break;
            }
        }

        if (!matched)
        {
            SelectLanguageByTag(comboBox, "custom");
            customBox.Text = normalized;
        }
    }

    private static string GetSelectedLanguage(ComboBox comboBox, TextBox customBox)
    {
        var tag = GetSelectedLanguageTag(comboBox);
        if (tag == "custom")
        {
            return customBox.Text.Trim();
        }

        return tag;
    }

    private AppCaptureMode GetCaptureMode()
    {
        if (CaptureModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag == "Screen" ? AppCaptureMode.Screen : AppCaptureMode.ActiveWindow;
        }

        return AppCaptureMode.ActiveWindow;
    }

    private CaptureProviderKind GetCaptureProviderKind()
    {
        if (CaptureProviderBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag switch
            {
                "Dxgi" => CaptureProviderKind.Dxgi,
                "Gdi" => CaptureProviderKind.Gdi,
                _ => CaptureProviderKind.Wgc
            };
        }

        return CaptureProviderKind.Wgc;
    }

    private OcrEngineKind GetOcrEngineKind()
    {
        if (OcrEngineBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag == "Paddle" ? OcrEngineKind.Paddle : OcrEngineKind.WinRt;
        }

        return OcrEngineKind.WinRt;
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

    private static void SetHotkeyKey(ComboBox comboBox, string key)
    {
        if (comboBox.Items.Count == 0)
        {
            return;
        }

        foreach (var item in comboBox.Items)
        {
            if (string.Equals(item?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.SelectedIndex = 0;
    }

    private static string GetHotkeyKey(ComboBox comboBox)
    {
        return comboBox.SelectedItem?.ToString() ?? "F8";
    }

    private static void SetHotkeyModifiers(string value, CheckBox ctrl, CheckBox alt, CheckBox shift)
    {
        var modifiers = ParseModifiers(value);
        ctrl.IsChecked = modifiers.HasFlag(ModifierKeys.Control);
        alt.IsChecked = modifiers.HasFlag(ModifierKeys.Alt);
        shift.IsChecked = modifiers.HasFlag(ModifierKeys.Shift);
    }

    private static string GetHotkeyModifiers(CheckBox ctrl, CheckBox alt, CheckBox shift)
    {
        var modifiers = ModifierKeys.None;
        if (ctrl.IsChecked == true)
        {
            modifiers |= ModifierKeys.Control;
        }

        if (alt.IsChecked == true)
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (shift.IsChecked == true)
        {
            modifiers |= ModifierKeys.Shift;
        }

        return modifiers == ModifierKeys.None ? "None" : modifiers.ToString();
    }

    private void ApplyTranslationPriority(AppSettings settings)
    {
        var ordered = NormalizeTranslationPriority(settings);
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
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = settings.TranslationPriority ?? new List<string>();
        foreach (var name in current)
        {
            if (string.IsNullOrWhiteSpace(name))
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

    private void OnTranslationPriorityUp(object sender, RoutedEventArgs e)
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
        _ = SaveSettingsAsync();
    }

    private void OnTranslationPriorityDown(object sender, RoutedEventArgs e)
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
        _ = SaveSettingsAsync();
    }

    private async void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnReloadLlamaModels(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Settings;
        ReloadLlamaModelOptions(settings);
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnSettingLostFocus(object sender, RoutedEventArgs e)
    {
        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOcrBinarizationThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOcrBinarizationThresholdValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOcrGammaChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOcrGammaValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnPaddleConfidenceThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdatePaddleConfidenceThresholdValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOcrDownsampleScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOcrDownsampleScaleValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOcrTwoPassLowThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOcrTwoPassThresholdValues();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOcrTwoPassHighThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOcrTwoPassThresholdValues();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOverlayFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOverlayFontSizeValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnOverlayBackgroundOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOverlayBackgroundOpacityValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnSceneChangeThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSceneChangeThresholdValue();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnSceneChangeWatchIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSceneChangeWatchValues();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async void OnSceneChangeWatchPhashChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateSceneChangeWatchValues();
        if (_isApplyingSettings)
        {
            return;
        }

        await SaveSettingsAsync().ConfigureAwait(true);
    }

    private async Task SaveSettingsAsync()
    {
        if (_isApplyingSettings || !IsLoaded)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.CaptureMode = GetCaptureMode();
        settings.CaptureProviderMode = CaptureProviderFixedCheck.IsChecked == true
            ? CaptureProviderMode.Fixed
            : CaptureProviderMode.Auto;
        settings.PreferredCaptureProvider = GetCaptureProviderKind();
        settings.SourceLanguage = GetSelectedLanguage(SourceLangCombo, SourceLangCustom);
        settings.TargetLanguage = GetSelectedLanguage(TargetLangCombo, TargetLangCustom);
        settings.EnableRoi = EnableRoiCheck.IsChecked == true;
        settings.OcrEngine = GetOcrEngineKind();
        settings.PaddleTextDetectionModelName = GetSelectedTag(PaddleDetectionModelBox, "PP-OCRv5_mobile_det");
        settings.PaddleTextRecognitionModelName = GetSelectedTag(PaddleRecognitionModelBox, "PP-OCRv5_server_rec");
        settings.EnablePaddleConfidenceFilter = EnablePaddleConfidenceFilterCheck.IsChecked == true;
        settings.PaddleConfidenceThreshold = Math.Round(PaddleConfidenceThresholdSlider.Value, 2);
        settings.EnableCTranslate2 = EnableCTranslate2Check.IsChecked == true;
        settings.CTranslate2Device = GetSelectedTag(CTranslate2DeviceBox, "cpu");
        NormalizeCTranslate2Settings(settings);
        settings.EnableLlamaCppTranslation = EnableLlamaCppCheck.IsChecked == true;
        settings.LlamaSelectedModelFileName = GetSelectedTag(LlamaModelBox, DefaultLlamaModelFileName);
        settings.LlamaHost = LlamaHostBox.Text.Trim();
        if (int.TryParse(LlamaPortBox.Text.Trim(), out var llamaPort))
        {
            settings.LlamaPort = llamaPort;
        }
        if (int.TryParse(LlamaContextSizeBox.Text.Trim(), out var llamaContext))
        {
            settings.LlamaContextSize = llamaContext;
        }
        if (int.TryParse(LlamaGpuLayersBox.Text.Trim(), out var llamaGpuLayers))
        {
            settings.LlamaGpuLayers = llamaGpuLayers;
        }
        if (int.TryParse(LlamaThreadsBox.Text.Trim(), out var llamaThreads))
        {
            settings.LlamaThreads = llamaThreads;
        }
        if (int.TryParse(LlamaParallelBox.Text.Trim(), out var llamaParallel))
        {
            settings.LlamaParallel = llamaParallel;
        }
        if (int.TryParse(LlamaBatchSizeBox.Text.Trim(), out var llamaBatchSize))
        {
            settings.LlamaBatchSize = llamaBatchSize;
        }
        if (int.TryParse(LlamaMaxTokensBox.Text.Trim(), out var llamaMaxTokens))
        {
            settings.LlamaMaxTokens = llamaMaxTokens;
        }
        if (double.TryParse(LlamaTemperatureBox.Text.Trim(), out var llamaTemperature))
        {
            settings.LlamaTemperature = llamaTemperature;
        }
        if (double.TryParse(LlamaTopPBox.Text.Trim(), out var llamaTopP))
        {
            settings.LlamaTopP = llamaTopP;
        }
        if (int.TryParse(LlamaTopKBox.Text.Trim(), out var llamaTopK))
        {
            settings.LlamaTopK = llamaTopK;
        }
        if (double.TryParse(LlamaRepeatPenaltyBox.Text.Trim(), out var llamaRepeatPenalty))
        {
            settings.LlamaRepeatPenalty = llamaRepeatPenalty;
        }
        NormalizeLlamaSettings(settings);
        if (settings.EnableLlamaCppTranslation && settings.EnableCTranslate2)
        {
            DisableCTranslate2(settings);
        }
        settings.EnableDeepL = EnableDeepLCheck.IsChecked == true;
        settings.DeepLApiKey = DeepLApiKeyBox.Password;
        settings.DeepLEndpoint = DeepLEndpointBox.Text.Trim();
        settings.EnableGemini = EnableGeminiCheck.IsChecked == true;
        settings.TranslationPriority = GetTranslationPriority();
        settings.ApiKey = ApiKeyBox.Password;
        ApplyHotkeySettingsFromUi(settings);
        settings.EnableOcrBinarization = EnableOcrBinarizationCheck.IsChecked == true;
        settings.OcrBinarizationThreshold = (int)Math.Round(OcrBinarizationThresholdSlider.Value);
        settings.EnableOcrAutoThreshold = EnableOcrAutoThresholdCheck.IsChecked == true;
        settings.EnableOcrAutoInvert = EnableOcrAutoInvertCheck.IsChecked == true;
        settings.EnableOcrGamma = EnableOcrGammaCheck.IsChecked == true;
        settings.OcrGamma = Math.Round(OcrGammaSlider.Value, 2);
        settings.EnableLogging = EnableLoggingCheck.IsChecked == true;
        settings.EnableOcrPerfLog = EnableOcrPerfLogCheck.IsChecked == true;
        settings.EnableOcrDownsampling = EnableOcrDownsamplingCheck.IsChecked == true;
        settings.OcrDownsampleScale = Math.Round(OcrDownsampleScaleSlider.Value, 2);
        settings.EnableOcrTwoPass = EnableOcrTwoPassCheck.IsChecked == true;
        settings.OcrTwoPassPreferAuto = OcrTwoPassPreferAutoCheck.IsChecked == true;
        settings.OcrTwoPassLowThreshold = (int)Math.Round(OcrTwoPassLowThresholdSlider.Value);
        settings.OcrTwoPassHighThreshold = (int)Math.Round(OcrTwoPassHighThresholdSlider.Value);
        settings.OverlayFontSize = Math.Round(OverlayFontSizeSlider.Value, 1);
        settings.OverlayBackgroundOpacity = Math.Round(OverlayBackgroundOpacitySlider.Value, 2);
        settings.EnableFixedRoiOverlay = EnableFixedRoiOverlayCheck.IsChecked == true;
        settings.EnableOverlayShortLineShrink = EnableOverlayShortLineShrinkCheck.IsChecked == true;
        settings.EnableSceneChangeAutoHide = EnableSceneChangeAutoHideCheck.IsChecked == true;
        settings.EnableSceneChangeTextWeighted = EnableSceneChangeTextWeightedCheck.IsChecked == true;
        settings.SceneChangeThreshold = SceneChangeThresholdSlider.Value;
        settings.SceneChangeWatchIntervalMs = (int)Math.Round(SceneChangeWatchIntervalSlider.Value);
        settings.SceneChangeWatchPhashThreshold = (int)Math.Round(SceneChangeWatchPhashSlider.Value);

        if (int.TryParse(PhashThresholdBox.Text.Trim(), out var phashThreshold))
        {
            settings.PhashThreshold = phashThreshold;
        }

        if (double.TryParse(IouThresholdBox.Text.Trim(), out var iouThreshold))
        {
            settings.OcrIouThreshold = iouThreshold;
        }

        if (int.TryParse(OcrPerfLogThresholdBox.Text.Trim(), out var perfThreshold))
        {
            settings.OcrPerfLogThresholdMs = Math.Max(0, perfThreshold);
        }

        _overlayWindow?.ApplyStyle(settings);
        UpdateLoggingState(settings.EnableLogging);
        _overlayPresenter?.UpdatePerfLogging(settings.EnableOcrPerfLog && settings.EnableLogging, settings.OcrPerfLogThresholdMs);
        UpdateOcrBinarizationThresholdValue();
        UpdateOcrGammaValue();
        UpdateOcrDownsampleScaleValue();
        UpdateOcrTwoPassThresholdValues();
        UpdateOverlayFontSizeValue();
        UpdateOverlayBackgroundOpacityValue();
        UpdateOcrPreprocessControls(settings);
        UpdateSceneChangeThresholdValue();
        UpdateSceneChangeControls(settings);
        UpdateSceneChangeWatchValues();
        UpdateRoiStatus(settings);
        UpdateTranslationStatus(settings);
        await EnsureResourceHostsAsync(settings).ConfigureAwait(true);
        await _settingsService.SaveAsync().ConfigureAwait(true);
        AppendLog("Settings saved.");
        TryUpdateHotkeys(settings);
        UpdateAutoHideWatcher(settings);
    }

    private void PopulateHotkeyKeyBoxes()
    {
        var keys = BuildHotkeyKeyOptions();
        HotkeyRunOnceKeyBox.ItemsSource = keys;
        HotkeyToggleOverlayKeyBox.ItemsSource = keys;
        HotkeyForceRunKeyBox.ItemsSource = keys;
        HotkeyOcrOnlyKeyBox.ItemsSource = keys;
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

        var fallback = HotkeyConfig.Default;
        if (TryRegisterHotkeys(fallback))
        {
            _currentHotkeyConfig = fallback;
            AppendLog("Hotkey fallback applied due to registration failure.");
        }
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
                      $"OcrOnly={FormatHotkey(config.OcrOnlyKey, config.OcrOnlyModifiers)}.");
        }
        else
        {
            AppendLog("Hotkey update failed; keeping previous hotkeys.");
        }
    }

    private bool TryRegisterHotkeys(HotkeyConfig config)
    {
        HotkeyManager? runOnce = null;
        HotkeyManager? toggle = null;
        HotkeyManager? forceRun = null;
        HotkeyManager? ocrOnly = null;

        try
        {
            runOnce = new HotkeyManager(this, config.RunOnceKey, config.RunOnceModifiers, id: 1);
            runOnce.HotkeyPressed += OnHotkeyPressed;
            runOnce.Register();

            toggle = new HotkeyManager(this, config.ToggleOverlayKey, config.ToggleOverlayModifiers, id: 2);
            toggle.HotkeyPressed += OnToggleOverlayHotkeyPressed;
            toggle.Register();

            forceRun = new HotkeyManager(this, config.ForceRunKey, config.ForceRunModifiers, id: 3);
            forceRun.HotkeyPressed += OnForceRunHotkeyPressed;
            forceRun.Register();

            ocrOnly = new HotkeyManager(this, config.OcrOnlyKey, config.OcrOnlyModifiers, id: 4);
            ocrOnly.HotkeyPressed += OnOcrOnlyHotkeyPressed;
            ocrOnly.Register();
        }
        catch (Exception ex)
        {
            runOnce?.Dispose();
            toggle?.Dispose();
            forceRun?.Dispose();
            ocrOnly?.Dispose();
            _logger?.Error(ex, "Failed to register hotkeys.");
            return false;
        }

        _hotkeyManager?.Dispose();
        _overlayToggleHotkeyManager?.Dispose();
        _forceRunHotkeyManager?.Dispose();
        _ocrOnlyHotkeyManager?.Dispose();

        _hotkeyManager = runOnce;
        _overlayToggleHotkeyManager = toggle;
        _forceRunHotkeyManager = forceRun;
        _ocrOnlyHotkeyManager = ocrOnly;
        return true;
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
            ParseKey(settings.HotkeyOcrOnlyKey, Key.F11),
            ParseModifiers(settings.HotkeyOcrOnlyModifiers));
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

    private void UpdateOcrBinarizationThresholdValue()
    {
        if (OcrBinarizationThresholdValue == null || OcrBinarizationThresholdSlider == null)
        {
            return;
        }

        OcrBinarizationThresholdValue.Text = ((int)Math.Round(OcrBinarizationThresholdSlider.Value)).ToString();
    }

    private void UpdateOcrGammaValue()
    {
        if (OcrGammaValue == null || OcrGammaSlider == null)
        {
            return;
        }

        OcrGammaValue.Text = OcrGammaSlider.Value.ToString("0.00");
    }

    private void UpdatePaddleConfidenceThresholdValue()
    {
        if (PaddleConfidenceThresholdValue == null || PaddleConfidenceThresholdSlider == null)
        {
            return;
        }

        PaddleConfidenceThresholdValue.Text = PaddleConfidenceThresholdSlider.Value.ToString("0.00");
    }

    private void UpdateOcrDownsampleScaleValue()
    {
        if (OcrDownsampleScaleValue == null || OcrDownsampleScaleSlider == null)
        {
            return;
        }

        OcrDownsampleScaleValue.Text = OcrDownsampleScaleSlider.Value.ToString("0.00");
    }

    private void UpdateOcrTwoPassThresholdValues()
    {
        if (OcrTwoPassLowThresholdValue == null || OcrTwoPassLowThresholdSlider == null ||
            OcrTwoPassHighThresholdValue == null || OcrTwoPassHighThresholdSlider == null)
        {
            return;
        }

        OcrTwoPassLowThresholdValue.Text = ((int)Math.Round(OcrTwoPassLowThresholdSlider.Value)).ToString();
        OcrTwoPassHighThresholdValue.Text = ((int)Math.Round(OcrTwoPassHighThresholdSlider.Value)).ToString();
    }

    private void UpdateOverlayFontSizeValue()
    {
        if (OverlayFontSizeValue == null || OverlayFontSizeSlider == null)
        {
            return;
        }

        OverlayFontSizeValue.Text = OverlayFontSizeSlider.Value.ToString("0.0");
    }

    private void UpdateOverlayBackgroundOpacityValue()
    {
        if (OverlayBackgroundOpacityValue == null || OverlayBackgroundOpacitySlider == null)
        {
            return;
        }

        OverlayBackgroundOpacityValue.Text = OverlayBackgroundOpacitySlider.Value.ToString("0.00");
    }

    private void UpdateSceneChangeThresholdValue()
    {
        if (SceneChangeThresholdValue == null || SceneChangeThresholdSlider == null)
        {
            return;
        }

        SceneChangeThresholdValue.Text = SceneChangeThresholdSlider.Value.ToString("0.00");
    }

    private void UpdateSceneChangeControls(AppSettings settings)
    {
        if (EnableSceneChangeAutoHideCheck == null || EnableSceneChangeTextWeightedCheck == null ||
            SceneChangeThresholdSlider == null || SceneChangeThresholdValue == null ||
            SceneChangeWatchIntervalSlider == null || SceneChangeWatchIntervalValue == null ||
            SceneChangeWatchPhashSlider == null || SceneChangeWatchPhashValue == null)
        {
            return;
        }

        var enabled = settings.EnableSceneChangeAutoHide;
        EnableSceneChangeTextWeightedCheck.IsEnabled = enabled;
        SceneChangeThresholdSlider.IsEnabled = enabled;
        SceneChangeThresholdValue.Foreground = enabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        SceneChangeWatchIntervalSlider.IsEnabled = enabled;
        SceneChangeWatchIntervalValue.Foreground = enabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        SceneChangeWatchPhashSlider.IsEnabled = enabled;
        SceneChangeWatchPhashValue.Foreground = enabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
    }

    private void UpdateSceneChangeWatchValues()
    {
        if (SceneChangeWatchIntervalValue == null || SceneChangeWatchIntervalSlider == null ||
            SceneChangeWatchPhashValue == null || SceneChangeWatchPhashSlider == null)
        {
            return;
        }

        SceneChangeWatchIntervalValue.Text = ((int)Math.Round(SceneChangeWatchIntervalSlider.Value)).ToString();
        SceneChangeWatchPhashValue.Text = ((int)Math.Round(SceneChangeWatchPhashSlider.Value)).ToString();
    }

    private void OnOverlayShown()
    {
        _overlayVisible = true;
        UpdateAutoHideWatcher(_settingsService.Settings);
        ScheduleAutoHideBaselineReset();
    }

    private void OnOverlayHidden()
    {
        _overlayVisible = false;
        StopAutoHideWatcher();
    }

    private void OnOverlayUpdated()
    {
        ScheduleAutoHideBaselineReset();
    }

    private void InitializeAutoHideWatcher(AppSettings settings)
    {
        _autoHideTimer = new DispatcherTimer(DispatcherPriority.Background);
        _autoHideTimer.Tick += OnAutoHideTick;
        UpdateAutoHideWatcher(settings);
        if (_overlayVisible)
        {
            ScheduleAutoHideBaselineReset();
        }
    }

    private void UpdateAutoHideWatcher(AppSettings settings)
    {
        if (_autoHideTimer == null)
        {
            return;
        }

        if (!settings.EnableSceneChangeAutoHide || !_overlayVisible)
        {
            StopAutoHideWatcher();
            return;
        }

        var intervalMs = Math.Clamp(settings.SceneChangeWatchIntervalMs, 200, 10000);
        _autoHideTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        if (!_autoHideTimer.IsEnabled)
        {
            _autoHideTimer.Start();
        }
    }

    private void StopAutoHideWatcher()
    {
        if (_autoHideTimer != null && _autoHideTimer.IsEnabled)
        {
            _autoHideTimer.Stop();
        }

        ClearAutoHideBaseline();
    }

    private void ClearAutoHideBaseline()
    {
        _autoHideBaselineVersion++;
        _autoHideLastHash = null;
        _autoHideBaselinePending = false;
        if (_autoHideBaselineCts != null)
        {
            _autoHideBaselineCts.Cancel();
            _autoHideBaselineCts.Dispose();
            _autoHideBaselineCts = null;
        }
    }

    private void ScheduleAutoHideBaselineReset()
    {
        var settings = _settingsService.Settings;
        if (!_overlayVisible || !settings.EnableSceneChangeAutoHide || _captureManager == null || _phashService == null)
        {
            return;
        }

        var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
        var perfThresholdMs = Math.Max(0, settings.OcrPerfLogThresholdMs);
        _autoHideBaselineVersion++;
        _autoHideBaselinePending = true;
        _autoHideLastHash = null;
        _autoHideBaselineCts?.Cancel();
        _autoHideBaselineCts?.Dispose();
        _autoHideBaselineCts = new CancellationTokenSource();
        var token = _autoHideBaselineCts.Token;

        _ = Task.Run(async () =>
        {
            Stopwatch? baselineStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
            try
            {
                // WHY: Delay a bit so the overlay frame is fully composed before hashing.
                await Task.Delay(OverlayBaselineDelayMs, token).ConfigureAwait(false);
                if (token.IsCancellationRequested)
                {
                    return;
                }

                using var frame = _captureManager.Capture(settings);
                if (frame.IsBlack)
                {
                    return;
                }

                var roiScreen = GetRoiBounds(settings, frame.Bounds);
                if (roiScreen.IsEmpty)
                {
                    return;
                }

                var roiInFrame = new Rect(
                    roiScreen.X - frame.Bounds.X,
                    roiScreen.Y - frame.Bounds.Y,
                    roiScreen.Width,
                    roiScreen.Height);

                using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
                _autoHideLastHash = _phashService.ComputeHash(roiBitmap);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Auto-hide baseline reset failed.");
            }
            finally
            {
                if (baselineStopwatch != null)
                {
                    baselineStopwatch.Stop();
                    if (baselineStopwatch.ElapsedMilliseconds >= perfThresholdMs)
                    {
                        _logger?.Info($"[Perf] AutoHideBaselineReset={baselineStopwatch.ElapsedMilliseconds}ms.");
                    }
                }
                _autoHideBaselinePending = false;
            }
        }, token);
    }

    private async void OnAutoHideTick(object? sender, EventArgs e)
    {
        if (_autoHideTickInProgress || _captureManager == null || _phashService == null)
        {
            return;
        }

        var settings = _settingsService.Settings;
        if (!settings.EnableSceneChangeAutoHide || !_overlayVisible || _autoHideBaselinePending || !_autoHideLastHash.HasValue)
        {
            return;
        }

        var baselineHash = _autoHideLastHash.Value;
        var baselineVersion = _autoHideBaselineVersion;
        var perfEnabled = settings.EnableOcrPerfLog && settings.EnableLogging;
        var perfThresholdMs = Math.Max(0, settings.OcrPerfLogThresholdMs);
        _autoHideTickInProgress = true;
        try
        {
            await Task.Run(() =>
            {
                Stopwatch? watcherStopwatch = perfEnabled ? Stopwatch.StartNew() : null;
                try
                {
                    using var frame = _captureManager.Capture(settings);
                    if (frame.IsBlack)
                    {
                        return;
                    }

                    var roiScreen = GetRoiBounds(settings, frame.Bounds);
                    if (roiScreen.IsEmpty)
                    {
                        return;
                    }

                    var roiInFrame = new Rect(
                        roiScreen.X - frame.Bounds.X,
                        roiScreen.Y - frame.Bounds.Y,
                        roiScreen.Width,
                        roiScreen.Height);

                    using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);
                    var hash = _phashService.ComputeHash(roiBitmap);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    var diff = _phashService.HammingDistance(hash, baselineHash);
                    var threshold = Math.Clamp(settings.SceneChangeWatchPhashThreshold, 0, 64);
                    if (baselineVersion != _autoHideBaselineVersion)
                    {
                        return;
                    }

                    if (diff >= threshold)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            _overlayEnabled = false;
                            _overlayPresenter?.SetEnabled(false);
                            AppendLog($"Overlay auto-hidden (watcher diff {diff}).");
                        });
                    }

                    if (baselineVersion == _autoHideBaselineVersion)
                    {
                        _autoHideLastHash = hash;
                    }
                }
                finally
                {
                    if (watcherStopwatch != null)
                    {
                        watcherStopwatch.Stop();
                        if (watcherStopwatch.ElapsedMilliseconds >= perfThresholdMs)
                        {
                            _logger?.Info($"[Perf] AutoHideWatcherTick={watcherStopwatch.ElapsedMilliseconds}ms.");
                        }
                    }
                }
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Auto-hide watcher failed.");
        }
        finally
        {
            _autoHideTickInProgress = false;
        }
    }

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

    private void UpdateOcrPreprocessControls(AppSettings settings)
    {
        if (OcrBinarizationThresholdSlider == null || OcrBinarizationThresholdValue == null ||
            EnableOcrAutoThresholdCheck == null || EnableOcrAutoInvertCheck == null ||
            EnableOcrGammaCheck == null || OcrGammaSlider == null || OcrGammaValue == null ||
            EnableOcrDownsamplingCheck == null || OcrDownsampleScaleSlider == null || OcrDownsampleScaleValue == null ||
            EnableLoggingCheck == null || EnableOcrPerfLogCheck == null || OcrPerfLogThresholdBox == null ||
            EnableOcrTwoPassCheck == null || OcrTwoPassPreferAutoCheck == null ||
            OcrTwoPassLowThresholdSlider == null || OcrTwoPassLowThresholdValue == null ||
            OcrTwoPassHighThresholdSlider == null || OcrTwoPassHighThresholdValue == null)
        {
            return;
        }

        var enabled = settings.EnableOcrBinarization;
        var manualThresholdEnabled = enabled && !settings.EnableOcrAutoThreshold;
        OcrBinarizationThresholdSlider.IsEnabled = manualThresholdEnabled;
        EnableOcrAutoThresholdCheck.IsEnabled = enabled;
        EnableOcrAutoInvertCheck.IsEnabled = enabled;
        EnableOcrTwoPassCheck.IsEnabled = enabled;
        OcrGammaSlider.IsEnabled = settings.EnableOcrGamma;
        OcrDownsampleScaleSlider.IsEnabled = settings.EnableOcrDownsampling;
        EnableOcrPerfLogCheck.IsEnabled = settings.EnableLogging;
        OcrPerfLogThresholdBox.IsEnabled = settings.EnableLogging && settings.EnableOcrPerfLog;
        var twoPassEnabled = enabled && settings.EnableOcrTwoPass;
        OcrTwoPassLowThresholdSlider.IsEnabled = twoPassEnabled;
        OcrTwoPassHighThresholdSlider.IsEnabled = twoPassEnabled;
        OcrTwoPassLowThresholdValue.Foreground = twoPassEnabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        OcrTwoPassHighThresholdValue.Foreground = twoPassEnabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        OcrTwoPassPreferAutoCheck.IsEnabled = twoPassEnabled && settings.EnableOcrAutoThreshold;
        OcrBinarizationThresholdValue.Foreground = manualThresholdEnabled
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        OcrGammaValue.Foreground = settings.EnableOcrGamma
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        OcrDownsampleScaleValue.Foreground = settings.EnableOcrDownsampling
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
        OcrPerfLogThresholdBox.Foreground = settings.EnableLogging && settings.EnableOcrPerfLog
            ? System.Windows.Media.Brushes.Black
            : System.Windows.Media.Brushes.DimGray;
    }

    private void AppendLog(string message)
    {
        if (!_loggingEnabled)
        {
            return;
        }

        _logQueue.Enqueue(message);
    }

    private void InitializeLogBuffer()
    {
        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background);
        _logFlushTimer.Interval = TimeSpan.FromMilliseconds(LogFlushIntervalMs);
        _logFlushTimer.Tick += OnLogFlushTick;
        _logFlushTimer.Start();
    }

    private void OnLogFlushTick(object? sender, EventArgs e)
    {
        FlushLogs();
    }

    private void FlushLogs()
    {
        if (!_loggingEnabled || LogBox == null)
        {
            ClearLogQueue();
            return;
        }

        if (_logFlushPending)
        {
            return;
        }

        var builder = new StringBuilder();
        while (_logQueue.TryDequeue(out var message))
        {
            builder.AppendLine(message);
        }

        if (builder.Length == 0)
        {
            return;
        }

        var payload = builder.ToString();
        _logFlushPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _logFlushPending = false;
            LogBox.AppendText(payload);
            _logLineCount += CountNewlines(payload);
            TrimLogLines(MaxLogLines);
            LogBox.ScrollToEnd();
        }));
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

    private void ClearLogQueue()
    {
        while (_logQueue.TryDequeue(out _))
        {
        }
    }

    private void UpdateLoggingState(bool enabled)
    {
        _loggingEnabled = enabled;
        _logger?.SetEnabled(enabled);
        if (!enabled)
        {
            ClearLogQueue();
            _logFlushPending = false;
        }
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
        if (Interlocked.CompareExchange(ref _runInProgress, 1, 1) != 1)
        {
            return;
        }

        _translationOverlayCts?.Cancel();
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

        if (token.IsCancellationRequested || Interlocked.CompareExchange(ref _runInProgress, 1, 1) != 1)
        {
            return;
        }

        SetBusyOverlay(true, "Translating...");
    }

    private void OnTranslationCompleted()
    {
        _translationOverlayCts?.Cancel();
        if (Interlocked.CompareExchange(ref _runInProgress, 1, 1) != 1)
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

    private void EnsureSettingsCategorySelection()
    {
        if (SettingsCategoryList.SelectedIndex < 0)
        {
            SettingsCategoryList.SelectedIndex = 0;
        }

        UpdateSettingsCategoryPanels();
    }

    private void OnSettingsCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSettingsCategoryPanels();
    }

    private void UpdateSettingsCategoryPanels()
    {
        if (SettingsCategoryList == null || SettingsPanelOcr == null || SettingsPanelPaddle == null ||
            SettingsPanelTranslation == null || SettingsPanelHotkeys == null)
        {
            return;
        }

        var index = SettingsCategoryList.SelectedIndex;
        SettingsPanelOcr.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelPaddle.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelTranslation.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelHotkeys.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
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
        Key OcrOnlyKey,
        ModifierKeys OcrOnlyModifiers)
    {
        public static HotkeyConfig Default => new(
            Key.F8,
            ModifierKeys.None,
            Key.F9,
            ModifierKeys.None,
            Key.F10,
            ModifierKeys.None,
            Key.F11,
            ModifierKeys.None);
    }
}
