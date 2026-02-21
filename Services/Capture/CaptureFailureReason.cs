namespace Hotkey_Translator.Services.Capture;

internal enum CaptureFailureReason
{
    None = 0,
    Disabled = 1,
    Cooldown = 2,
    CaptureError = 3,
    DxgiWaitTimeout = 4,
    BlackFrameThreshold = 5,
    HookFrameUnstable = 6,
}
