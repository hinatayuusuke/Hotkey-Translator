using System;

namespace Hotkey_Translator.Services.Capture;

internal readonly record struct ProviderRuntimeState(
    int BlackCount,
    DateTimeOffset? CooldownUntil,
    string? LastFailureReason);
