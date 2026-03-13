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
    private bool _suspendSceneModeSync;
    private bool _suspendMirrorModeSync;
    private bool _suspendAutoSave;
    private bool _suspendNumericFieldSync;

    public SettingsViewModel(ISettingsChangeScheduler changeScheduler)
    {
        _changeScheduler = changeScheduler;
    }

    [ObservableProperty] private double _paddleConfidenceThreshold;
    [ObservableProperty] private double _ocrBinarizationThreshold;
    [ObservableProperty] private double _ocrGamma;
    [ObservableProperty] private double _ocrContrast;
    [ObservableProperty] private double _ocrDownsampleScale;
    [ObservableProperty] private double _ocrTwoPassLowThreshold;
    [ObservableProperty] private double _ocrTwoPassHighThreshold;
    [ObservableProperty] private double _overlayFontSize;
    [ObservableProperty] private double _overlayBackgroundOpacity;
    [ObservableProperty] private double _smallTextThresholdPx;
    [ObservableProperty] private double _sceneChangeThreshold;
    [ObservableProperty] private double _sceneChangeWatchIntervalMs;
    [ObservableProperty] private double _sceneChangeWatchPhashThreshold;
    [ObservableProperty] private double? _phashThresholdValue;
    [ObservableProperty] private double? _iouThresholdValue;
    [ObservableProperty] private double? _sceneChangeQuietWindowMsValue;
    [ObservableProperty] private double? _paddleTextDetThreshValue;
    [ObservableProperty] private double? _paddleTextDetBoxThreshValue;
    [ObservableProperty] private double? _paddleTextDetUnclipRatioValue;
    [ObservableProperty] private double? _paddleTextRecScoreThreshValue;
    [ObservableProperty] private double? _paddleVlMaxPixelsValue;
    [ObservableProperty] private double? _paddleVlLayoutThresholdValue;
    [ObservableProperty] private double? _paddleVlMaxNewTokensValue;
    [ObservableProperty] private string _sourceLanguageTag = "en";
    [ObservableProperty] private string _targetLanguageTag = "ja";
    [ObservableProperty] private string _sourceLanguageCustom = string.Empty;
    [ObservableProperty] private string _targetLanguageCustom = string.Empty;
    [ObservableProperty] private string _captureModeTag = "ActiveWindow";
    [ObservableProperty] private string _captureProviderTag = "Gdi";
    [ObservableProperty] private bool _isCaptureProviderFixed;
    [ObservableProperty] private bool _enableRoi;
    [ObservableProperty] private string _ocrEngineTag = "WinRt";
    [ObservableProperty] private string _paddleDetectionModelName = "PP-OCRv5_mobile_det";
    [ObservableProperty] private string _paddleRecognitionModelName = "PP-OCRv5_server_rec";
    [ObservableProperty] private string _paddleVlPipelineVersion = "v1.5";
    [ObservableProperty] private string _paddleVlUseLayoutDetectionModeTag = "auto";
    [ObservableProperty] private string _paddleVlPrecisionTag = "fp16";
    [ObservableProperty] private bool _enablePaddleConfidenceFilter;
    [ObservableProperty] private bool _enableLlamaCppTranslation;
    [ObservableProperty] private string _llamaSelectedModelFileName = string.Empty;
    [ObservableProperty] private string _visionLlmSelectedModelFileName = string.Empty;
    [ObservableProperty] private string _visionLlmSelectedMmprojFileName = string.Empty;
    [ObservableProperty] private bool _enableVisionLlmSharedLocalTranslation;
    [ObservableProperty] private bool _enableVisionGeometryHybridOcr;
    [ObservableProperty] private string _visionGeometryHybridBaseEngineTag = "WinRt";
    [ObservableProperty] private bool _enableDeepL;
    [ObservableProperty] private bool _enableGemini;
    [ObservableProperty] private string _verticalModeOverrideTag = "Auto";
    [ObservableProperty] private bool _enableSimpleMergeTuning;
    [ObservableProperty] private double _horizontalMergeStrength = 50;
    [ObservableProperty] private double _verticalMergeStrength = 50;
    [ObservableProperty] private bool _enableLogging;
    [ObservableProperty] private bool _enableOcrPerfLog;
    [ObservableProperty] private bool _enableOcrBinarization;
    [ObservableProperty] private bool _enableOcrAutoThreshold;
    [ObservableProperty] private bool _enableOcrGamma;
    [ObservableProperty] private bool _enableOcrGrayscale;
    [ObservableProperty] private bool _enableOcrContrast;
    [ObservableProperty] private bool _enableOcrDownsampling;
    [ObservableProperty] private bool _enableOcrTwoPass;
    [ObservableProperty] private bool _ocrTwoPassPreferAuto;
    [ObservableProperty] private bool _enableFixedRoiOverlay;
    [ObservableProperty] private bool _enableOverlayFontStabilization;
    [ObservableProperty] private bool _enableSmallBoxReadabilityBoost;
    [ObservableProperty] private bool _enableSceneChangeAutoHide;
    [ObservableProperty] private bool _enableSceneChangeAutoTranslate;
    [ObservableProperty] private bool _showAutoTranslateBadgeIcon;
    [ObservableProperty] private bool _enableGraphicsHookPipeline;
    [ObservableProperty] private string _graphicsHookApiTag = "Dx11";
    [ObservableProperty] private bool _enableMirrorFullscreenMode;
    [ObservableProperty] private bool _graphicsHookOverlayEnabled;
    [ObservableProperty] private bool _graphicsHookFallbackOnError;
    [ObservableProperty] private bool _enableGraphicsHookPerfDiagLog;
    [ObservableProperty] private bool _enableGraphicsHookDiagFileSink;
    [ObservableProperty] private bool _enableGraphicsHookLauncher;
    [ObservableProperty] private string _resourceBudgetProfileTag = "Balanced";
    [ObservableProperty] private bool _enableSceneChangeTextWeighted;
    [ObservableProperty] private bool _enableSceneChangeQuietWindow;
    [ObservableProperty] private string _phashThresholdText = string.Empty;
    [ObservableProperty] private string _iouThresholdText = string.Empty;
    [ObservableProperty] private string _ocrPerfLogThresholdText = string.Empty;
    [ObservableProperty] private string _sceneChangeQuietWindowMsText = string.Empty;
    [ObservableProperty] private string _graphicsHookCaptureFpsLimitText = string.Empty;
    [ObservableProperty] private string _graphicsHookLauncherExePath = string.Empty;
    [ObservableProperty] private string _graphicsHookLauncherArgs = string.Empty;
    [ObservableProperty] private string _magpieProfileIndexText = string.Empty;
    [ObservableProperty] private string _paddleTextDetThreshText = string.Empty;
    [ObservableProperty] private string _paddleTextDetBoxThreshText = string.Empty;
    [ObservableProperty] private string _paddleTextDetUnclipRatioText = string.Empty;
    [ObservableProperty] private string _paddleTextRecScoreThreshText = string.Empty;
    [ObservableProperty] private string _paddleVlMaxPixelsText = string.Empty;
    [ObservableProperty] private string _paddleVlLayoutThresholdText = string.Empty;
    [ObservableProperty] private string _paddleVlMaxNewTokensText = string.Empty;
    [ObservableProperty] private string _llamaHostText = string.Empty;
    [ObservableProperty] private string _llamaPortText = string.Empty;
    [ObservableProperty] private string _llamaContextSizeText = string.Empty;
    [ObservableProperty] private string _llamaGpuLayersText = string.Empty;
    [ObservableProperty] private string _llamaThreadsText = string.Empty;
    [ObservableProperty] private string _llamaParallelText = string.Empty;
    [ObservableProperty] private string _llamaBatchSizeText = string.Empty;
    [ObservableProperty] private string _llamaMaxTokensText = string.Empty;
    [ObservableProperty] private string _llamaTemperatureText = string.Empty;
    [ObservableProperty] private string _llamaTopPText = string.Empty;
    [ObservableProperty] private string _llamaTopKText = string.Empty;
    [ObservableProperty] private string _llamaRepeatPenaltyText = string.Empty;
    [ObservableProperty] private string _visionLlmHostText = string.Empty;
    [ObservableProperty] private string _visionLlmPortText = string.Empty;
    [ObservableProperty] private string _visionLlmContextSizeText = string.Empty;
    [ObservableProperty] private string _visionLlmGpuLayersText = string.Empty;
    [ObservableProperty] private string _visionLlmThreadsText = string.Empty;
    [ObservableProperty] private string _visionLlmParallelText = string.Empty;
    [ObservableProperty] private string _visionLlmBatchSizeText = string.Empty;
    [ObservableProperty] private string _visionLlmMaxTokensText = string.Empty;
    [ObservableProperty] private string _visionLlmMaxImageSideText = string.Empty;
    [ObservableProperty] private string _deepLEndpointText = string.Empty;
    [ObservableProperty] private string _deepLApiKeyText = string.Empty;
    [ObservableProperty] private string _apiKeyText = string.Empty;
    [ObservableProperty] private string _hotkeyRunOnceKey = "F8";
    [ObservableProperty] private bool _hotkeyRunOnceCtrl;
    [ObservableProperty] private bool _hotkeyRunOnceAlt;
    [ObservableProperty] private bool _hotkeyRunOnceShift;
    [ObservableProperty] private string _hotkeyRunNextRoiKey = "F8";
    [ObservableProperty] private bool _hotkeyRunNextRoiCtrl;
    [ObservableProperty] private bool _hotkeyRunNextRoiAlt;
    [ObservableProperty] private bool _hotkeyRunNextRoiShift = true;
    [ObservableProperty] private string _hotkeyRunNextNextRoiKey = "F8";
    [ObservableProperty] private bool _hotkeyRunNextNextRoiCtrl = true;
    [ObservableProperty] private bool _hotkeyRunNextNextRoiAlt;
    [ObservableProperty] private bool _hotkeyRunNextNextRoiShift;
    [ObservableProperty] private string _hotkeyToggleOverlayKey = "F9";
    [ObservableProperty] private bool _hotkeyToggleOverlayCtrl;
    [ObservableProperty] private bool _hotkeyToggleOverlayAlt;
    [ObservableProperty] private bool _hotkeyToggleOverlayShift;
    [ObservableProperty] private string _hotkeyForceRunKey = "F10";
    [ObservableProperty] private bool _hotkeyForceRunCtrl;
    [ObservableProperty] private bool _hotkeyForceRunAlt;
    [ObservableProperty] private bool _hotkeyForceRunShift;
    [ObservableProperty] private string _hotkeyForceRunNextRoiKey = "F10";
    [ObservableProperty] private bool _hotkeyForceRunNextRoiCtrl;
    [ObservableProperty] private bool _hotkeyForceRunNextRoiAlt;
    [ObservableProperty] private bool _hotkeyForceRunNextRoiShift = true;
    [ObservableProperty] private string _hotkeyForceRunNextNextRoiKey = "F10";
    [ObservableProperty] private bool _hotkeyForceRunNextNextRoiCtrl = true;
    [ObservableProperty] private bool _hotkeyForceRunNextNextRoiAlt;
    [ObservableProperty] private bool _hotkeyForceRunNextNextRoiShift;
    [ObservableProperty] private string _hotkeyForceGeminiStrictKey = "F10";
    [ObservableProperty] private bool _hotkeyForceGeminiStrictCtrl;
    [ObservableProperty] private bool _hotkeyForceGeminiStrictAlt = true;
    [ObservableProperty] private bool _hotkeyForceGeminiStrictShift;
    [ObservableProperty] private string _hotkeyOcrOnlyKey = "F11";
    [ObservableProperty] private bool _hotkeyOcrOnlyCtrl;
    [ObservableProperty] private bool _hotkeyOcrOnlyAlt;
    [ObservableProperty] private bool _hotkeyOcrOnlyShift;
    [ObservableProperty] private string _hotkeyToggleSceneAutoTranslateKey = "F5";
    [ObservableProperty] private bool _hotkeyToggleSceneAutoTranslateCtrl;
    [ObservableProperty] private bool _hotkeyToggleSceneAutoTranslateAlt;
    [ObservableProperty] private bool _hotkeyToggleSceneAutoTranslateShift;
    [ObservableProperty] private string _hotkeySelectRoiKey = "F6";
    [ObservableProperty] private bool _hotkeySelectRoiCtrl;
    [ObservableProperty] private bool _hotkeySelectRoiAlt;
    [ObservableProperty] private bool _hotkeySelectRoiShift;
    [ObservableProperty] private string _hotkeyLockCaptureWindowKey = "F7";
    [ObservableProperty] private bool _hotkeyLockCaptureWindowCtrl;
    [ObservableProperty] private bool _hotkeyLockCaptureWindowAlt;
    [ObservableProperty] private bool _hotkeyLockCaptureWindowShift;
    [ObservableProperty] private string _hotkeyUnlockCaptureWindowKey = "F7";
    [ObservableProperty] private bool _hotkeyUnlockCaptureWindowCtrl;
    [ObservableProperty] private bool _hotkeyUnlockCaptureWindowAlt;
    [ObservableProperty] private bool _hotkeyUnlockCaptureWindowShift = true;
    [ObservableProperty] private string _hotkeyToggleMirrorFullscreenKey = "F7";
    [ObservableProperty] private bool _hotkeyToggleMirrorFullscreenCtrl = true;
    [ObservableProperty] private bool _hotkeyToggleMirrorFullscreenAlt;
    [ObservableProperty] private bool _hotkeyToggleMirrorFullscreenShift;
    [ObservableProperty] private bool _enableRawInputHotkeys;

    public void LoadFrom(AppSettings settings)
    {
        _suspendAutoSave = true;
        try
        {
            CaptureModeTag = settings.CaptureMode == CaptureMode.Screen ? "Screen" : "ActiveWindow";
            CaptureProviderTag = settings.PreferredCaptureProvider.ToString();
            IsCaptureProviderFixed = settings.CaptureProviderMode == CaptureProviderMode.Fixed;
            EnableRoi = settings.EnableRoi;
            OcrEngineTag = settings.OcrEngine switch
            {
                OcrEngineKind.Paddle => "Paddle",
                OcrEngineKind.PaddleVllm => "PaddleVllm",
                OcrEngineKind.Ndl => "Ndl",
                OcrEngineKind.VisionLlm => "VisionLlm",
                _ => "WinRt"
            };
            PaddleDetectionModelName = settings.PaddleTextDetectionModelName;
            PaddleRecognitionModelName = settings.PaddleTextRecognitionModelName;
            PaddleVlPipelineVersion = settings.PaddleVlPipelineVersion;
            PaddleVlUseLayoutDetectionModeTag = ToPaddleVlLayoutDetectionModeTag(settings.PaddleVlUseLayoutDetection);
            PaddleVlPrecisionTag = NormalizePaddleVlPrecisionTag(settings.PaddleVlPrecision);
            EnablePaddleConfidenceFilter = settings.EnablePaddleConfidenceFilter;
            EnableLlamaCppTranslation = settings.EnableLlamaCppTranslation;
            LlamaSelectedModelFileName = settings.LlamaSelectedModelFileName;
            VisionLlmSelectedModelFileName = settings.VisionLlmSelectedModelFileName;
            VisionLlmSelectedMmprojFileName = settings.VisionLlmSelectedMmprojFileName;
            EnableVisionLlmSharedLocalTranslation = settings.EnableVisionLlmSharedLocalTranslation;
            EnableVisionGeometryHybridOcr = settings.EnableVisionGeometryHybridOcr;
            VisionGeometryHybridBaseEngineTag = settings.VisionGeometryHybridBaseEngine switch
            {
                VisionGeometryHybridBaseEngineKind.Ndl => "Ndl",
                VisionGeometryHybridBaseEngineKind.Paddle => "Paddle",
                _ => "WinRt"
            };
            EnableDeepL = settings.EnableDeepL;
            EnableGemini = settings.EnableGemini;
            VerticalModeOverrideTag = settings.VerticalModeOverride switch
            {
                VerticalModeOverride.Vertical => "Vertical",
                VerticalModeOverride.Horizontal => "Horizontal",
                _ => "Auto"
            };
            EnableSimpleMergeTuning = settings.EnableSimpleMergeTuning;
            HorizontalMergeStrength = settings.HorizontalMergeStrength;
            VerticalMergeStrength = settings.VerticalMergeStrength;
            EnableLogging = settings.EnableLogging;
            EnableOcrPerfLog = settings.EnableOcrPerfLog;
            EnableOcrBinarization = settings.EnableOcrBinarization;
            EnableOcrAutoThreshold = settings.EnableOcrAutoThreshold;
            EnableOcrGamma = settings.EnableOcrGamma;
            EnableOcrGrayscale = settings.EnableOcrGrayscale;
            EnableOcrContrast = settings.EnableOcrContrast;
            EnableOcrDownsampling = settings.EnableOcrDownsampling;
            EnableOcrTwoPass = settings.EnableOcrTwoPass;
            OcrTwoPassPreferAuto = settings.OcrTwoPassPreferAuto;
            EnableFixedRoiOverlay = settings.EnableFixedRoiOverlay;
            EnableOverlayFontStabilization = settings.EnableOverlayFontStabilization;
            EnableSmallBoxReadabilityBoost = settings.EnableSmallBoxReadabilityBoost;
            EnableSceneChangeAutoHide = settings.EnableSceneChangeAutoHide;
            EnableSceneChangeAutoTranslate = settings.EnableSceneChangeAutoTranslate;
            ShowAutoTranslateBadgeIcon = settings.ShowAutoTranslateBadgeIcon;
            EnableGraphicsHookPipeline = settings.EnableGraphicsHookPipeline;
            GraphicsHookApiTag = settings.GraphicsHookApi switch
            {
                GraphicsHookApiKind.Dx9 => "Dx9",
                GraphicsHookApiKind.Vulkan => "Vulkan",
                _ => "Dx11"
            };
            EnableMirrorFullscreenMode = settings.EnableMirrorFullscreenMode;
            GraphicsHookOverlayEnabled = settings.GraphicsHookOverlayEnabled;
            GraphicsHookFallbackOnError = settings.GraphicsHookFallbackOnError;
            EnableGraphicsHookPerfDiagLog = settings.EnableGraphicsHookPerfDiagLog;
            EnableGraphicsHookDiagFileSink = settings.EnableGraphicsHookDiagFileSink;
            EnableGraphicsHookLauncher = settings.EnableGraphicsHookLauncher;
            ResourceBudgetProfileTag = settings.ResourceBudgetProfile switch
            {
                GraphicsResourceBudgetProfile.LowVram => "LowVram",
                GraphicsResourceBudgetProfile.HighVram => "HighVram",
                GraphicsResourceBudgetProfile.UltraVram => "UltraVram",
                _ => "Balanced"
            };
            EnableSceneChangeTextWeighted = settings.EnableSceneChangeTextWeighted;
            EnableSceneChangeQuietWindow = settings.EnableSceneChangeQuietWindow;
            PhashThresholdText = settings.PhashThreshold.ToString();
            IouThresholdText = settings.OcrIouThreshold.ToString("0.00");
            OcrPerfLogThresholdText = settings.OcrPerfLogThresholdMs.ToString();
            SceneChangeQuietWindowMsText = settings.SceneChangeQuietWindowMs.ToString();
            GraphicsHookCaptureFpsLimitText = settings.GraphicsHookCaptureFpsLimit.ToString();
            GraphicsHookLauncherExePath = settings.GraphicsHookLauncherExePath;
            GraphicsHookLauncherArgs = settings.GraphicsHookLauncherArgs;
            MagpieProfileIndexText = settings.MagpieProfileIndex.ToString();
            PaddleTextDetThreshText = settings.PaddleTextDetThresh.ToString("0.###");
            PaddleTextDetBoxThreshText = settings.PaddleTextDetBoxThresh.ToString("0.###");
            PaddleTextDetUnclipRatioText = settings.PaddleTextDetUnclipRatio.ToString("0.###");
            PaddleTextRecScoreThreshText = settings.PaddleTextRecScoreThresh.ToString("0.###");
            PaddleVlMaxPixelsText = settings.PaddleVlMaxPixels?.ToString() ?? string.Empty;
            PaddleVlLayoutThresholdText = settings.PaddleVlLayoutThreshold?.ToString("0.###") ?? string.Empty;
            PaddleVlMaxNewTokensText = settings.PaddleVlMaxNewTokens?.ToString() ?? string.Empty;
            PhashThresholdValue = settings.PhashThreshold;
            IouThresholdValue = settings.OcrIouThreshold;
            SceneChangeQuietWindowMsValue = settings.SceneChangeQuietWindowMs;
            PaddleTextDetThreshValue = settings.PaddleTextDetThresh;
            PaddleTextDetBoxThreshValue = settings.PaddleTextDetBoxThresh;
            PaddleTextDetUnclipRatioValue = settings.PaddleTextDetUnclipRatio;
            PaddleTextRecScoreThreshValue = settings.PaddleTextRecScoreThresh;
            PaddleVlMaxPixelsValue = settings.PaddleVlMaxPixels;
            PaddleVlLayoutThresholdValue = settings.PaddleVlLayoutThreshold;
            PaddleVlMaxNewTokensValue = settings.PaddleVlMaxNewTokens;
            LlamaHostText = settings.LlamaHost;
            LlamaPortText = settings.LlamaPort.ToString();
            LlamaContextSizeText = settings.LlamaContextSize.ToString();
            LlamaGpuLayersText = settings.LlamaGpuLayers.ToString();
            LlamaThreadsText = settings.LlamaThreads.ToString();
            LlamaParallelText = settings.LlamaParallel.ToString();
            LlamaBatchSizeText = settings.LlamaBatchSize.ToString();
            LlamaMaxTokensText = settings.LlamaMaxTokens.ToString();
            LlamaTemperatureText = settings.LlamaTemperature.ToString("0.###");
            LlamaTopPText = settings.LlamaTopP.ToString("0.###");
            LlamaTopKText = settings.LlamaTopK.ToString();
            LlamaRepeatPenaltyText = settings.LlamaRepeatPenalty.ToString("0.###");
            VisionLlmHostText = settings.VisionLlmHost;
            VisionLlmPortText = settings.VisionLlmPort.ToString();
            VisionLlmContextSizeText = settings.VisionLlmContextSize.ToString();
            VisionLlmGpuLayersText = settings.VisionLlmGpuLayers.ToString();
            VisionLlmThreadsText = settings.VisionLlmThreads.ToString();
            VisionLlmParallelText = settings.VisionLlmParallel.ToString();
            VisionLlmBatchSizeText = settings.VisionLlmBatchSize.ToString();
            VisionLlmMaxTokensText = settings.VisionLlmMaxTokens.ToString();
            VisionLlmMaxImageSideText = settings.VisionLlmMaxImageSide.ToString();
            DeepLEndpointText = settings.DeepLEndpoint;
            DeepLApiKeyText = settings.DeepLApiKey ?? string.Empty;
            ApiKeyText = settings.ApiKey ?? string.Empty;
            PaddleConfidenceThreshold = settings.PaddleConfidenceThreshold;
            OcrBinarizationThreshold = settings.OcrBinarizationThreshold;
            OcrGamma = settings.OcrGamma;
            OcrContrast = double.IsFinite(settings.OcrContrast) ? Math.Clamp(settings.OcrContrast, 0.5, 2.0) : 1.0;
            OcrDownsampleScale = settings.OcrDownsampleScale;
            OcrTwoPassLowThreshold = settings.OcrTwoPassLowThreshold;
            OcrTwoPassHighThreshold = settings.OcrTwoPassHighThreshold;
            OverlayFontSize = settings.OverlayFontSize;
            OverlayBackgroundOpacity = settings.OverlayBackgroundOpacity;
            SmallTextThresholdPx = settings.SmallTextThresholdPx;
            SceneChangeThreshold = settings.SceneChangeThreshold;
            SceneChangeWatchIntervalMs = settings.SceneChangeWatchIntervalMs;
            SceneChangeWatchPhashThreshold = settings.SceneChangeWatchPhashThreshold;
            AssignHotkeySettings(settings);
            EnableRawInputHotkeys = settings.EnableRawInputHotkeys;
            AssignLanguageSettings(settings);
        }
        finally
        {
            _suspendAutoSave = false;
        }
    }

    public void ApplyTo(AppSettings settings)
    {
        settings.CaptureMode = string.Equals(CaptureModeTag, "Screen", StringComparison.OrdinalIgnoreCase)
            ? CaptureMode.Screen
            : CaptureMode.ActiveWindow;
        settings.CaptureProviderMode = IsCaptureProviderFixed
            ? CaptureProviderMode.Fixed
            : CaptureProviderMode.Auto;
        settings.PreferredCaptureProvider = CaptureProviderTag switch
        {
            "Hook" => CaptureProviderKind.GraphicsHook,
            "GraphicsHook" => CaptureProviderKind.GraphicsHook,
            "Dxgi" => CaptureProviderKind.Dxgi,
            "Gdi" => CaptureProviderKind.Gdi,
            _ => CaptureProviderKind.Wgc
        };
        settings.EnableRoi = EnableRoi;
        settings.OcrEngine = OcrEngineTag switch
        {
            "Paddle" => OcrEngineKind.Paddle,
            "PaddleVllm" => OcrEngineKind.PaddleVllm,
            "Ndl" => OcrEngineKind.Ndl,
            "VisionLlm" => OcrEngineKind.VisionLlm,
            _ => OcrEngineKind.WinRt
        };
        settings.PaddleTextDetectionModelName = PaddleDetectionModelName;
        settings.PaddleTextRecognitionModelName = PaddleRecognitionModelName;
        settings.PaddleVlPipelineVersion = PaddleVlPipelineVersion;
        settings.PaddleVlUseLayoutDetection = ParsePaddleVlLayoutDetectionModeTag(PaddleVlUseLayoutDetectionModeTag);
        settings.PaddleVlPrecision = NormalizePaddleVlPrecisionTag(PaddleVlPrecisionTag);
        settings.EnablePaddleConfidenceFilter = EnablePaddleConfidenceFilter;
        settings.EnableLlamaCppTranslation = EnableLlamaCppTranslation;
        settings.LlamaSelectedModelFileName = (LlamaSelectedModelFileName ?? string.Empty).Trim();
        settings.VisionLlmSelectedModelFileName = (VisionLlmSelectedModelFileName ?? string.Empty).Trim();
        settings.VisionLlmSelectedMmprojFileName = (VisionLlmSelectedMmprojFileName ?? string.Empty).Trim();
        settings.EnableVisionLlmSharedLocalTranslation = EnableVisionLlmSharedLocalTranslation;
        settings.EnableDeepL = EnableDeepL;
        settings.EnableGemini = EnableGemini;
        settings.VerticalModeOverride = VerticalModeOverrideTag switch
        {
            "Vertical" => VerticalModeOverride.Vertical,
            "Horizontal" => VerticalModeOverride.Horizontal,
            _ => VerticalModeOverride.Auto
        };
        settings.EnableSimpleMergeTuning = EnableSimpleMergeTuning;
        settings.HorizontalMergeStrength = (int)Math.Round(Math.Clamp(HorizontalMergeStrength, 0, 100));
        settings.VerticalMergeStrength = (int)Math.Round(Math.Clamp(VerticalMergeStrength, 0, 100));
        settings.EnableLogging = EnableLogging;
        settings.EnableOcrPerfLog = EnableOcrPerfLog;
        settings.EnableOcrBinarization = EnableOcrBinarization;
        settings.EnableOcrAutoThreshold = EnableOcrAutoThreshold;
        settings.EnableOcrGamma = EnableOcrGamma;
        settings.EnableOcrGrayscale = EnableOcrGrayscale;
        settings.EnableOcrContrast = EnableOcrContrast;
        settings.EnableOcrDownsampling = EnableOcrDownsampling;
        settings.EnableOcrTwoPass = EnableOcrTwoPass;
        settings.OcrTwoPassPreferAuto = OcrTwoPassPreferAuto;
        settings.EnableFixedRoiOverlay = EnableFixedRoiOverlay;
        settings.EnableOverlayFontStabilization = EnableOverlayFontStabilization;
        settings.EnableSmallBoxReadabilityBoost = EnableSmallBoxReadabilityBoost;
        settings.EnableSceneChangeAutoHide = EnableSceneChangeAutoHide;
        settings.EnableSceneChangeAutoTranslate = EnableSceneChangeAutoTranslate;
        settings.ShowAutoTranslateBadgeIcon = ShowAutoTranslateBadgeIcon;
        settings.EnableGraphicsHookPipeline = EnableGraphicsHookPipeline;
        settings.GraphicsHookApi = GraphicsHookApiTag switch
        {
            "Dx9" => GraphicsHookApiKind.Dx9,
            "Vulkan" => GraphicsHookApiKind.Vulkan,
            _ => GraphicsHookApiKind.Dx11
        };
        settings.EnableMirrorFullscreenMode = EnableMirrorFullscreenMode;
        settings.GraphicsHookOverlayEnabled = GraphicsHookOverlayEnabled;
        settings.GraphicsHookFallbackOnError = GraphicsHookFallbackOnError;
        settings.EnableGraphicsHookPerfDiagLog = EnableGraphicsHookPerfDiagLog;
        settings.EnableGraphicsHookDiagFileSink = EnableGraphicsHookDiagFileSink;
        settings.EnableGraphicsHookLauncher = EnableGraphicsHookLauncher;
        settings.ResourceBudgetProfile = ResourceBudgetProfileTag switch
        {
            "LowVram" => GraphicsResourceBudgetProfile.LowVram,
            "HighVram" => GraphicsResourceBudgetProfile.HighVram,
            "UltraVram" => GraphicsResourceBudgetProfile.UltraVram,
            _ => GraphicsResourceBudgetProfile.Balanced
        };
        settings.GraphicsHookLauncherExePath = (GraphicsHookLauncherExePath ?? string.Empty).Trim();
        settings.GraphicsHookLauncherArgs = GraphicsHookLauncherArgs ?? string.Empty;
        settings.EnableRawInputHotkeys = EnableRawInputHotkeys;
        settings.EnableSceneChangeTextWeighted = EnableSceneChangeTextWeighted;
        settings.EnableSceneChangeQuietWindow = EnableSceneChangeQuietWindow;
        if (string.IsNullOrWhiteSpace(SceneChangeQuietWindowMsText))
        {
            settings.SceneChangeQuietWindowMs = 450;
        }
        else if (int.TryParse(SceneChangeQuietWindowMsText.Trim(), out var quietWindowMs))
        {
            settings.SceneChangeQuietWindowMs = quietWindowMs;
        }

        if (string.IsNullOrWhiteSpace(GraphicsHookCaptureFpsLimitText))
        {
            settings.GraphicsHookCaptureFpsLimit = 15;
        }
        else if (int.TryParse(GraphicsHookCaptureFpsLimitText.Trim(), out var hookCaptureFpsLimit))
        {
            settings.GraphicsHookCaptureFpsLimit = hookCaptureFpsLimit;
        }

        if (string.IsNullOrWhiteSpace(MagpieProfileIndexText))
        {
            settings.MagpieProfileIndex = 0;
        }
        else if (int.TryParse(MagpieProfileIndexText.Trim(), out var magpieProfileIndex))
        {
            settings.MagpieProfileIndex = magpieProfileIndex;
        }

        if (int.TryParse(PhashThresholdText.Trim(), out var phashThreshold))
        {
            settings.PhashThreshold = phashThreshold;
        }

        if (double.TryParse(IouThresholdText.Trim(), out var iouThreshold))
        {
            settings.OcrIouThreshold = iouThreshold;
        }

        if (int.TryParse(OcrPerfLogThresholdText.Trim(), out var perfThreshold))
        {
            settings.OcrPerfLogThresholdMs = Math.Max(0, perfThreshold);
        }

        if (double.TryParse(PaddleTextDetThreshText.Trim(), out var textDetThresh))
        {
            settings.PaddleTextDetThresh = textDetThresh;
        }

        if (double.TryParse(PaddleTextDetBoxThreshText.Trim(), out var textDetBoxThresh))
        {
            settings.PaddleTextDetBoxThresh = textDetBoxThresh;
        }

        if (double.TryParse(PaddleTextDetUnclipRatioText.Trim(), out var textDetUnclip))
        {
            settings.PaddleTextDetUnclipRatio = textDetUnclip;
        }

        if (double.TryParse(PaddleTextRecScoreThreshText.Trim(), out var textRecScoreThresh))
        {
            settings.PaddleTextRecScoreThresh = textRecScoreThresh;
        }

        if (int.TryParse(PaddleVlMaxPixelsText.Trim(), out var paddleVlMaxPixels))
        {
            settings.PaddleVlMaxPixels = paddleVlMaxPixels;
        }
        else if (string.IsNullOrWhiteSpace(PaddleVlMaxPixelsText))
        {
            settings.PaddleVlMaxPixels = null;
        }

        if (double.TryParse(PaddleVlLayoutThresholdText.Trim(), out var paddleVlLayoutThreshold))
        {
            settings.PaddleVlLayoutThreshold = paddleVlLayoutThreshold;
        }
        else if (string.IsNullOrWhiteSpace(PaddleVlLayoutThresholdText))
        {
            settings.PaddleVlLayoutThreshold = null;
        }

        if (int.TryParse(PaddleVlMaxNewTokensText.Trim(), out var paddleVlMaxNewTokens))
        {
            settings.PaddleVlMaxNewTokens = Math.Clamp(paddleVlMaxNewTokens, 512, 4096);
        }
        else if (string.IsNullOrWhiteSpace(PaddleVlMaxNewTokensText))
        {
            // NOTE: Blank means AUTO; Python side keeps PaddleOCR-VL internal default.
            settings.PaddleVlMaxNewTokens = null;
        }

        settings.LlamaHost = (LlamaHostText ?? string.Empty).Trim();
        if (int.TryParse(LlamaPortText.Trim(), out var llamaPort))
        {
            settings.LlamaPort = llamaPort;
        }

        if (int.TryParse(LlamaContextSizeText.Trim(), out var llamaContext))
        {
            settings.LlamaContextSize = llamaContext;
        }

        if (int.TryParse(LlamaGpuLayersText.Trim(), out var llamaGpuLayers))
        {
            settings.LlamaGpuLayers = llamaGpuLayers;
        }

        if (int.TryParse(LlamaThreadsText.Trim(), out var llamaThreads))
        {
            settings.LlamaThreads = llamaThreads;
        }

        if (int.TryParse(LlamaParallelText.Trim(), out var llamaParallel))
        {
            settings.LlamaParallel = llamaParallel;
        }

        if (int.TryParse(LlamaBatchSizeText.Trim(), out var llamaBatchSize))
        {
            settings.LlamaBatchSize = llamaBatchSize;
        }

        if (int.TryParse(LlamaMaxTokensText.Trim(), out var llamaMaxTokens))
        {
            settings.LlamaMaxTokens = llamaMaxTokens;
        }

        if (double.TryParse(LlamaTemperatureText.Trim(), out var llamaTemperature))
        {
            settings.LlamaTemperature = llamaTemperature;
        }

        if (double.TryParse(LlamaTopPText.Trim(), out var llamaTopP))
        {
            settings.LlamaTopP = llamaTopP;
        }

        if (int.TryParse(LlamaTopKText.Trim(), out var llamaTopK))
        {
            settings.LlamaTopK = llamaTopK;
        }

        if (double.TryParse(LlamaRepeatPenaltyText.Trim(), out var llamaRepeatPenalty))
        {
            settings.LlamaRepeatPenalty = llamaRepeatPenalty;
        }

        settings.VisionLlmHost = (VisionLlmHostText ?? string.Empty).Trim();
        if (int.TryParse(VisionLlmPortText.Trim(), out var visionLlmPort))
        {
            settings.VisionLlmPort = visionLlmPort;
        }

        if (int.TryParse(VisionLlmContextSizeText.Trim(), out var visionLlmContext))
        {
            settings.VisionLlmContextSize = visionLlmContext;
        }

        if (int.TryParse(VisionLlmGpuLayersText.Trim(), out var visionLlmGpuLayers))
        {
            settings.VisionLlmGpuLayers = visionLlmGpuLayers;
        }

        if (int.TryParse(VisionLlmThreadsText.Trim(), out var visionLlmThreads))
        {
            settings.VisionLlmThreads = visionLlmThreads;
        }

        if (int.TryParse(VisionLlmParallelText.Trim(), out var visionLlmParallel))
        {
            settings.VisionLlmParallel = visionLlmParallel;
        }

        if (int.TryParse(VisionLlmBatchSizeText.Trim(), out var visionLlmBatchSize))
        {
            settings.VisionLlmBatchSize = visionLlmBatchSize;
        }

        if (int.TryParse(VisionLlmMaxTokensText.Trim(), out var visionLlmMaxTokens))
        {
            settings.VisionLlmMaxTokens = visionLlmMaxTokens;
        }

        if (int.TryParse(VisionLlmMaxImageSideText.Trim(), out var visionLlmMaxImageSide))
        {
            settings.VisionLlmMaxImageSide = visionLlmMaxImageSide;
        }

        settings.EnableVisionGeometryHybridOcr = EnableVisionGeometryHybridOcr;
        settings.VisionGeometryHybridBaseEngine = VisionGeometryHybridBaseEngineTag switch
        {
            "Ndl" => VisionGeometryHybridBaseEngineKind.Ndl,
            "Paddle" => VisionGeometryHybridBaseEngineKind.Paddle,
            _ => VisionGeometryHybridBaseEngineKind.WinRt
        };

        settings.DeepLEndpoint = (DeepLEndpointText ?? string.Empty).Trim();
        settings.DeepLApiKey = DeepLApiKeyText ?? string.Empty;
        settings.ApiKey = ApiKeyText ?? string.Empty;
        ApplyHotkeySettings(settings);
        settings.PaddleConfidenceThreshold = Math.Round(PaddleConfidenceThreshold, 2);
        settings.OcrBinarizationThreshold = (int)Math.Round(OcrBinarizationThreshold);
        settings.OcrGamma = Math.Round(OcrGamma, 2);
        settings.OcrContrast = Math.Round(Math.Clamp(OcrContrast, 0.5, 2.0), 2);
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

    // WHY: NumberBox edits commit through Value, but ApplyTo() still parses the
    // legacy string fields. Keep both representations aligned until that path is removed.
    private void SyncNumericFieldFromText(string? text, Action<double?> setValue, Func<string, double?> parseValue)
    {
        if (_suspendNumericFieldSync)
        {
            return;
        }

        _suspendNumericFieldSync = true;
        try
        {
            setValue(parseValue(text ?? string.Empty));
        }
        finally
        {
            _suspendNumericFieldSync = false;
        }
    }

    private void SyncTextFieldFromNumeric(double? value, Action<string> setText, Func<double?, string> formatValue)
    {
        if (_suspendNumericFieldSync)
        {
            return;
        }

        _suspendNumericFieldSync = true;
        try
        {
            setText(formatValue(value));
        }
        finally
        {
            _suspendNumericFieldSync = false;
        }
    }

    private static double? ParseIntegerNumberBoxValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var parsed) ? parsed : null;
    }

    private static double? ParseDoubleNumberBoxValue(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return double.TryParse(text.Trim(), out var parsed) ? parsed : null;
    }

    private static string FormatIntegerNumberBoxText(double? value)
    {
        return value.HasValue ? ((int)Math.Round(value.Value)).ToString() : string.Empty;
    }

    private static string FormatDoubleNumberBoxText(double? value, string format)
    {
        return value.HasValue ? value.Value.ToString(format) : string.Empty;
    }

    partial void OnPaddleConfidenceThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrBinarizationThresholdChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrGammaChanged(double value) => RequestSaveOnValueChange();
    partial void OnOcrContrastChanged(double value) => RequestSaveOnValueChange();
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
    partial void OnCaptureModeTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnCaptureProviderTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnIsCaptureProviderFixedChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableRoiChanged(bool value) => RequestSaveOnValueChange();
    partial void OnOcrEngineTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleDetectionModelNameChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleRecognitionModelNameChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleVlPipelineVersionChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleVlUseLayoutDetectionModeTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleVlPrecisionTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnablePaddleConfidenceFilterChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableLlamaCppTranslationChanged(bool value) => RequestSaveOnValueChange();
    partial void OnLlamaSelectedModelFileNameChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmSelectedModelFileNameChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmSelectedMmprojFileNameChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnableDeepLChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableGeminiChanged(bool value) => RequestSaveOnValueChange();
    partial void OnVerticalModeOverrideTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnableSimpleMergeTuningChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHorizontalMergeStrengthChanged(double value) => RequestSaveOnValueChange();
    partial void OnVerticalMergeStrengthChanged(double value) => RequestSaveOnValueChange();
    partial void OnEnableLoggingChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrPerfLogChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrBinarizationChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrAutoThresholdChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrGammaChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrGrayscaleChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrContrastChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrDownsamplingChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOcrTwoPassChanged(bool value) => RequestSaveOnValueChange();
    partial void OnOcrTwoPassPreferAutoChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableFixedRoiOverlayChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableOverlayFontStabilizationChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableSmallBoxReadabilityBoostChanged(bool value) => RequestSaveOnValueChange();
    partial void OnPhashThresholdTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PhashThresholdValue = parsed, ParseIntegerNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPhashThresholdValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PhashThresholdText = formatted, FormatIntegerNumberBoxText);
    }

    partial void OnIouThresholdTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => IouThresholdValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnIouThresholdValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => IouThresholdText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.00"));
    }
    partial void OnOcrPerfLogThresholdTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnPaddleTextDetThreshTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleTextDetThreshValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleTextDetThreshValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleTextDetThreshText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.###"));
    }

    partial void OnPaddleTextDetBoxThreshTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleTextDetBoxThreshValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleTextDetBoxThreshValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleTextDetBoxThreshText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.###"));
    }

    partial void OnPaddleTextDetUnclipRatioTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleTextDetUnclipRatioValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleTextDetUnclipRatioValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleTextDetUnclipRatioText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.###"));
    }

    partial void OnPaddleTextRecScoreThreshTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleTextRecScoreThreshValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleTextRecScoreThreshValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleTextRecScoreThreshText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.###"));
    }

    partial void OnPaddleVlMaxPixelsTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleVlMaxPixelsValue = parsed, ParseIntegerNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleVlMaxPixelsValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleVlMaxPixelsText = formatted, FormatIntegerNumberBoxText);
    }

    partial void OnPaddleVlLayoutThresholdTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleVlLayoutThresholdValue = parsed, ParseDoubleNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleVlLayoutThresholdValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleVlLayoutThresholdText = formatted, parsed => FormatDoubleNumberBoxText(parsed, "0.###"));
    }

    partial void OnPaddleVlMaxNewTokensTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => PaddleVlMaxNewTokensValue = parsed, ParseIntegerNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnPaddleVlMaxNewTokensValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => PaddleVlMaxNewTokensText = formatted, FormatIntegerNumberBoxText);
    }
    partial void OnLlamaHostTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaPortTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaContextSizeTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaGpuLayersTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaThreadsTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaParallelTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaBatchSizeTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaMaxTokensTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaTemperatureTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaTopPTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaTopKTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnLlamaRepeatPenaltyTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmHostTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmPortTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmContextSizeTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmGpuLayersTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmThreadsTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmParallelTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmBatchSizeTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmMaxTokensTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnVisionLlmMaxImageSideTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnableVisionLlmSharedLocalTranslationChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableVisionGeometryHybridOcrChanged(bool value) => RequestSaveOnValueChange();
    partial void OnVisionGeometryHybridBaseEngineTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnDeepLEndpointTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnDeepLApiKeyTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnApiKeyTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunOnceKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunOnceCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunOnceAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunOnceShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextRoiKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextRoiCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextRoiAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextRoiShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextNextRoiKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextNextRoiCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextNextRoiAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyRunNextNextRoiShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleOverlayKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleOverlayCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleOverlayAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleOverlayShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextRoiKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextRoiCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextRoiAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextRoiShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextNextRoiKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextNextRoiCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextNextRoiAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceRunNextNextRoiShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceGeminiStrictKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceGeminiStrictCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceGeminiStrictAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyForceGeminiStrictShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyOcrOnlyKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyOcrOnlyCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyOcrOnlyAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyOcrOnlyShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleSceneAutoTranslateKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleSceneAutoTranslateCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleSceneAutoTranslateAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleSceneAutoTranslateShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeySelectRoiKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeySelectRoiCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeySelectRoiAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeySelectRoiShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyLockCaptureWindowKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyLockCaptureWindowCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyLockCaptureWindowAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyLockCaptureWindowShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyUnlockCaptureWindowKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyUnlockCaptureWindowCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyUnlockCaptureWindowAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyUnlockCaptureWindowShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleMirrorFullscreenKeyChanged(string value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleMirrorFullscreenCtrlChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleMirrorFullscreenAltChanged(bool value) => RequestSaveOnValueChange();
    partial void OnHotkeyToggleMirrorFullscreenShiftChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableRawInputHotkeysChanged(bool value) => RequestSaveOnValueChange();
    partial void OnShowAutoTranslateBadgeIconChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableGraphicsHookPipelineChanged(bool value)
    {
        if (_suspendMirrorModeSync)
        {
            RequestSaveOnValueChange();
            return;
        }

        if (value && EnableMirrorFullscreenMode)
        {
            _suspendMirrorModeSync = true;
            try
            {
                EnableMirrorFullscreenMode = false;
            }
            finally
            {
                _suspendMirrorModeSync = false;
            }
        }

        RequestSaveOnValueChange();
    }

    partial void OnGraphicsHookApiTagChanged(string value) => RequestSaveOnValueChange();

    partial void OnEnableMirrorFullscreenModeChanged(bool value)
    {
        if (_suspendMirrorModeSync)
        {
            RequestSaveOnValueChange();
            return;
        }

        if (value && EnableGraphicsHookPipeline)
        {
            _suspendMirrorModeSync = true;
            try
            {
                EnableGraphicsHookPipeline = false;
            }
            finally
            {
                _suspendMirrorModeSync = false;
            }
        }

        RequestSaveOnValueChange();
    }
    partial void OnGraphicsHookOverlayEnabledChanged(bool value) => RequestSaveOnValueChange();
    partial void OnGraphicsHookFallbackOnErrorChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableGraphicsHookPerfDiagLogChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableGraphicsHookDiagFileSinkChanged(bool value) => RequestSaveOnValueChange();
    partial void OnGraphicsHookCaptureFpsLimitTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnableGraphicsHookLauncherChanged(bool value) => RequestSaveOnValueChange();
    partial void OnResourceBudgetProfileTagChanged(string value) => RequestSaveOnValueChange();
    partial void OnGraphicsHookLauncherExePathChanged(string value) => RequestSaveOnValueChange();
    partial void OnGraphicsHookLauncherArgsChanged(string value) => RequestSaveOnValueChange();
    partial void OnMagpieProfileIndexTextChanged(string value) => RequestSaveOnValueChange();
    partial void OnEnableSceneChangeTextWeightedChanged(bool value) => RequestSaveOnValueChange();
    partial void OnEnableSceneChangeQuietWindowChanged(bool value) => RequestSaveOnValueChange();
    partial void OnSceneChangeQuietWindowMsTextChanged(string value)
    {
        SyncNumericFieldFromText(value, parsed => SceneChangeQuietWindowMsValue = parsed, ParseIntegerNumberBoxValue);
        RequestSaveOnValueChange();
    }

    partial void OnSceneChangeQuietWindowMsValueChanged(double? value)
    {
        SyncTextFieldFromNumeric(value, formatted => SceneChangeQuietWindowMsText = formatted, FormatIntegerNumberBoxText);
    }
    partial void OnEnableSceneChangeAutoHideChanged(bool value)
    {
        if (_suspendSceneModeSync)
        {
            RequestSaveOnValueChange();
            return;
        }

        if (value && EnableSceneChangeAutoTranslate)
        {
            _suspendSceneModeSync = true;
            try
            {
                EnableSceneChangeAutoTranslate = false;
            }
            finally
            {
                _suspendSceneModeSync = false;
            }
        }

        RequestSaveOnValueChange();
    }

    partial void OnEnableSceneChangeAutoTranslateChanged(bool value)
    {
        if (_suspendSceneModeSync)
        {
            RequestSaveOnValueChange();
            return;
        }

        if (value && EnableSceneChangeAutoHide)
        {
            _suspendSceneModeSync = true;
            try
            {
                EnableSceneChangeAutoHide = false;
            }
            finally
            {
                _suspendSceneModeSync = false;
            }
        }

        RequestSaveOnValueChange();
    }

    private void AssignHotkeySettings(AppSettings settings)
    {
        HotkeyRunOnceKey = NormalizeHotkeyKey(settings.HotkeyRunOnceKey, "F8");
        AssignHotkeyModifiers(settings.HotkeyRunOnceModifiers, out var runOnceCtrl, out var runOnceAlt, out var runOnceShift);
        HotkeyRunOnceCtrl = runOnceCtrl;
        HotkeyRunOnceAlt = runOnceAlt;
        HotkeyRunOnceShift = runOnceShift;

        HotkeyRunNextRoiKey = NormalizeHotkeyKey(settings.HotkeyRunNextRoiKey, "F8");
        AssignHotkeyModifiers(settings.HotkeyRunNextRoiModifiers, out var runNextRoiCtrl, out var runNextRoiAlt, out var runNextRoiShift);
        HotkeyRunNextRoiCtrl = runNextRoiCtrl;
        HotkeyRunNextRoiAlt = runNextRoiAlt;
        HotkeyRunNextRoiShift = runNextRoiShift;

        HotkeyRunNextNextRoiKey = NormalizeHotkeyKey(settings.HotkeyRunNextNextRoiKey, "F8");
        AssignHotkeyModifiers(settings.HotkeyRunNextNextRoiModifiers, out var runNextNextRoiCtrl, out var runNextNextRoiAlt, out var runNextNextRoiShift);
        HotkeyRunNextNextRoiCtrl = runNextNextRoiCtrl;
        HotkeyRunNextNextRoiAlt = runNextNextRoiAlt;
        HotkeyRunNextNextRoiShift = runNextNextRoiShift;

        HotkeyToggleOverlayKey = NormalizeHotkeyKey(settings.HotkeyToggleOverlayKey, "F9");
        AssignHotkeyModifiers(settings.HotkeyToggleOverlayModifiers, out var toggleOverlayCtrl, out var toggleOverlayAlt,
            out var toggleOverlayShift);
        HotkeyToggleOverlayCtrl = toggleOverlayCtrl;
        HotkeyToggleOverlayAlt = toggleOverlayAlt;
        HotkeyToggleOverlayShift = toggleOverlayShift;

        HotkeyForceRunKey = NormalizeHotkeyKey(settings.HotkeyForceRunKey, "F10");
        AssignHotkeyModifiers(settings.HotkeyForceRunModifiers, out var forceRunCtrl, out var forceRunAlt, out var forceRunShift);
        HotkeyForceRunCtrl = forceRunCtrl;
        HotkeyForceRunAlt = forceRunAlt;
        HotkeyForceRunShift = forceRunShift;

        HotkeyForceRunNextRoiKey = NormalizeHotkeyKey(settings.HotkeyForceRunNextRoiKey, "F10");
        AssignHotkeyModifiers(settings.HotkeyForceRunNextRoiModifiers, out var forceRunNextRoiCtrl, out var forceRunNextRoiAlt, out var forceRunNextRoiShift);
        HotkeyForceRunNextRoiCtrl = forceRunNextRoiCtrl;
        HotkeyForceRunNextRoiAlt = forceRunNextRoiAlt;
        HotkeyForceRunNextRoiShift = forceRunNextRoiShift;

        HotkeyForceRunNextNextRoiKey = NormalizeHotkeyKey(settings.HotkeyForceRunNextNextRoiKey, "F10");
        AssignHotkeyModifiers(settings.HotkeyForceRunNextNextRoiModifiers, out var forceRunNextNextRoiCtrl, out var forceRunNextNextRoiAlt, out var forceRunNextNextRoiShift);
        HotkeyForceRunNextNextRoiCtrl = forceRunNextNextRoiCtrl;
        HotkeyForceRunNextNextRoiAlt = forceRunNextNextRoiAlt;
        HotkeyForceRunNextNextRoiShift = forceRunNextNextRoiShift;

        HotkeyForceGeminiStrictKey = NormalizeHotkeyKey(settings.HotkeyForceGeminiStrictKey, "F10");
        AssignHotkeyModifiers(settings.HotkeyForceGeminiStrictModifiers, out var forceGeminiCtrl, out var forceGeminiAlt,
            out var forceGeminiShift);
        HotkeyForceGeminiStrictCtrl = forceGeminiCtrl;
        HotkeyForceGeminiStrictAlt = forceGeminiAlt;
        HotkeyForceGeminiStrictShift = forceGeminiShift;

        HotkeyOcrOnlyKey = NormalizeHotkeyKey(settings.HotkeyOcrOnlyKey, "F11");
        AssignHotkeyModifiers(settings.HotkeyOcrOnlyModifiers, out var ocrOnlyCtrl, out var ocrOnlyAlt, out var ocrOnlyShift);
        HotkeyOcrOnlyCtrl = ocrOnlyCtrl;
        HotkeyOcrOnlyAlt = ocrOnlyAlt;
        HotkeyOcrOnlyShift = ocrOnlyShift;

        HotkeyToggleSceneAutoTranslateKey = NormalizeHotkeyKey(settings.HotkeyToggleSceneAutoTranslateKey, "F5");
        AssignHotkeyModifiers(settings.HotkeyToggleSceneAutoTranslateModifiers, out var sceneToggleCtrl, out var sceneToggleAlt,
            out var sceneToggleShift);
        HotkeyToggleSceneAutoTranslateCtrl = sceneToggleCtrl;
        HotkeyToggleSceneAutoTranslateAlt = sceneToggleAlt;
        HotkeyToggleSceneAutoTranslateShift = sceneToggleShift;

        HotkeySelectRoiKey = NormalizeHotkeyKey(settings.HotkeySelectRoiKey, "F6");
        AssignHotkeyModifiers(settings.HotkeySelectRoiModifiers, out var selectRoiCtrl, out var selectRoiAlt, out var selectRoiShift);
        HotkeySelectRoiCtrl = selectRoiCtrl;
        HotkeySelectRoiAlt = selectRoiAlt;
        HotkeySelectRoiShift = selectRoiShift;

        HotkeyLockCaptureWindowKey = NormalizeHotkeyKey(settings.HotkeyLockCaptureWindowKey, "F7");
        AssignHotkeyModifiers(settings.HotkeyLockCaptureWindowModifiers, out var lockWindowCtrl, out var lockWindowAlt,
            out var lockWindowShift);
        HotkeyLockCaptureWindowCtrl = lockWindowCtrl;
        HotkeyLockCaptureWindowAlt = lockWindowAlt;
        HotkeyLockCaptureWindowShift = lockWindowShift;

        HotkeyUnlockCaptureWindowKey = NormalizeHotkeyKey(settings.HotkeyUnlockCaptureWindowKey, "F7");
        AssignHotkeyModifiers(settings.HotkeyUnlockCaptureWindowModifiers, out var unlockWindowCtrl, out var unlockWindowAlt,
            out var unlockWindowShift);
        HotkeyUnlockCaptureWindowCtrl = unlockWindowCtrl;
        HotkeyUnlockCaptureWindowAlt = unlockWindowAlt;
        HotkeyUnlockCaptureWindowShift = unlockWindowShift;

        HotkeyToggleMirrorFullscreenKey = NormalizeHotkeyKey(settings.HotkeyToggleMirrorFullscreenKey, "F7");
        AssignHotkeyModifiers(settings.HotkeyToggleMirrorFullscreenModifiers, out var toggleMirrorCtrl, out var toggleMirrorAlt,
            out var toggleMirrorShift);
        HotkeyToggleMirrorFullscreenCtrl = toggleMirrorCtrl;
        HotkeyToggleMirrorFullscreenAlt = toggleMirrorAlt;
        HotkeyToggleMirrorFullscreenShift = toggleMirrorShift;
    }

    private void ApplyHotkeySettings(AppSettings settings)
    {
        settings.HotkeyRunOnceKey = NormalizeHotkeyKey(HotkeyRunOnceKey, "F8");
        settings.HotkeyRunOnceModifiers = BuildHotkeyModifiers(HotkeyRunOnceCtrl, HotkeyRunOnceAlt, HotkeyRunOnceShift);
        settings.HotkeyRunNextRoiKey = NormalizeHotkeyKey(HotkeyRunNextRoiKey, "F8");
        settings.HotkeyRunNextRoiModifiers = BuildHotkeyModifiers(HotkeyRunNextRoiCtrl, HotkeyRunNextRoiAlt, HotkeyRunNextRoiShift);
        settings.HotkeyRunNextNextRoiKey = NormalizeHotkeyKey(HotkeyRunNextNextRoiKey, "F8");
        settings.HotkeyRunNextNextRoiModifiers = BuildHotkeyModifiers(HotkeyRunNextNextRoiCtrl, HotkeyRunNextNextRoiAlt, HotkeyRunNextNextRoiShift);
        settings.HotkeyToggleOverlayKey = NormalizeHotkeyKey(HotkeyToggleOverlayKey, "F9");
        settings.HotkeyToggleOverlayModifiers = BuildHotkeyModifiers(HotkeyToggleOverlayCtrl, HotkeyToggleOverlayAlt,
            HotkeyToggleOverlayShift);
        settings.HotkeyForceRunKey = NormalizeHotkeyKey(HotkeyForceRunKey, "F10");
        settings.HotkeyForceRunModifiers = BuildHotkeyModifiers(HotkeyForceRunCtrl, HotkeyForceRunAlt, HotkeyForceRunShift);
        settings.HotkeyForceRunNextRoiKey = NormalizeHotkeyKey(HotkeyForceRunNextRoiKey, "F10");
        settings.HotkeyForceRunNextRoiModifiers = BuildHotkeyModifiers(HotkeyForceRunNextRoiCtrl, HotkeyForceRunNextRoiAlt, HotkeyForceRunNextRoiShift);
        settings.HotkeyForceRunNextNextRoiKey = NormalizeHotkeyKey(HotkeyForceRunNextNextRoiKey, "F10");
        settings.HotkeyForceRunNextNextRoiModifiers =
            BuildHotkeyModifiers(HotkeyForceRunNextNextRoiCtrl, HotkeyForceRunNextNextRoiAlt, HotkeyForceRunNextNextRoiShift);
        settings.HotkeyForceGeminiStrictKey = NormalizeHotkeyKey(HotkeyForceGeminiStrictKey, "F10");
        settings.HotkeyForceGeminiStrictModifiers = BuildHotkeyModifiers(HotkeyForceGeminiStrictCtrl, HotkeyForceGeminiStrictAlt,
            HotkeyForceGeminiStrictShift);
        settings.HotkeyOcrOnlyKey = NormalizeHotkeyKey(HotkeyOcrOnlyKey, "F11");
        settings.HotkeyOcrOnlyModifiers = BuildHotkeyModifiers(HotkeyOcrOnlyCtrl, HotkeyOcrOnlyAlt, HotkeyOcrOnlyShift);
        settings.HotkeyToggleSceneAutoTranslateKey =
            NormalizeHotkeyKey(HotkeyToggleSceneAutoTranslateKey, "F5");
        settings.HotkeyToggleSceneAutoTranslateModifiers =
            BuildHotkeyModifiers(HotkeyToggleSceneAutoTranslateCtrl, HotkeyToggleSceneAutoTranslateAlt,
                HotkeyToggleSceneAutoTranslateShift);
        settings.HotkeySelectRoiKey = NormalizeHotkeyKey(HotkeySelectRoiKey, "F6");
        settings.HotkeySelectRoiModifiers = BuildHotkeyModifiers(HotkeySelectRoiCtrl, HotkeySelectRoiAlt, HotkeySelectRoiShift);
        settings.HotkeyLockCaptureWindowKey = NormalizeHotkeyKey(HotkeyLockCaptureWindowKey, "F7");
        settings.HotkeyLockCaptureWindowModifiers =
            BuildHotkeyModifiers(HotkeyLockCaptureWindowCtrl, HotkeyLockCaptureWindowAlt, HotkeyLockCaptureWindowShift);
        settings.HotkeyUnlockCaptureWindowKey = NormalizeHotkeyKey(HotkeyUnlockCaptureWindowKey, "F7");
        settings.HotkeyUnlockCaptureWindowModifiers =
            BuildHotkeyModifiers(HotkeyUnlockCaptureWindowCtrl, HotkeyUnlockCaptureWindowAlt, HotkeyUnlockCaptureWindowShift);
        settings.HotkeyToggleMirrorFullscreenKey = NormalizeHotkeyKey(HotkeyToggleMirrorFullscreenKey, "F7");
        settings.HotkeyToggleMirrorFullscreenModifiers =
            BuildHotkeyModifiers(HotkeyToggleMirrorFullscreenCtrl, HotkeyToggleMirrorFullscreenAlt, HotkeyToggleMirrorFullscreenShift);
    }

    private static void AssignHotkeyModifiers(string modifiers, out bool ctrl, out bool alt, out bool shift)
    {
        ctrl = false;
        alt = false;
        shift = false;
        var tokens = (modifiers ?? string.Empty).Split(new[] { ',', '+', ';' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            var normalized = token.Trim();
            if (normalized.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                ctrl = true;
            }
            else if (normalized.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                alt = true;
            }
            else if (normalized.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                shift = true;
            }
        }
    }

    private static string BuildHotkeyModifiers(bool ctrl, bool alt, bool shift)
    {
        var parts = new List<string>(3);
        if (ctrl)
        {
            parts.Add("Control");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        return parts.Count == 0 ? "None" : string.Join(", ", parts);
    }

    private static string NormalizeHotkeyKey(string value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string ToPaddleVlLayoutDetectionModeTag(bool? value)
    {
        return value switch
        {
            true => "true",
            false => "false",
            _ => "auto"
        };
    }

    private static bool? ParsePaddleVlLayoutDetectionModeTag(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "true" => true,
            "false" => false,
            _ => null
        };
    }

    private static string NormalizePaddleVlPrecisionTag(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized == "fp16" ? "fp16" : "fp32";
    }

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



