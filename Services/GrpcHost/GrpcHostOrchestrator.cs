using System;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.GrpcHost;

internal sealed class GrpcHostOrchestrator
{
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<bool, string?> _setBusyOverlay;
    private readonly Action<string> _showLoadFailure;

    public GrpcHostOrchestrator(
        Func<AppLogger?> loggerAccessor,
        Action<bool, string?> setBusyOverlay,
        Action<string> showLoadFailure)
    {
        _loggerAccessor = loggerAccessor;
        _setBusyOverlay = setBusyOverlay;
        _showLoadFailure = showLoadFailure;
    }

    public async Task<bool> EnsureHostsAsync(
        AppSettings settings,
        GrpcHostRegistry registry,
        CancellationToken cancellationToken)
    {
        var settingsChanged = false;
        foreach (var descriptor in registry.All)
        {
            if (!descriptor.ShouldLoad(settings))
            {
                continue;
            }

            foreach (var dependencyHostId in descriptor.StopBeforeStartHostIds)
            {
                if (!registry.TryGet(dependencyHostId, out var dependency))
                {
                    continue;
                }

                if (!dependency.IsRunning())
                {
                    continue;
                }

                _loggerAccessor()?.Info(
                    $"stage=grpc_host host={descriptor.HostId} event=stop_dependency dependency={dependencyHostId}.");
                dependency.Stop();
                dependency.OnStopped?.Invoke();
            }

            if (descriptor.IsRunning())
            {
                if (descriptor.HasDeferredConfigChange?.Invoke(settings) == true)
                {
                    descriptor.OnDeferredConfigDetected?.Invoke();
                }

                continue;
            }

            _setBusyOverlay(true, descriptor.BusyMessage(settings));
            try
            {
                await descriptor.StartAsync(settings, cancellationToken).ConfigureAwait(true);
                descriptor.OnStartSucceeded?.Invoke(settings);
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, descriptor.FailureLogMessage);
                descriptor.Stop();
                descriptor.OnStopped?.Invoke();
                descriptor.DisableOnFailure(settings);
                _showLoadFailure(descriptor.FailureUserMessage(settings));
                settingsChanged = true;
            }
            finally
            {
                _setBusyOverlay(false, null);
            }
        }

        return settingsChanged;
    }
}
