using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Application;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsChangeScheduler _changeScheduler;
    private static readonly HashSet<string> BuiltInLanguageTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "ja",
        "zh-Hant",
        "zh-Hans",
        "ru"
    };
    private bool _suspendAutoSave;

    public SettingsViewModel(ISettingsChangeScheduler changeScheduler)
    {
        _changeScheduler = changeScheduler;
    }

    [ObservableProperty] private double _paddleConfidenceThreshold;
    [ObservableProperty] private double _ocrBinarizationThreshold;
    [ObservableProperty] private double _ocrGamma;
    [ObservableProperty] private double _ocrDownsampleScale;
    [ObservableProperty] private double _ocrTwoPassLowThreshold;
    [ObservableProperty] private double _ocrTwoPassHighThreshold;
    [ObservableProperty] private double _overlayFontSize;
    [ObservableProperty] private double _overlayBackgroundOpacity;
    [ObservableProperty] private double _smallTextThresholdPx;
    [ObservableProperty] private double _sceneChangeThreshold;
    [ObservableProperty] private double _sceneChangeWatchIntervalMs;
    [ObservableProperty] private double _sceneChangeWatchPhashThreshold;
    [ObservableProperty] private string _sourceLanguageTag = "en";
    [ObservableProperty] private string _targetLanguageTag = "ja";
    [ObservableProperty] private string _sourceLanguageCustom = string.Empty;
    [ObservableProperty] private string _targetLanguageCustom = string.Empty;

    public void LoadFrom(AppSettings settings)
    {
        _suspendAutoSave = true;
        try
        {
            PaddleConfidenceThreshold = settings.PaddleConfidenceThreshold;
            OcrBinarizationThreshold = settings.OcrBinarizationThreshold;
            OcrGamma = settings.OcrGamma;
            OcrDownsampleScale = settings.OcrDownsampleScale;
            OcrTwoPassLowThreshold = settings.OcrTwoPassLowThreshold;
            OcrTwoPassHighThreshold = settings.OcrTwoPassHighThreshold;
            OverlayFontSize = settings.OverlayFontSize;
            OverlayBackgroundOpacity = settings.OverlayBackgroundOpacity;
            SmallTextThresholdPx = settings.SmallTextThresholdPx;
            SceneChangeThreshold = settings.SceneChangeThreshold;
            SceneChangeWatchIntervalMs = settings.SceneChangeWatchIntervalMs;
            SceneChangeWatchPhashThreshold = settings.SceneChangeWatchPhashThreshold;
            AssignLanguageSettings(settings);
        }
        finally
        {
            _suspendAutoSave = false;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.PaddleConfidenceThreshold = Math.Round(PaddleConfidenceThreshold, 2);
        settings.OcrBinarizationThreshold = (int)Math.Round(OcrBinarizationThreshold);
        settings.OcrGamma = Math.Round(OcrGamma, 2);
        settings.OcrDownsampleScale = Math.Round(OcrDownsampleScale, 2);
        settings.OcrTwoPassLowThreshold = (int)Math.Round(OcrTwoPassLowThreshold);
        settings.OcrTwoPassHighThreshold = (int)Math.Round(OcrTwoPassHighThreshold);
        settings.OverlayFontSize = Math.Round(OverlayFontSize, 1);
        settings.OverlayBackgroundOpacity = Math.Round(OverlayBackgroundOpacity, 2);
        settings.SmallTextThresholdPx = Math.Round(SmallTextThresholdPx, 1);
        settings.SceneChangeThreshold = SceneChangeThreshold;
        settings.SceneChangeWatchIntervalMs = (int)Math.Round(SceneChangeWatchIntervalMs);
        settings.SceneChangeWatchPhashThreshold = (int)Math.Round(SceneChangeWatchPhashThreshold);
        settings.SourceLanguage = ResolveLanguageTag(SourceLanguageTag, SourceLanguageCustom);
        settings.TargetLanguage = ResolveLanguageTag(TargetLanguageTag, TargetLanguageCustom);
    }

    public void RequestSave()
    {
        _changeScheduler.RequestSave();
    }

    public Task FlushPendingSaveAsync()
    {
        return _changeScheduler.FlushAsync();
    }

    public void CancelPendingSave()
    {
        _changeScheduler.CancelPending();
    }

    partial void OnPaddleConfidenceThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrBinarizationThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrGammaChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrDownsampleScaleChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrTwoPassLowThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrTwoPassHighThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOverlayFontSizeChanged(double value) => RequestSaveOnValueChange();
    partial void OnOverlayBackgroundOpacityChanged(double value) => RequestSaveOnValueChange();
    partial void OnSmallTextThresholdPxChanged(double value) => RequestSaveOnValueChange();
    partial void OnSceneChangeThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnSceneChangeWatchIntervalMsChanged(double value) => RequestSaveOnValueChange();
    partial void OnSceneChangeWatchPhashThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnSourceLanguageTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnTargetLanguageTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnSourceLanguageCustomChanged(string value) => RequestSaveOnValueChange();
    partial void OnTargetLanguageCustomChanged(string value) => RequestSaveOnValueChange();

    private void RequestSaveOnValueChange()
    {
        if (_suspendAutoSave)
        {
            return;
        }

        _changeScheduler.RequestSave();
    }

    private void AssignLanguageSettings(AppSettings settings)
    {
        SourceLanguageTag = ResolveLanguageSelectionTag(settings.SourceLanguage, out var sourceCustom);
        SourceLanguageCustom = sourceCustom;
        TargetLanguageTag = ResolveLanguageSelectionTag(settings.TargetLanguage, out var targetCustom);
        TargetLanguageCustom = targetCustom;
    }

    private static string ResolveLanguageSelectionTag(string value, out string custom)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalized) && BuiltInLanguageTags.Contains(normalized))
        {
            custom = string.Empty;
            return normalized;
        }

        custom = normalized;
        return "custom";
    }

    private static string ResolveLanguageTag(string selectedTag, string customText)
    {
        if (string.Equals(selectedTag, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return (customText ?? string.Empty).Trim();
        }

        return (selectedTag ?? string.Empty).Trim();
    }
}
