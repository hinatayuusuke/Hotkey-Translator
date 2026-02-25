namespace Hotkey_Translator.Services.Hook.Contracts;

internal sealed record Dx11HookAttachRequest(
    int Pid,
    int CaptureFpsLimit,
    bool EnableOverlay);

internal sealed record Dx11HookDetachRequest(
    int Pid);

internal sealed record Dx11HookShutdownRequest();

internal sealed record Dx11HookCommandEnvelope(
    string Type,
    object Payload);
