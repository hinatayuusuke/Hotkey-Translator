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
        if (providerKind == CaptureProviderKind.Dxgi && IsDxgiWaitTimeout(error))
        {
            return false;
        }

        // WHY: GraphicsHook writer race is transient under heavy Present cadence. Cooldown would force WGC fallback
        // for several seconds and increase overlay self-capture risk.
        if (providerKind == CaptureProviderKind.GraphicsHook && IsGraphicsHookFrameUnstable(error))
        {
            return false;
        }

        return true;
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

    private static bool IsGraphicsHookFrameUnstable(string? error)
    {
        return string.Equals(error, "Frame was unstable (writer race).", StringComparison.Ordinal);
    }
}
