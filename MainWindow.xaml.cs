using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.UI;
using AppCaptureMode = Hotkey_Translator.Models.CaptureMode;

namespace Hotkey_Translator;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly HttpClient _httpClient = new();
    private OverlayWindow? _overlayWindow;
    private OverlayPresenter? _overlayPresenter;
    private CacheRepository? _cacheRepository;
    private CaptureManager? _captureManager;
    private PipelineOrchestrator? _pipeline;
    private HotkeyManager? _hotkeyManager;
    private HotkeyManager? _overlayToggleHotkeyManager;
    private CancellationTokenSource? _runCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private readonly ObservableCollection<string> _translationPriority = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _logger = new AppLogger(AppendLog);
        await _settingsService.LoadAsync().ConfigureAwait(true);
        ApplySettingsToUi(_settingsService.Settings);
        TranslationPriorityList.ItemsSource = _translationPriority;

        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplyStyle(_settingsService.Settings);
        _overlayPresenter = new OverlayPresenter(_overlayWindow);
        _overlayPresenter.Show();

        _cacheRepository = new CacheRepository(_settingsService.CachePath);
        var frameGate = new FrameGate();
        _captureManager = new CaptureManager(frameGate, _logger);
        var ocrEngine = new OcrEngine(_logger);
        var ocrDiff = new OcrDiffService { IouThreshold = _settingsService.Settings.OcrIouThreshold };
        var phashService = new PhashService();
        var normalization = new NormalizationService();
        var lineGrouper = new OcrLineGrouper();
        var keyBuilder = new CacheKeyBuilder();
        var geminiClient = new GeminiClient(_httpClient, _logger);
        var translationProviders = new List<ITranslationProvider>
        {
            new GeminiTranslationProvider(geminiClient)
        };
        var translationService = new TranslationFallbackService(translationProviders, _logger);

        _pipeline = new PipelineOrchestrator(
            _captureManager,
            ocrEngine,
            ocrDiff,
            phashService,
            normalization,
            lineGrouper,
            _cacheRepository,
            keyBuilder,
            translationService,
            _overlayPresenter,
            _settingsService,
            _logger);

        _hotkeyManager = new HotkeyManager(this, Key.F8, ModifierKeys.None);
        _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
        _hotkeyManager.Register();
        _overlayToggleHotkeyManager = new HotkeyManager(this, Key.F9, ModifierKeys.None, id: 2);
        _overlayToggleHotkeyManager.HotkeyPressed += OnToggleOverlayHotkeyPressed;
        _overlayToggleHotkeyManager.Register();
        AppendLog("Ready. Press F8 to capture. Press F9 to toggle overlay.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _hotkeyManager?.Dispose();
        _overlayToggleHotkeyManager?.Dispose();
        _cacheRepository?.Dispose();
        _httpClient.Dispose();
        _overlayWindow?.Close();
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        await RunOnceAsync().ConfigureAwait(true);
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
        _overlayPresenter.SetEnabled(true);
        AppendLog("Overlay shown.");
    }

    private async void OnRunOnce(object sender, RoutedEventArgs e)
    {
        await RunOnceAsync().ConfigureAwait(true);
    }

    private async Task RunOnceAsync()
    {
        if (_pipeline == null)
        {
            return;
        }

        EnableOverlay();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        await _pipeline.RunOnceAsync(_runCts.Token).ConfigureAwait(true);
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

    private async void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Settings;
        settings.CaptureMode = GetCaptureMode();
        settings.SourceLanguage = SourceLangBox.Text.Trim();
        settings.TargetLanguage = TargetLangBox.Text.Trim();
        settings.EnableRoi = EnableRoiCheck.IsChecked == true;
        settings.OcrEngine = GetOcrEngineKind();
        settings.PaddleProjectDir = PaddleProjectDirBox.Text.Trim();
        settings.PaddleUvPath = PaddleUvPathBox.Text.Trim();
        settings.PaddleLanguage = PaddleLanguageBox.Text.Trim();
        settings.PaddleDevice = PaddleDeviceBox.Text.Trim();
        settings.PaddleModelDir = string.IsNullOrWhiteSpace(PaddleModelDirBox.Text) ? null : PaddleModelDirBox.Text.Trim();
        settings.EnableGemini = EnableGeminiCheck.IsChecked == true;
        settings.TranslationPriority = GetTranslationPriority();
        settings.ApiKey = ApiKeyBox.Password;

        if (int.TryParse(PhashThresholdBox.Text.Trim(), out var phashThreshold))
        {
            settings.PhashThreshold = phashThreshold;
        }

        if (double.TryParse(IouThresholdBox.Text.Trim(), out var iouThreshold))
        {
            settings.OcrIouThreshold = iouThreshold;
        }

        _overlayWindow?.ApplyStyle(settings);
        UpdateRoiStatus(settings);
        await _settingsService.SaveAsync().ConfigureAwait(true);
        AppendLog("Settings saved.");
    }

    private void ApplySettingsToUi(AppSettings settings)
    {
        CaptureModeBox.SelectedIndex = settings.CaptureMode == AppCaptureMode.Screen ? 0 : 1;
        SourceLangBox.Text = settings.SourceLanguage;
        TargetLangBox.Text = settings.TargetLanguage;
        EnableRoiCheck.IsChecked = settings.EnableRoi;
        SetComboBoxByTag(OcrEngineBox, settings.OcrEngine == OcrEngineKind.Paddle ? "Paddle" : "WinRt");
        PaddleProjectDirBox.Text = settings.PaddleProjectDir;
        PaddleUvPathBox.Text = settings.PaddleUvPath;
        PaddleLanguageBox.Text = settings.PaddleLanguage;
        PaddleDeviceBox.Text = settings.PaddleDevice;
        PaddleModelDirBox.Text = settings.PaddleModelDir ?? string.Empty;
        EnableGeminiCheck.IsChecked = settings.EnableGemini;
        ApplyTranslationPriority(settings);
        ApiKeyBox.Password = settings.ApiKey ?? string.Empty;
        PhashThresholdBox.Text = settings.PhashThreshold.ToString();
        IouThresholdBox.Text = settings.OcrIouThreshold.ToString("0.00");
        UpdateRoiStatus(settings);
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

    private AppCaptureMode GetCaptureMode()
    {
        if (CaptureModeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            return tag == "Screen" ? AppCaptureMode.Screen : AppCaptureMode.ActiveWindow;
        }

        return AppCaptureMode.ActiveWindow;
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
    }

    private void AppendLog(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendLog(message));
            return;
        }

        LogBox.AppendText(message + Environment.NewLine);
        LogBox.ScrollToEnd();
    }
}
