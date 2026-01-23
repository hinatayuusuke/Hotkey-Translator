namespace Hotkey_Translator.Models;

public enum OcrEngineKind
{
    // COMPAT: Persisted to settings.json; keep numeric values stable.
    WinRt = 0,
    Paddle = 1,
    PaddleVllm = 2,
    Florence2 = 3
}
