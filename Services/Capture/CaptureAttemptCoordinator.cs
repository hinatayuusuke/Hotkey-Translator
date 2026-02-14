using System;
using System.Collections.Generic;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureAttemptCoordinator
{
    private readonly FrameGate _frameGate;
    private readonly ICapturePolicy _policy;
    private readonly CaptureProviderStateStore _stateStore;
    private readonly AppLogger _logger;

    public CaptureAttemptCoordinator(
        FrameGate frameGate,
        ICapturePolicy policy,
        CaptureProviderStateStore stateStore,
        AppLogger logger)
    {
        _frameGate = frameGate;
        _policy = policy;
        _stateStore = stateStore;
        _logger = logger;
    }

    public CaptureExecutionOutcome Execute(
        IReadOnlyList<ICaptureProvider> orderedProviders,
        CaptureRequest request,
        AppSettings settings,
        DateTimeOffset now)
    {
        var attempts = new List<CaptureAttemptResult>(orderedProviders.Count);
        Exception? lastError = null;
        foreach (var provider in orderedProviders)
        {
            _logger.Info($"stage=capture event=attempt provider={provider.Kind}.");

            if (!provider.IsEnabled(settings))
            {
                _logger.Info($"stage=capture event=skip provider={provider.Kind} reason=disabled.");
                attempts.Add(new CaptureAttemptResult(provider.Kind, false, CaptureFailureReason.Disabled, "Provider disabled."));
                continue;
            }

            var state = _stateStore.Get(provider.Kind);
            if (_policy.IsInCooldown(state, now))
            {
                _logger.Info(
                    $"stage=capture event=skip provider={provider.Kind} reason=cooldown until={state.CooldownUntil:HH:mm:ss}.");
                attempts.Add(
                    new CaptureAttemptResult(
                        provider.Kind,
                        false,
                        CaptureFailureReason.Cooldown,
                        "Provider in cooldown.",
                        CooldownUntil: state.CooldownUntil));
                continue;
            }

            if (!provider.TryCapture(request, out var frame, out var error))
            {
                var reason = ClassifyCaptureError(provider.Kind, error);
                lastError = error is null ? null : new InvalidOperationException(error);
                var shouldStartCooldown = _policy.ShouldStartCooldown(provider.Kind, error, isBlackFrame: false, settings);
                if (shouldStartCooldown)
                {
                    var cooldownUntil = _stateStore.StartCooldown(provider.Kind, now, error, settings, _policy);
                    _logger.Info(
                        $"stage=capture event=fail provider={provider.Kind} reason={reason} cooldownUntil={cooldownUntil:HH:mm:ss} detail={error ?? "unknown"}.");
                    attempts.Add(
                        new CaptureAttemptResult(
                            provider.Kind,
                            false,
                            reason,
                            error,
                            CooldownUntil: cooldownUntil));
                }
                else
                {
                    _stateStore.RecordFailure(provider.Kind, error);
                    _logger.Info($"stage=capture event=fail provider={provider.Kind} reason={reason} detail={error ?? "unknown"}.");
                    attempts.Add(new CaptureAttemptResult(provider.Kind, false, reason, error));
                }

                continue;
            }

            if (_frameGate.IsBlack(frame.Bitmap, settings, out var stats))
            {
                frame.IsBlack = true;
                var blackCount = _stateStore.IncrementBlackCount(provider.Kind);
                _logger.Info(
                    $"stage=capture event=black provider={provider.Kind} mean={stats.Mean:0.0} var={stats.Variance:0.0} count={blackCount}.");

                if (_policy.IsBlackThresholdExceeded(blackCount, settings))
                {
                    const string reason = "Black frame threshold exceeded.";
                    var cooldownUntil = _stateStore.StartCooldown(provider.Kind, now, reason, settings, _policy);
                    _logger.Info(
                        $"stage=capture event=cooldown provider={provider.Kind} reason=black_threshold cooldownUntil={cooldownUntil:HH:mm:ss}.");
                    attempts.Add(
                        new CaptureAttemptResult(
                            provider.Kind,
                            false,
                            CaptureFailureReason.BlackFrameThreshold,
                            reason,
                            IsBlackFrame: true,
                            CooldownUntil: cooldownUntil));
                    frame.Dispose();
                    continue;
                }
            }
            else
            {
                _stateStore.ResetBlackCount(provider.Kind);
            }

            _logger.Info($"stage=capture event=success provider={provider.Kind} black={frame.IsBlack}.");
            attempts.Add(
                new CaptureAttemptResult(
                    provider.Kind,
                    true,
                    CaptureFailureReason.None,
                    IsBlackFrame: frame.IsBlack));
            return new CaptureExecutionOutcome(frame, lastError, new CaptureExecutionTrace(attempts));
        }

        return new CaptureExecutionOutcome(null, lastError, new CaptureExecutionTrace(attempts));
    }

    private static CaptureFailureReason ClassifyCaptureError(CaptureProviderKind providerKind, string? error)
    {
        if (providerKind == CaptureProviderKind.Dxgi &&
            string.Equals(error, "DXGI timed out waiting for a new frame.", StringComparison.Ordinal))
        {
            return CaptureFailureReason.DxgiWaitTimeout;
        }

        return CaptureFailureReason.CaptureError;
    }
}
