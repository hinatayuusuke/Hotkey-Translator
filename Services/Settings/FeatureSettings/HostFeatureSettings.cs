using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.FeatureSettings;

internal readonly record struct HostFeatureSettings(
    OcrEngineKind OcrEngine,
    bool EnablePaddleGrpcHost,
    bool EnablePaddleVlGrpcHost,
    bool EnableCTranslate2,
    bool EnableLlamaCppTranslation);
