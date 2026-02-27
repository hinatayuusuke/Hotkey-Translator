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
    private readonly IReadOnlyList<string> _providerOrder;
    private readonly AppLogger? _logger;

    public TranslationFallbackService(IEnumerable<ITranslationProvider> providers, AppLogger? logger = null)
    {
        var orderedProviders = providers.ToList();
        _providers = orderedProviders.ToDictionary(provider => provider.Name, provider => provider, StringComparer.OrdinalIgnoreCase);
        _providerOrder = orderedProviders.Select(provider => provider.Name).ToList();
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>> TranslateAsync(
        IReadOnlyList<string> texts,
        AppSettings settings,
        ForceRunOptions options,
        CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return new Dictionary<string, string>();
        }

        if (options.ForceGeminiStrict)
        {
            // WHY: Strict hotkey requests a Gemini-only attempt for this run and must not fall back to other providers.
            if (!_providers.TryGetValue(TranslationProviderNames.Gemini, out var geminiProvider))
            {
                _logger?.Info("Translation provider skipped[forced strict]: Gemini (not registered).");
                return new Dictionary<string, string>();
            }

            if (!geminiProvider.IsEnabled(settings))
            {
                _logger?.Info("Translation provider skipped[forced strict]: Gemini (disabled).");
                return new Dictionary<string, string>();
            }

            try
            {
                _logger?.Info("Translation provider active[forced strict]: Gemini.");
                var result = await geminiProvider.TranslateAsync(texts, settings, cancellationToken).ConfigureAwait(false);
                if (result.Count > 0)
                {
                    return result;
                }

                _logger?.Info("Translation provider returned empty result[forced strict]: Gemini.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Translation provider failed[forced strict]: Gemini.");
            }

            return new Dictionary<string, string>();
        }

        var priority = NormalizePriority(settings.TranslationPriority, _providerOrder);
        _logger?.Info($"Translation provider order: {string.Join(" > ", priority)}.");
        for (var i = 0; i < priority.Count; i++)
        {
            var name = priority[i];
            if (!_providers.TryGetValue(name, out var provider))
            {
                _logger?.Info($"Translation provider skipped[{i + 1}/{priority.Count}]: {name} (not registered).");
                continue;
            }

            if (!provider.IsEnabled(settings))
            {
                _logger?.Info($"Translation provider skipped[{i + 1}/{priority.Count}]: {provider.Name} (disabled).");
                continue;
            }

            try
            {
                _logger?.Info($"Translation provider active[{i + 1}/{priority.Count}]: {provider.Name}.");
                var result = await provider.TranslateAsync(texts, settings, cancellationToken).ConfigureAwait(false);
                if (result.Count > 0)
                {
                    return result;
                }

                _logger?.Info($"Translation provider returned empty result[{i + 1}/{priority.Count}]: {provider.Name}.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Translation provider failed[{i + 1}/{priority.Count}]: {provider.Name}.");
            }
        }

        return new Dictionary<string, string>();
    }

    public static List<string> NormalizePriority(
        IReadOnlyList<string>? currentPriority,
        IReadOnlyList<string> registeredProviderNames)
    {
        var orderedRegistered = registeredProviderNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allowed = new HashSet<string>(orderedRegistered, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in currentPriority ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (!allowed.Contains(name))
            {
                continue;
            }

            if (existing.Add(name))
            {
                ordered.Add(name);
            }
        }

        foreach (var name in orderedRegistered)
        {
            if (existing.Add(name))
            {
                ordered.Add(name);
            }
        }

        return ordered;
    }
}
