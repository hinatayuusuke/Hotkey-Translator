using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Hook.Contracts;

internal sealed record GraphicsHookAttachRequest(
    int Pid,
    GraphicsHookApiKind Api,
    int CaptureFpsLimit,
    bool EnableOverlay,
    uint ConfigFlags);

internal sealed record GraphicsHookDetachRequest(
    int Pid);

internal sealed record GraphicsHookShutdownRequest();

internal sealed record GraphicsHookCommandEnvelope(
    string Type,
    object Payload);
