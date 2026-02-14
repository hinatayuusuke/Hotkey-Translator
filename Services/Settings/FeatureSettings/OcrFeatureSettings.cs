using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.FeatureSettings;

internal readonly record struct OcrFeatureSettings(
    OcrEngineKind OcrEngine,
    bool EnablePaddleConfidenceFilter,
    double PaddleTextDetThresh,
    double PaddleTextDetBoxThresh,
    double PaddleTextDetUnclipRatio,
    double PaddleTextRecScoreThresh);
