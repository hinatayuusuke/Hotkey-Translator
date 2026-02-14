using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.GrpcHost;

internal interface IGrpcHostLifecycle
{
    bool IsRunning { get; }

    Task StartAsync(AppSettings settings, CancellationToken cancellationToken);

    void Stop();
}
