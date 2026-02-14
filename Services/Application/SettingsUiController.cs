using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Input;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal interface ISettingsUiBridge
{
    bool IsLoaded { get; }
    bool IsApplyingSettings { get; set; }
    void ApplyUiInputToSettings(AppSettings settings);
    void ApplyRuntimeStateAfterSave(AppSettings settings);
    Task<bool> EnsureResourceHostsAsync(AppSettings settings);
    Task PersistSettingsAsync();
    void AppendLog(string message);
    void TryUpdateHotkeys(AppSettings settings);
    void UpdateAutoHideWatcher(AppSettings settings);
    void ClearSceneChangeAutoTranslatePending(string reason);
}

internal sealed class SettingsUiController
{
    private const string DefaultLlamaModelFileName = "HY-MT1.5-1.8B-Q8_0.gguf";

    private readonly SettingsService _settingsService;
    private readonly ISettingsUiBridge _bridge;
    private readonly Func<AppLogger?> _loggerAccessor;

    public SettingsUiController(
        SettingsService settingsService,
        ISettingsUiBridge bridge,
        Func<AppLogger?> loggerAccessor)
    {
        _settingsService = settingsService;
        _bridge = bridge;
        _loggerAccessor = loggerAccessor;
    }

    public bool NormalizeOnLoad(AppSettings settings)
    {
        var settingsChanged = NormalizeHotkeySettings(settings);
        settingsChanged |= NormalizeSceneChangeModeSettings(settings);
        settingsChanged |= NormalizeSceneSemanticSettings(settings);
        settingsChanged |= NormalizeWritingModeSettings(settings);
        settingsChanged |= NormalizeSmallBoxReadabilitySettings(settings);
        settingsChanged |= NormalizePaddleOcrSettings(settings);
        if (settings.EnableCTranslate2)
        {
            settings.EnableCTranslate2 = false;
            settingsChanged = true;
        }

        return settingsChanged;
    }

    public async Task SaveFromUiAsync()
    {
        if (_bridge.IsApplyingSettings || !_bridge.IsLoaded)
        {
            return;
        }

        var previousApplyingState = _bridge.IsApplyingSettings;
        _bridge.IsApplyingSettings = true;
        try
        {
            var settings = _settingsService.Settings;
            _bridge.ApplyUiInputToSettings(settings);

            NormalizeSceneChangeModeSettings(settings);

            if (!settings.EnableSceneChangeAutoTranslate)
            {
                _bridge.ClearSceneChangeAutoTranslatePending("auto-translate disabled");
            }

            NormalizeLlamaSettings(settings);
            NormalizeSceneSemanticSettings(settings);
            NormalizeWritingModeSettings(settings);
            NormalizeSmallBoxReadabilitySettings(settings);
            NormalizePaddleOcrSettings(settings);

            _bridge.ApplyRuntimeStateAfterSave(settings);
            await _bridge.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
            await _bridge.PersistSettingsAsync().ConfigureAwait(true);
            _bridge.AppendLog("Settings saved.");
            _bridge.TryUpdateHotkeys(settings);
            _bridge.UpdateAutoHideWatcher(settings);
        }
        finally
        {
            _bridge.IsApplyingSettings = previousApplyingState;
        }
    }

    public static void NormalizeCTranslate2Settings(AppSettings settings)
    {
        settings.CTranslate2Device = NormalizeCTranslate2Device(settings.CTranslate2Device);
        settings.CTranslate2Precision = ResolveCTranslate2Precision(settings.CTranslate2Device);
        settings.EnableCTranslate2AutoDownload = true;
        if (string.IsNullOrWhiteSpace(settings.CTranslate2ModelId))
        {
            settings.CTranslate2ModelId = "entai2965/nllb-200-distilled-600M-ctranslate2";
        }
    }

    public static void NormalizeLlamaSettings(AppSettings settings)
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

    public static string NormalizeLlamaModelFileName(string? value)
    {
        var fileName = Path.GetFileName((value ?? string.Empty).Trim());
        return string.IsNullOrWhiteSpace(fileName) ||
               !fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
            ? DefaultLlamaModelFileName
            : fileName;
    }

