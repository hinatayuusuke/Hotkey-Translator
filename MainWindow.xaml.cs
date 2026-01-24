using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Imaging;
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
    private HotkeyManager? _forceRunHotkeyManager;
    private HotkeyManager? _ocrOnlyHotkeyManager;
    private CancellationTokenSource? _runCts;
    private AppLogger? _logger;
    private bool _overlayEnabled = true;
    private bool _hasRunOnce;
    private HotkeyConfig? _currentHotkeyConfig;
    private readonly ObservableCollection<string> _translationPriority = new();
    private bool _isApplyingSettings;

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
        await _settingsService.LoadAsync().ConfigureAwait(true);
        ApplySettingsToUi(_settingsService.Settings);
        TranslationPriorityList.ItemsSource = _translationPriority;
        EnsureSettingsCategorySelection();

        _overlayWindow = new OverlayWindow();
        _overlayWindow.ApplyStyle(_settingsService.Settings);
        _overlayPresenter = new OverlayPresenter(_overlayWindow);
        _overlayPresenter.Show();

        _cacheRepository = new CacheRepository(_settingsService.CachePath);
        var frameGate = new FrameGate();
        _captureManager = new CaptureManager(frameGate, _logger);
        var ocrEngine = new OcrEngine(_httpClient, _logger);
        var ocrDiff = new OcrDiffService { IouThreshold = _settingsService.Settings.OcrIouThreshold };
        var phashService = new PhashService();
        var normalization = new NormalizationService();
        var ocrPreprocess = new OcrPreprocessService();
        var lineGrouper = new OcrLineGrouper();
        var keyBuilder = new CacheKeyBuilder();
        var geminiClient = new GeminiClient(_httpClient, _logger);
        var translationProviders = new List<ITranslationProvider>
        {
            new DeepLTranslationProvider(_httpClient, _logger),
            new GeminiTranslationProvider(geminiClient)
        };
        var translationService = new TranslationFallbackService(translationProviders, _logger);

        _pipeline = new PipelineOrchestrator(
            _captureManager,
            ocrEngine,
            ocrDiff,
            phashService,
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

        InitializeHotkeys(_settingsService.Settings);
        AppendLog("Ready. F8: hide overlay if shown, or run once if hidden. F9: toggle overlay. F10: force run. F11: OCR only.");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _runCts?.Cancel();
        _runCts?.Dispose();
        _hotkeyManager?.Dispose();
        _overlayToggleHotkeyManager?.Dispose();
        _forceRunHotkeyManager?.Dispose();
        _ocrOnlyHotkeyManager?.Dispose();
        _cacheRepository?.Dispose();
        _httpClient.Dispose();
        if (_pipeline != null)
        {
            _pipeline.OcrPreprocessPreviewReady -= OnOcrPreprocessPreviewReady;
        }
        _overlayWindow?.Close();
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (!_hasRunOnce)
        {
            AppendLog("F8: Run once (first run).");
            await RunOnceAsync().ConfigureAwait(true);
            return;
        }

        if (_overlayEnabled)
        {
            _overlayEnabled = false;
            _overlayPresenter?.SetEnabled(false);
            AppendLog("F8: Overlay hidden.");
            return;
        }

        AppendLog("F8: Run once (overlay shown).");
        await RunOnceAsync().ConfigureAwait(true);
    }

    private async void OnForceRunHotkeyPressed(object? sender, EventArgs e)
    {
        AppendLog("Force run: skip pHash, OCR diff, translation cache.");
        await RunOnceAsync(new ForceRunOptions(SkipPhash: true, SkipOcrDiff: true, SkipTranslationCache: true, SkipTranslation: false))
            .ConfigureAwait(true);
    }

    private async void OnOcrOnlyHotkeyPressed(object? sender, EventArgs e)
    {
        AppendLog("OCR-only run (translation skipped).");
        await RunOnceAsync(new ForceRunOptions(SkipPhash: false, SkipOcrDiff: false, SkipTranslationCache: false, SkipTranslation: true))
            .ConfigureAwait(true);
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
        await RunOnceAsync(ForceRunOptions.None).ConfigureAwait(true);
    }

    private async Task RunOnceAsync(ForceRunOptions options)
    {
        if (_pipeline == null)
        {
            return;
        }

        _hasRunOnce = true;
        EnableOverlay();
        _runCts?.Cancel();
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        await _pipeline.RunOnceAsync(_runCts.Token, options).ConfigureAwait(true);
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
        ApplyLanguageSelection(SourceLangCombo, SourceLangCustom, settings.SourceLanguage);
        ApplyLanguageSelection(TargetLangCombo, TargetLangCustom, settings.TargetLanguage);
        EnableRoiCheck.IsChecked = settings.EnableRoi;
        SetComboBoxByTag(OcrEngineBox, settings.OcrEngine switch
        {
            OcrEngineKind.Paddle => "Paddle",
            OcrEngineKind.PaddleVllm => "PaddleVllm",
            OcrEngineKind.Florence2 => "Florence2",
            _ => "WinRt"
        });
        PaddleProjectDirBox.Text = settings.PaddleProjectDir;
        PaddleUvPathBox.Text = settings.PaddleUvPath;
        PaddleLanguageBox.Text = settings.PaddleLanguage;
        PaddleDeviceBox.Text = settings.PaddleDevice;
        PaddleModelDirBox.Text = settings.PaddleModelDir ?? string.Empty;
        FlorenceProjectDirBox.Text = settings.FlorenceProjectDir;
        FlorenceUvPathBox.Text = settings.FlorenceUvPath;
        FlorenceModelNameBox.Text = settings.FlorenceModelName;
        FlorenceDeviceBox.Text = settings.FlorenceDevice;
        FlorenceModelDirBox.Text = settings.FlorenceModelDir ?? string.Empty;
        VllmBaseUrlBox.Text = settings.VllmBaseUrl;
        VllmModelNameBox.Text = settings.VllmModelName;
        VllmApiKeyBox.Password = settings.VllmApiKey ?? string.Empty;
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
        EnableOcrBinarizationCheck.IsChecked = settings.EnableOcrBinarization;
        EnableOcrAutoThresholdCheck.IsChecked = settings.EnableOcrAutoThreshold;
        EnableOcrAutoInvertCheck.IsChecked = settings.EnableOcrAutoInvert;
        EnableOcrTwoPassCheck.IsChecked = settings.EnableOcrTwoPass;
        OcrTwoPassPreferAutoCheck.IsChecked = settings.OcrTwoPassPreferAuto;
        OcrBinarizationThresholdSlider.Value = settings.OcrBinarizationThreshold;
        OcrTwoPassLowThresholdSlider.Value = settings.OcrTwoPassLowThreshold;
        OcrTwoPassHighThresholdSlider.Value = settings.OcrTwoPassHighThreshold;
        UpdateOcrBinarizationThresholdValue();
        UpdateOcrTwoPassThresholdValues();
        UpdateOcrPreprocessControls(settings);
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
        var geminiStatus = settings.EnableGemini
            ? (string.IsNullOrWhiteSpace(settings.ApiKey) ? "Gemini: key missing" : "Gemini: enabled")
            : "Gemini: disabled";
        var deepLStatus = settings.EnableDeepL
            ? (string.IsNullOrWhiteSpace(settings.DeepLApiKey) ? "DeepL: key missing" : "DeepL: enabled")
            : "DeepL: disabled";
        TranslationStatusText.Text = $"Translation status: {geminiStatus} | {deepLStatus}";
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

    private OcrEngineKind GetOcrEngineKind()
    {
        if (OcrEngineBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (tag == "PaddleVllm")
            {
                return OcrEngineKind.PaddleVllm;
            }

            if (tag == "Florence2")
            {
                return OcrEngineKind.Florence2;
            }

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

    private async Task SaveSettingsAsync()
    {
        if (_isApplyingSettings)
        {
            return;
        }

        var settings = _settingsService.Settings;
        settings.CaptureMode = GetCaptureMode();
        settings.SourceLanguage = GetSelectedLanguage(SourceLangCombo, SourceLangCustom);
        settings.TargetLanguage = GetSelectedLanguage(TargetLangCombo, TargetLangCustom);
        settings.EnableRoi = EnableRoiCheck.IsChecked == true;
        settings.OcrEngine = GetOcrEngineKind();
        settings.PaddleProjectDir = PaddleProjectDirBox.Text.Trim();
        settings.PaddleUvPath = PaddleUvPathBox.Text.Trim();
        settings.PaddleLanguage = PaddleLanguageBox.Text.Trim();
        settings.PaddleDevice = PaddleDeviceBox.Text.Trim();
        settings.PaddleModelDir = string.IsNullOrWhiteSpace(PaddleModelDirBox.Text) ? null : PaddleModelDirBox.Text.Trim();
        settings.FlorenceProjectDir = FlorenceProjectDirBox.Text.Trim();
        settings.FlorenceUvPath = FlorenceUvPathBox.Text.Trim();
        settings.FlorenceModelName = FlorenceModelNameBox.Text.Trim();
        settings.FlorenceDevice = FlorenceDeviceBox.Text.Trim();
        settings.FlorenceModelDir = string.IsNullOrWhiteSpace(FlorenceModelDirBox.Text) ? null : FlorenceModelDirBox.Text.Trim();
        settings.VllmBaseUrl = VllmBaseUrlBox.Text.Trim();
        settings.VllmModelName = VllmModelNameBox.Text.Trim();
        settings.VllmApiKey = VllmApiKeyBox.Password;
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
        settings.EnableOcrTwoPass = EnableOcrTwoPassCheck.IsChecked == true;
        settings.OcrTwoPassPreferAuto = OcrTwoPassPreferAutoCheck.IsChecked == true;
        settings.OcrTwoPassLowThreshold = (int)Math.Round(OcrTwoPassLowThresholdSlider.Value);
        settings.OcrTwoPassHighThreshold = (int)Math.Round(OcrTwoPassHighThresholdSlider.Value);

        if (int.TryParse(PhashThresholdBox.Text.Trim(), out var phashThreshold))
        {
            settings.PhashThreshold = phashThreshold;
        }

        if (double.TryParse(IouThresholdBox.Text.Trim(), out var iouThreshold))
        {
            settings.OcrIouThreshold = iouThreshold;
        }

        _overlayWindow?.ApplyStyle(settings);
        UpdateOcrBinarizationThresholdValue();
        UpdateOcrTwoPassThresholdValues();
        UpdateOcrPreprocessControls(settings);
        UpdateRoiStatus(settings);
        UpdateTranslationStatus(settings);
        await _settingsService.SaveAsync().ConfigureAwait(true);
        AppendLog("Settings saved.");
        TryUpdateHotkeys(settings);
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

    private void UpdateOcrPreprocessControls(AppSettings settings)
    {
        if (OcrBinarizationThresholdSlider == null || OcrBinarizationThresholdValue == null ||
            EnableOcrAutoThresholdCheck == null || EnableOcrAutoInvertCheck == null ||
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
            SettingsPanelFlorence == null || SettingsPanelVllm == null || SettingsPanelTranslation == null ||
            SettingsPanelHotkeys == null)
        {
            return;
        }

        var index = SettingsCategoryList.SelectedIndex;
        SettingsPanelOcr.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelPaddle.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelFlorence.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelVllm.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelTranslation.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanelHotkeys.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

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
