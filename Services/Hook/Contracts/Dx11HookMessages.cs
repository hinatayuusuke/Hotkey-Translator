namespace Hotkey_Translator.Services.Hook.Contracts;

internal sealed record Dx11HookAttachRequest(
    int Pid,
    int CaptureFpsLimit,
    bool EnableOverlay);

internal sealed record Dx11HookDetachRequest(
    int Pid);

internal sealed record Dx11HookOverlayUpdateRequest(
    int Pid,
    int Count,
    string RectsB64);

internal readonly record struct Dx11HookOverlayRect(
    float X,
    float Y,
    float W,
    float H,
    uint Argb,
    uint Thickness);

internal sealed record Dx11HookCommandEnvelope(
    string Type,
    object Payload);
