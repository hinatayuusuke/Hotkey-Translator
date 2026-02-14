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
