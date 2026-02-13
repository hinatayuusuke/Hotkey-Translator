using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class CaptureManager
{
    private readonly IReadOnlyList<ICaptureProvider> _providers;
    private readonly DxgiDuplicationProvider _dxgiProvider;
    private readonly WindowBindingService _windowBindingService = new();
    private readonly Dictionary<CaptureProviderKind, ProviderState> _states = new();
    private readonly FrameGate _frameGate;
    private readonly AppLogger _logger;
    private string? _captureTargetResolutionState;

    public CaptureManager(FrameGate frameGate, AppLogger logger)
    {
        _frameGate = frameGate;
        _logger = logger;
        _dxgiProvider = new DxgiDuplicationProvider(_logger);
        _providers = new ICaptureProvider[]
        {
            new WgcCaptureProvider(_logger),
            _dxgiProvider,
            new GdiCaptureProvider()
        };
    }

    public CaptureFrame Capture(AppSettings settings)
    {
        UpdateDxgiResidentState(settings);
        var now = DateTimeOffset.UtcNow;
        var order = BuildProviderOrder(settings);
        var request = BuildCaptureRequest(settings);
        Exception? lastError = null;

        foreach (var provider in order)
        {
            if (!provider.IsEnabled(settings))
            {
                continue;
            }

            if (IsInCooldown(provider.Kind, now))
            {
                continue;
            }

            if (!provider.TryCapture(request, out var frame, out var error))
            {
                lastError = error is null ? null : new InvalidOperationException(error);
                // WHY: DXGI wait timeout is often a transient "no new frame yet" state; cooldown here causes long blind spots.
                if (!(provider.Kind == CaptureProviderKind.Dxgi && IsDxgiWaitTimeout(error)))
                {
                    StartCooldown(provider.Kind, settings, now, error);
                }
                continue;
            }

            if (_frameGate.IsBlack(frame.Bitmap, settings, out var stats))
            {
                frame.IsBlack = true;
                var state = GetState(provider.Kind);
                state.BlackCount++;
                _states[provider.Kind] = state;

                _logger.Info($"Capture black frame via {provider.Kind} (mean {stats.Mean:0.0}, var {stats.Variance:0.0}, count {state.BlackCount}).");

                if (state.BlackCount >= settings.BlackFrameThreshold)
                {
                    StartCooldown(provider.Kind, settings, now, "Black frame threshold exceeded.");
                    frame.Dispose();
                    continue;
                }
            }
            else
            {
                ResetBlackCount(provider.Kind);
            }

            return frame;
        }

        throw new InvalidOperationException("All capture providers failed.", lastError);
    }

    private static bool IsDxgiWaitTimeout(string? error)
    {
        return string.Equals(error, "DXGI timed out waiting for a new frame.", StringComparison.Ordinal);
    }

    public Rect GetCaptureBounds(AppSettings settings)
    {
        UpdateDxgiResidentState(settings);
        var order = BuildProviderOrder(settings);
        var request = BuildCaptureRequest(settings);
        foreach (var provider in order)
        {
            if (!provider.IsEnabled(settings))
            {
                continue;
            }

            if (provider.TryGetBounds(request, out var bounds))
            {
                return bounds;
            }
        }

        return Rect.Empty;
    }

    private IReadOnlyList<ICaptureProvider> BuildProviderOrder(AppSettings settings)
    {
        var preferred = _providers.FirstOrDefault(provider => provider.Kind == settings.PreferredCaptureProvider);
        if (preferred is null)
        {
            return _providers.ToList();
        }

        if (settings.CaptureProviderMode == CaptureProviderMode.Fixed)
        {
            return new List<ICaptureProvider> { preferred };
        }

        var ordered = new List<ICaptureProvider> { preferred };
        ordered.AddRange(_providers.Where(provider => provider.Kind != settings.PreferredCaptureProvider));
        return ordered;
    }

    private ProviderState GetState(CaptureProviderKind kind)
    {
        return _states.TryGetValue(kind, out var state) ? state : default;
    }

    private CaptureRequest BuildCaptureRequest(AppSettings settings)
    {
        var request = new CaptureRequest(settings.CaptureMode, null);
        if (settings.CaptureMode != CaptureMode.ActiveWindow || !settings.EnableFixedCaptureWindow)
        {
            TrackCaptureTargetResolution(null);
            return request;
        }

        if (_windowBindingService.TryResolveWindowHandle(settings, out var hwnd, out var reason))
        {
            TrackCaptureTargetResolution($"Fixed capture target resolved: hwnd=0x{hwnd.ToInt64():X}.");
            return request with { TargetWindowHandle = hwnd };
        }

        TrackCaptureTargetResolution($"Fixed capture target invalid; fallback to active window. Reason: {reason ?? "unknown"}.");
        return request;
    }

    private void TrackCaptureTargetResolution(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _captureTargetResolutionState = null;
            return;
        }

        if (string.Equals(_captureTargetResolutionState, message, StringComparison.Ordinal))
        {
            return;
        }

        _captureTargetResolutionState = message;
        _logger.Info(message);
    }

    private void UpdateDxgiResidentState(AppSettings settings)
    {
        var enableResident = settings.EnableDxgiCapture && settings.PreferredCaptureProvider == CaptureProviderKind.Dxgi;
        _dxgiProvider.SetResidentEnabled(enableResident);
    }

    private bool IsInCooldown(CaptureProviderKind kind, DateTimeOffset now)
    {
        if (!_states.TryGetValue(kind, out var state))
        {
            return false;
        }

        return state.CooldownUntil.HasValue && state.CooldownUntil.Value > now;
    }

    private void ResetBlackCount(CaptureProviderKind kind)
    {
        if (_states.TryGetValue(kind, out var state))
        {
            state.BlackCount = 0;
            _states[kind] = state;
        }
    }

    private void StartCooldown(CaptureProviderKind kind, AppSettings settings, DateTimeOffset now, string? reason)
    {
        var state = GetState(kind);
        state.BlackCount = 0;
        state.CooldownUntil = now.AddSeconds(Math.Max(1, settings.ProviderCooldownSeconds));
        _states[kind] = state;
        _logger.Info($"Capture provider {kind} cooldown until {state.CooldownUntil:HH:mm:ss}. Reason: {reason ?? "unknown"}.");
    }

    private struct ProviderState
    {
        public int BlackCount { get; set; }
        public DateTimeOffset? CooldownUntil { get; set; }
    }
}
