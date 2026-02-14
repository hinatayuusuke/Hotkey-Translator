namespace Hotkey_Translator.Services.Settings.FeatureSettings;

internal readonly record struct SceneFeatureSettings(
    bool EnableSceneChangeAutoHide,
    bool EnableSceneChangeAutoTranslate,
    bool EnableSceneChangeSemanticGate,
    int SceneChangeWatchIntervalMs,
    bool EnableSceneChangeQuietWindow,
    int SceneChangeQuietWindowMs);
