using System.Collections.Generic;
using System.Linq;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Capture;

internal sealed class CaptureProviderSelector
{
    private readonly IReadOnlyList<ICaptureProvider> _providers;

    public CaptureProviderSelector(IReadOnlyList<ICaptureProvider> providers)
    {
        _providers = providers;
    }

    public IReadOnlyList<ICaptureProvider> BuildProviderOrder(AppSettings settings)
    {
        if (settings.EnableGraphicsHookPipeline)
        {
            var hook = _providers.FirstOrDefault(provider => provider.Kind == CaptureProviderKind.GraphicsHook);
            if (hook != null)
            {
                // WHY: When the hook pipeline is enabled, we always try it first and rely on existing fallback providers.
                var orderedHookFirst = new List<ICaptureProvider> { hook };
                orderedHookFirst.AddRange(_providers.Where(provider => provider.Kind != CaptureProviderKind.GraphicsHook));
                return orderedHookFirst;
            }
        }

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
}