    public static string NormalizeCTranslate2Device(string? device)
    {
        var normalized = (device ?? string.Empty).Trim();
        if (normalized.StartsWith("gpu", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("cuda", StringComparison.OrdinalIgnoreCase))
        {
            return "gpu";
        }

        return "cpu";
    }

    public static string ResolveCTranslate2Precision(string? device)
    {
        var normalized = NormalizeCTranslate2Device(device);
        return normalized == "gpu" ? "fp16" : "int8";
    }

    private bool NormalizeHotkeySettings(AppSettings settings)
    {
        var changed = false;
        var forceGeminiStrictKey = (settings.HotkeyForceGeminiStrictKey ?? string.Empty).Trim();
        var toggleSceneAutoTranslateKey = (settings.HotkeyToggleSceneAutoTranslateKey ?? string.Empty).Trim();
        var selectRoiKey = (settings.HotkeySelectRoiKey ?? string.Empty).Trim();
        var lockKey = (settings.HotkeyLockCaptureWindowKey ?? string.Empty).Trim();
        var lockModifiers = ParseModifiers(settings.HotkeyLockCaptureWindowModifiers);
        var unlockKey = (settings.HotkeyUnlockCaptureWindowKey ?? string.Empty).Trim();
        var unlockModifiers = ParseModifiers(settings.HotkeyUnlockCaptureWindowModifiers);

        if (string.IsNullOrWhiteSpace(forceGeminiStrictKey))
        {
            settings.HotkeyForceGeminiStrictKey = "F10";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyForceGeminiStrictModifiers))
        {
            settings.HotkeyForceGeminiStrictModifiers = "Shift";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(toggleSceneAutoTranslateKey))
        {
            settings.HotkeyToggleSceneAutoTranslateKey = "F5";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.HotkeyToggleSceneAutoTranslateModifiers))
        {
            settings.HotkeyToggleSceneAutoTranslateModifiers = "None";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(selectRoiKey))
        {
            settings.HotkeySelectRoiKey = "F6";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(lockKey))
        {
            settings.HotkeyLockCaptureWindowKey = "F7";
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(unlockKey))
        {
            settings.HotkeyUnlockCaptureWindowKey = "F7";
            changed = true;
        }

        // COMPAT: Legacy defaults (F12 / Shift+F12) are migrated to F7 to avoid common overlay/debug-tool conflicts.
        if (lockKey.Equals("F12", StringComparison.OrdinalIgnoreCase) &&
            lockModifiers == ModifierKeys.None &&
            unlockKey.Equals("F12", StringComparison.OrdinalIgnoreCase) &&
            unlockModifiers == ModifierKeys.Shift)
        {
            settings.HotkeyLockCaptureWindowKey = "F7";
            settings.HotkeyLockCaptureWindowModifiers = "None";
            settings.HotkeyUnlockCaptureWindowKey = "F7";
            settings.HotkeyUnlockCaptureWindowModifiers = "Shift";
            changed = true;
        }

