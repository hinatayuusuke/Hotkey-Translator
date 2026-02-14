using System;

namespace Hotkey_Translator.Services.GrpcHost;

internal readonly record struct GrpcHostRestartPolicy(
    int MaxRestarts,
    TimeSpan Window,
    int RetryDelayMs = 500);
