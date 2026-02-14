using System;
using System.Collections.Generic;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureProviderStateStore
{
    private readonly Dictionary<CaptureProviderKind, ProviderRuntimeState> _states = new();

    public ProviderRuntimeState Get(CaptureProviderKind kind)
    {
        return _states.TryGetValue(kind, out var state) ? state : default;
    }

    public int IncrementBlackCount(CaptureProviderKind kind)
    {
        var state = Get(kind);
        state = state with { BlackCount = state.BlackCount + 1 };
        _states[kind] = state;
        return state.BlackCount;
    }

    public void ResetBlackCount(CaptureProviderKind kind)
    {
        if (!_states.TryGetValue(kind, out var state))
        {
            return;
        }

        if (state.BlackCount == 0)
        {
            return;
        }

        _states[kind] = state with { BlackCount = 0 };
    }

    public DateTimeOffset StartCooldown(
        CaptureProviderKind kind,
        DateTimeOffset now,
        string? reason,
        AppSettings settings,
        ICapturePolicy policy)
    {
        var state = Get(kind);
        var cooldownUntil = policy.ResolveCooldownUntil(now, settings);
        state = state with
        {
            BlackCount = 0,
            CooldownUntil = cooldownUntil,
            LastFailureReason = reason
        };
        _states[kind] = state;
        return cooldownUntil;
    }

    public void RecordFailure(CaptureProviderKind kind, string? reason)
    {
        var state = Get(kind);
        _states[kind] = state with { LastFailureReason = reason };
    }
}
