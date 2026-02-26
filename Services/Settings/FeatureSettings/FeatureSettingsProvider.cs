using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Settings.FeatureSettings;

internal sealed class FeatureSettingsProvider
{
    public HostFeatureSettings GetHost(AppSettings settings)
    {
        return new HostFeatureSettings(
            settings.OcrEngine,
            settings.EnablePaddleGrpcHost,
            settings.EnablePaddleVlGrpcHost,
            settings.EnableNdlGrpcHost,
            settings.EnableCTranslate2,
            settings.EnableLlamaCppTranslation);
    }

    public SceneFeatureSettings GetScene(AppSettings settings)
    {
        return new SceneFeatureSettings(
            settings.EnableSceneChangeAutoHide,
            settings.EnableSceneChangeAutoTranslate,
            settings.EnableSceneChangeSemanticGate,
            settings.SceneChangeWatchIntervalMs,
            settings.EnableSceneChangeQuietWindow,
            settings.SceneChangeQuietWindowMs);
    }

    public OcrFeatureSettings GetOcr(AppSettings settings)
    {
        return new OcrFeatureSettings(
            settings.OcrEngine,
            settings.EnablePaddleConfidenceFilter,
            settings.PaddleTextDetThresh,
            settings.PaddleTextDetBoxThresh,
            settings.PaddleTextDetUnclipRatio,
            settings.PaddleTextRecScoreThresh);
    }
}
