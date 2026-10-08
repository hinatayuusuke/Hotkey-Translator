using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.GrpcHost;

internal sealed class GrpcHostOrchestrator
{
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<string> _onStarting;

    public GrpcHostOrchestrator(
        Func<AppLogger?> loggerAccessor,
        Action<string> onStarting)
    {
        _loggerAccessor = loggerAccessor;
        _onStarting = onStarting;
    }

    public async Task<ResourceHostLoadResult> EnsureHostsAsync(
        AppSettings settings,
        GrpcHostRegistry registry,
        CancellationToken cancellationToken,
        IReadOnlySet<string> suppressedHosts)
    {
        var settingsChanged = false;
        var failures = new List<HostLoadFailure>();
        foreach (var descriptor in registry.All)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(ResourceHostLoadStatus.Cancelled, settingsChanged, failures);
            if (!descriptor.ShouldLoad(settings) || suppressedHosts.Contains(descriptor.HostId))
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

            _onStarting(descriptor.BusyMessage(settings));
            try
            {
                await descriptor.StartAsync(settings, cancellationToken).ConfigureAwait(true);
                cancellationToken.ThrowIfCancellationRequested();
                descriptor.OnStartSucceeded?.Invoke(settings);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // NOTE: User cancellation must not disable the selected engine or masquerade as a load failure.
                descriptor.Stop();
                descriptor.OnStopped?.Invoke();
                return new(ResourceHostLoadStatus.Cancelled, settingsChanged, failures);
            }
            catch (Exception ex)
            {
                _loggerAccessor()?.Error(ex, descriptor.FailureLogMessage);
                var failure = descriptor.DescribeFailure?.Invoke(settings, ex)
                    ?? new HostLoadFailure(descriptor.HostId, "", null, HostLoadPhase.Environment,
                        HostLoadDiagnostics.Sanitize(ex.Message), "");
                descriptor.Stop();
                descriptor.OnStopped?.Invoke();
                descriptor.DisableOnFailure(settings);
                failures.Add(failure);
                settingsChanged = true;
            }
        }

        return new(failures.Count == 0 ? ResourceHostLoadStatus.Succeeded : ResourceHostLoadStatus.Failed,
            settingsChanged, failures);
    }
}
