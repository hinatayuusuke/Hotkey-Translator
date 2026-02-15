using System;
using System.Collections.Generic;
using System.Windows;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services.Capture;

namespace Hotkey_Translator.Services;

public sealed class CaptureManager
{
    private readonly IReadOnlyList<ICaptureProvider> _providers;
    private readonly DxgiDuplicationProvider _dxgiProvider;
    private readonly CaptureTargetResolver _targetResolver;
    private readonly CaptureProviderSelector _providerSelector;
    private readonly CaptureAttemptCoordinator _attemptCoordinator;
    private readonly AppLogger _logger;

    public CaptureManager(FrameGate frameGate, AppLogger logger)
    {
        _logger = logger;
        _dxgiProvider = new DxgiDuplicationProvider(_logger);
        _providers = new ICaptureProvider[]
        {
            new GraphicsHookCaptureProvider(_logger),
            new WgcCaptureProvider(_logger),
            _dxgiProvider,
            new GdiCaptureProvider()
        };
        _targetResolver = new CaptureTargetResolver(_logger);
        _providerSelector = new CaptureProviderSelector(_providers);
        var policy = new DefaultCapturePolicy();
        var stateStore = new CaptureProviderStateStore();
        _attemptCoordinator = new CaptureAttemptCoordinator(frameGate, policy, stateStore, _logger);
    }

    public CaptureFrame Capture(AppSettings settings)
    {
        UpdateDxgiResidentState(settings);
        var now = DateTimeOffset.UtcNow;
        var order = _providerSelector.BuildProviderOrder(settings);
        var request = _targetResolver.BuildCaptureRequest(settings);
        var outcome = _attemptCoordinator.Execute(order, request, settings, now);
        if (outcome.Success)
        {
            return outcome.Frame!;
        }

        _logger.Info($"stage=capture event=all_failed summary=\"{outcome.Trace.BuildSummary()}\".");
        throw new InvalidOperationException("All capture providers failed.", outcome.LastError);
    }

    public Rect GetCaptureBounds(AppSettings settings)
    {
        UpdateDxgiResidentState(settings);
        var order = _providerSelector.BuildProviderOrder(settings);
        var request = _targetResolver.BuildCaptureRequest(settings);
        foreach (var provider in order)
        {
            if (!provider.IsEnabled(settings))
            {
                _logger.Info($"stage=capture event=bounds_skip provider={provider.Kind} reason=disabled.");
                continue;
            }

            if (provider.TryGetBounds(request, out var bounds))
            {
                _logger.Info($"stage=capture event=bounds_success provider={provider.Kind}.");
                return bounds;
            }

            _logger.Info($"stage=capture event=bounds_fail provider={provider.Kind}.");
        }

        _logger.Info("stage=capture event=bounds_empty.");
        return Rect.Empty;
    }

    private void UpdateDxgiResidentState(AppSettings settings)
    {
        var enableResident = settings.EnableDxgiCapture && settings.PreferredCaptureProvider == CaptureProviderKind.Dxgi;
        _dxgiProvider.SetResidentEnabled(enableResident);
    }
}
