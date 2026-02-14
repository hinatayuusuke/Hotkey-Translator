using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal interface ICapturePolicy
{
    bool IsInCooldown(ProviderRuntimeState state, DateTimeOffset now);

    bool ShouldStartCooldown(
        CaptureProviderKind providerKind,
        string? error,
        bool isBlackFrame,
        AppSettings settings);

    bool IsBlackThresholdExceeded(int blackCount, AppSettings settings);

    DateTimeOffset ResolveCooldownUntil(DateTimeOffset now, AppSettings settings);
}
