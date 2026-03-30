using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.GrpcHost;

internal sealed class GrpcHostDescriptor
{
    public required string HostId { get; init; }

    public required Func<AppSettings, bool> ShouldLoad { get; init; }

    public required Func<bool> IsRunning { get; init; }

    public required Func<AppSettings, CancellationToken, Task> StartAsync { get; init; }

    public required Action Stop { get; init; }

    public required Func<AppSettings, string> BusyMessage { get; init; }

    public required Action<AppSettings> DisableOnFailure { get; init; }

    public required string FailureLogMessage { get; init; }

    public required Func<AppSettings, string> FailureUserMessage { get; init; }

    public IReadOnlyList<string> StopBeforeStartHostIds { get; init; } = Array.Empty<string>();

    public Func<AppSettings, bool>? HasDeferredConfigChange { get; init; }

    public Action? OnDeferredConfigDetected { get; init; }

    public Action<AppSettings>? OnStartSucceeded { get; init; }

    public Action? OnStopped { get; init; }
}