        return changed;
    }

    private static bool NormalizeSceneChangeModeSettings(AppSettings settings)
    {
        if (!settings.EnableSceneChangeAutoHide || !settings.EnableSceneChangeAutoTranslate)
        {
            return false;
        }

        // COMPAT: Legacy settings may have both enabled; keep auto-hide as the fixed priority.
        settings.EnableSceneChangeAutoTranslate = false;
        return true;
    }

    private bool NormalizeSceneSemanticSettings(AppSettings settings)
    {
        var changed = false;
        changed |= ClampSetting(settings.SceneSemanticBlockIouThreshold, 0.1, 0.95, 0.5, out var semanticIou);
        changed |= ClampSetting(settings.SceneSemanticMinChars, 0, 64, 2, out var semanticMinChars);
        changed |= ClampSetting(settings.SceneSemanticRequireConfirmTicks, 1, 5, 1, out var semanticConfirmTicks);
        settings.SceneSemanticBlockIouThreshold = semanticIou;
        settings.SceneSemanticMinChars = semanticMinChars;
        settings.SceneSemanticRequireConfirmTicks = semanticConfirmTicks;
        if (changed)
        {
            _loggerAccessor()?.Info(
                $"Scene semantic gate settings normalized: IoU={semanticIou:0.##}, MinChars={semanticMinChars}, ConfirmTicks={semanticConfirmTicks}.");
        }

        return changed;
    }

    private bool NormalizeWritingModeSettings(AppSettings settings)
    {
        var changed = false;
        if (!Enum.IsDefined(typeof(VerticalModeOverride), settings.VerticalModeOverride))
        {
            // COMPAT: Unknown persisted enum values must not break runtime behavior; fallback to Auto.
            _loggerAccessor()?.Info($"VerticalModeOverride value '{(int)settings.VerticalModeOverride}' is invalid. Falling back to Auto.");
            settings.VerticalModeOverride = VerticalModeOverride.Auto;
            changed = true;
        }

        // COMPAT: Writing-mode behavior is now controlled by VerticalModeOverride; keep legacy toggles enabled.
        if (!settings.EnableVerticalMerge)
        {
            settings.EnableVerticalMerge = true;
            changed = true;
        }

        if (!settings.VerticalModeAutoDetect)
        {
            settings.VerticalModeAutoDetect = true;
            changed = true;
        }

        return changed;
    }

    private bool NormalizeSmallBoxReadabilitySettings(AppSettings settings)
    {
        var changed = false;
        changed |= ClampSetting(settings.SmallTextThresholdPx, 8.0, 48.0, 22.0, out var smallTextThreshold);
        changed |= ClampSetting(settings.SmallBoxMaxScale, 1.0, 3.0, 1.6, out var smallBoxMaxScale);
        changed |= ClampSetting(settings.SmallBoxFontScaleWeight, 0.0, 1.0, 0.7, out var smallBoxFontScaleWeight);
        changed |= ClampSetting(settings.SmallBoxSlenderAspectThreshold, 1.0, 8.0, 3.0, out var smallBoxSlenderAspectThreshold);
        changed |= ClampSetting(settings.SmallBoxSlenderThresholdBoost, 1.0, 2.0, 1.2, out var smallBoxSlenderThresholdBoost);
        settings.SmallTextThresholdPx = smallTextThreshold;
        settings.SmallBoxMaxScale = smallBoxMaxScale;
        settings.SmallBoxFontScaleWeight = smallBoxFontScaleWeight;
        settings.SmallBoxSlenderAspectThreshold = smallBoxSlenderAspectThreshold;
        settings.SmallBoxSlenderThresholdBoost = smallBoxSlenderThresholdBoost;
        if (changed)
        {
            // WHY: settings.json allows advanced tuning; clamp here so malformed values don't destabilize overlay layout.
            _loggerAccessor()?.Info("Small-box readability settings were clamped to safe ranges.");
        }

        return changed;
    }

    private bool NormalizePaddleOcrSettings(AppSettings settings)
    {
        var changed = false;
        changed |= ClampSetting(settings.PaddleTextDetThresh, 0.0, 1.0, 0.5, out var textDetThresh);
        changed |= ClampSetting(settings.PaddleTextDetBoxThresh, 0.0, 1.0, 0.68, out var textDetBoxThresh);
        changed |= ClampSetting(settings.PaddleTextDetUnclipRatio, 0.5, 3.0, 1.3, out var textDetUnclipRatio);
        changed |= ClampSetting(settings.PaddleTextRecScoreThresh, 0.0, 1.0, 0.58, out var textRecScoreThresh);
        settings.PaddleTextDetThresh = textDetThresh;
        settings.PaddleTextDetBoxThresh = textDetBoxThresh;
        settings.PaddleTextDetUnclipRatio = textDetUnclipRatio;
        settings.PaddleTextRecScoreThresh = textRecScoreThresh;
        if (settings.EnablePaddleConfidenceFilter)
        {
            // COMPAT: Keep one confidence gate path to avoid double-filtering with text_rec_score_thresh.
            settings.EnablePaddleConfidenceFilter = false;
            changed = true;
        }

        var pipelineVersion = (settings.PaddleVlPipelineVersion ?? string.Empty).Trim();
        if (!string.Equals(pipelineVersion, "v1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(pipelineVersion, "v1.5", StringComparison.OrdinalIgnoreCase))
        {
            settings.PaddleVlPipelineVersion = "v1.5";
            changed = true;
        }
        else
        {
            settings.PaddleVlPipelineVersion = string.Equals(pipelineVersion, "v1", StringComparison.OrdinalIgnoreCase)
                ? "v1"
                : "v1.5";
        }

        if (settings.PaddleVlMaxPixels.HasValue && settings.PaddleVlMaxPixels.Value <= 0)
        {
            settings.PaddleVlMaxPixels = null;
            changed = true;
        }

        if (settings.PaddleVlLayoutThreshold.HasValue)
        {
            var clamped = Math.Clamp(settings.PaddleVlLayoutThreshold.Value, 0.0, 1.0);
            if (Math.Abs(clamped - settings.PaddleVlLayoutThreshold.Value) > 0.0001)
            {
                settings.PaddleVlLayoutThreshold = clamped;
                changed = true;
            }
        }

        if (settings.PaddleVlMaxNewTokens.HasValue)
        {
            var clamped = Math.Clamp(settings.PaddleVlMaxNewTokens.Value, 512, 4096);
            if (clamped != settings.PaddleVlMaxNewTokens.Value)
            {
                settings.PaddleVlMaxNewTokens = clamped;
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(settings.PaddleVlDevice))
        {
            settings.PaddleVlDevice = "gpu:0";
            changed = true;
        }
        else
        {
            settings.PaddleVlDevice = settings.PaddleVlDevice.Trim();
        }

        if (string.IsNullOrWhiteSpace(settings.PaddleVlPrecision))
        {
            settings.PaddleVlPrecision = "fp32";
            changed = true;
        }
        else
        {
            var precision = settings.PaddleVlPrecision.Trim().ToLowerInvariant();
            if (precision is not ("fp16" or "fp32"))
            {
                settings.PaddleVlPrecision = "fp32";
                changed = true;
            }
            else if (!string.Equals(settings.PaddleVlPrecision, precision, StringComparison.Ordinal))
            {
                settings.PaddleVlPrecision = precision;
                changed = true;
            }
        }

        return changed;
    }

    private static bool ClampSetting(double value, double min, double max, double fallback, out double normalized)
    {
        if (!double.IsFinite(value))
        {
            normalized = fallback;
            return true;
        }

        var clamped = Math.Clamp(value, min, max);
        if (Math.Abs(clamped - value) < 0.0001)
        {
            normalized = clamped;
            return false;
        }

        normalized = clamped;
        return true;
    }

    private static bool ClampSetting(int value, int min, int max, int fallback, out int normalized)
    {
        if (value < min || value > max)
        {
            normalized = fallback;
            return true;
        }

        normalized = value;
        return false;
    }

    private static ModifierKeys ParseModifiers(string value)
    {
        return Enum.TryParse(value, true, out ModifierKeys parsed) ? parsed : ModifierKeys.None;
    }
}
