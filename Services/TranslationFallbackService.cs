using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class TranslationFallbackService
{
    private readonly Dictionary<string, ITranslationProvider> _providers;
    private readonly AppLogger? _logger;

    public TranslationFallbackService(IEnumerable<ITranslationProvider> providers, AppLogger? logger = null)
    {
        _providers = providers.ToDictionary(provider => provider.Name, provider => provider, StringComparer.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        var priority = NormalizePriority(settings);
        foreach (var name in priority)
        {
            if (!_providers.TryGetValue(name, out var provider))
            {
                _logger?.Info($"Translation provider skipped: {name} (not registered).");
                continue;
            }

            if (!provider.IsEnabled(settings))
            {
                _logger?.Info($"Translation provider skipped: {provider.Name} (disabled).");
                continue;
            }

            try
            {
                _logger?.Info($"Translation provider active: {provider.Name}.");
                var result = await provider.TranslateAsync(texts, settings, cancellationToken).ConfigureAwait(false);
                if (result.Count > 0)
                {
                    return result;
                }

                _logger?.Info($"Translation provider returned empty result: {provider.Name}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Translation provider failed: {provider.Name}.");
            }
        }

        return new Dictionary<string, string>();
    }

    private static IReadOnlyList<string> NormalizePriority(AppSettings settings)
    {
        var ordered = new List<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = settings.TranslationPriority ?? new List<string>();
        foreach (var name in current)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (existing.Add(name))
            {
                ordered.Add(name);
            }
        }

        foreach (var name in TranslationProviderNames.Defaults)
        {
            if (existing.Add(name))
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }
}
