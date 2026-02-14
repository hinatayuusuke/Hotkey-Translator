using System;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class DefaultCapturePolicy : ICapturePolicy
{
    public bool IsInCooldown(ProviderRuntimeState state, DateTimeOffset now)
    {
        return state.CooldownUntil.HasValue && state.CooldownUntil.Value > now;
    }

    public bool ShouldStartCooldown(
        CaptureProviderKind providerKind,
        string? error,
        bool isBlackFrame,
        AppSettings settings)
    {
        if (isBlackFrame)
        {
            return true;
        }

        // WHY: DXGI wait timeout is often a transient "no new frame yet" state; cooldown here causes long blind spots.
        return !(providerKind == CaptureProviderKind.Dxgi && IsDxgiWaitTimeout(error));
    }

    public bool IsBlackThresholdExceeded(int blackCount, AppSettings settings)
    {
        return blackCount >= settings.BlackFrameThreshold;
    }

    public DateTimeOffset ResolveCooldownUntil(DateTimeOffset now, AppSettings settings)
    {
        return now.AddSeconds(Math.Max(1, settings.ProviderCooldownSeconds));
    }

    private static bool IsDxgiWaitTimeout(string? error)
    {
        return string.Equals(error, "DXGI timed out waiting for a new frame.", StringComparison.Ordinal);
    }
}
