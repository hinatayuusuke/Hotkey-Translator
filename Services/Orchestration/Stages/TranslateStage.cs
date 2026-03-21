using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Orchestration.Stages;

internal sealed class TranslateStage
{
    private readonly NormalizationService _normalizationService;
    private readonly TranslationTextNormalizer _translationTextNormalizer;
    private readonly CacheRepository _cacheRepository;
    private readonly CacheKeyBuilder _cacheKeyBuilder;
    private readonly TranslationFallbackService _translationService;
    private readonly AppLogger _logger;
    private readonly UserGlossaryService _userGlossaryService;
    private readonly Dictionary<string, string> _lastTranslations = new(StringComparer.Ordinal);

    public TranslateStage(
        NormalizationService normalizationService,
        TranslationTextNormalizer translationTextNormalizer,
        CacheRepository cacheRepository,
        CacheKeyBuilder cacheKeyBuilder,
        TranslationFallbackService translationService,
        UserGlossaryService userGlossaryService,
        AppLogger logger)
    {
        _normalizationService = normalizationService;
        _translationTextNormalizer = translationTextNormalizer;
        _cacheRepository = cacheRepository;
        _cacheKeyBuilder = cacheKeyBuilder;
        _translationService = translationService;
        _userGlossaryService = userGlossaryService;
        _logger = logger;
    }

    public async Task<Dictionary<int, string>> ExecuteAsync(
        IReadOnlyList<ReadingUnit> units,
        IReadOnlySet<int> changedUnitIds,
        AppSettings settings,
        ForceRunOptions options,
        CancellationToken cancellationToken,
        Action? onTranslationStarted = null,
        Action? onTranslationCompleted = null)
    {
        if (options.SkipTranslation)
        {
            return new Dictionary<int, string>();
        }

        var translations = new Dictionary<int, string>();
        var pending = new List<PendingTranslation>();
        var pendingNormalized = new HashSet<string>(StringComparer.Ordinal);
        var glossaryScope = _userGlossaryService.BuildGlossaryScope(settings);

        foreach (var unit in units)
        {
            var translationSourceText = _translationTextNormalizer.NormalizeForTranslation(unit.Text, settings.SourceLanguage);
            var glossaryPrepared = _userGlossaryService.Prepare(translationSourceText, settings);
            var normalized = _normalizationService.Normalize(translationSourceText);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var normalizedKey = BuildLastTranslationKey(glossaryScope, normalized);
            var key = _cacheKeyBuilder.Build(settings, normalized);
            if (!options.SkipTranslationCache)
            {
                var cached = await _cacheRepository.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(cached))
                {
                    translations[unit.Id] = cached;
                    _lastTranslations[normalizedKey] = cached;
                    continue;
                }

                if (_lastTranslations.TryGetValue(normalizedKey, out var last))
                {
                    translations[unit.Id] = last;
                    continue;
                }
            }

            if (changedUnitIds.Contains(unit.Id) && pendingNormalized.Add(normalizedKey))
            {
                pending.Add(new PendingTranslation(unit.Id, translationSourceText, glossaryPrepared, normalizedKey, key));
            }
        }

        if (pending.Count == 0)
        {
            _logger.Info($"Translation skipped: no pending items (changed {changedUnitIds.Count}, total {units.Count}).");
            return translations;
        }

        _logger.Info($"Translation pending: {pending.Count} items.");
        var pendingTexts = pending.Select(item => item.Prepared.ProtectedSourceText).ToList();
        var glossaryHitCount = pending.Sum(item => item.Prepared.HitCount);
        if (glossaryHitCount > 0)
        {
            _logger.Info($"glossary_prepare hits={glossaryHitCount} items={pending.Count}.");
        }
        _logger.Info(BuildTranslationPayloadLog(pending));
        onTranslationStarted?.Invoke();
        IReadOnlyDictionary<string, string> results;
        try
        {
            results = await _translationService.TranslateAsync(pendingTexts, settings, options, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            onTranslationCompleted?.Invoke();
        }

        foreach (var item in pending)
        {
            if (!results.TryGetValue(item.Prepared.ProtectedSourceText, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            var restoreResult = _userGlossaryService.Restore(translated, item.Prepared);
            if (restoreResult.UnresolvedCount > 0)
            {
                _logger.Info($"glossary_restore miss unit={item.UnitId} unresolved={restoreResult.UnresolvedCount}.");
            }

            if (restoreResult.RestoredCount > 0)
            {
                _logger.Info($"glossary_restore restored unit={item.UnitId} count={restoreResult.RestoredCount}.");
            }

            await _cacheRepository.SaveAsync(item.CacheKey, restoreResult.Text, cancellationToken).ConfigureAwait(false);
            _lastTranslations[item.NormalizedKey] = restoreResult.Text;
            translations[item.UnitId] = restoreResult.Text;
        }

        if (options.SkipTranslationCache)
        {
            return translations;
        }

        foreach (var unit in units)
        {
            var translationSourceText = _translationTextNormalizer.NormalizeForTranslation(unit.Text, settings.SourceLanguage);
            var normalized = _normalizationService.Normalize(translationSourceText);
            var normalizedKey = BuildLastTranslationKey(glossaryScope, normalized);
            if (_lastTranslations.TryGetValue(normalizedKey, out var translated))
            {
                translations[unit.Id] = translated;
            }
        }

        return translations;
    }

    private static string BuildTranslationPayloadLog(IReadOnlyList<PendingTranslation> pending)
    {
        const int maxPreviewItems = 10;
        const int maxPreviewChars = 180;

        var builder = new StringBuilder();
        builder.Append("Translation request payload preview: ");
        var previewCount = Math.Min(maxPreviewItems, pending.Count);
        for (var i = 0; i < previewCount; i++)
        {
            var item = pending[i];
            var (leadingSpaces, trailingSpaces, maxConsecutiveSpaces) = GetSpaceStats(item.SourceText);
            var textPreview = ToVisiblePreview(item.SourceText, maxPreviewChars, out var truncated);
            if (i > 0)
            {
                builder.Append(" | ");
            }

            // WHY: Make spacing and line breaks explicit to diagnose vertical reading-unit text shaping before translation.
            builder.Append(
                $"unit={item.UnitId}, len={item.SourceText.Length}, leadSp={leadingSpaces}, trailSp={trailingSpaces}, maxSpRun={maxConsecutiveSpaces}, text=\"{textPreview}");
            if (truncated)
            {
                builder.Append("...(truncated)");
            }
            builder.Append('"');
            if (!string.Equals(item.SourceText, item.Prepared.ProtectedSourceText, StringComparison.Ordinal))
            {
                var protectedPreview = ToVisiblePreview(item.Prepared.ProtectedSourceText, maxPreviewChars, out var protectedTruncated);
                builder.Append($", protected=\"{protectedPreview}");
                if (protectedTruncated)
                {
                    builder.Append("...(truncated)");
                }
                builder.Append('"');
            }
        }

        if (pending.Count > previewCount)
        {
            builder.Append($" | ... {pending.Count - previewCount} more item(s)");
        }

        return builder.ToString();
    }

    private static (int Leading, int Trailing, int MaxRun) GetSpaceStats(string text)
    {
        var leading = 0;
        while (leading < text.Length && text[leading] == ' ')
        {
            leading++;
        }

        var trailing = 0;
        var trailingIndex = text.Length - 1;
        while (trailingIndex >= 0 && text[trailingIndex] == ' ')
        {
            trailing++;
            trailingIndex--;
        }

        var maxRun = 0;
        var currentRun = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ' ')
            {
                currentRun++;
                if (currentRun > maxRun)
                {
                    maxRun = currentRun;
                }
            }
            else
            {
                currentRun = 0;
            }
        }

        return (leading, trailing, maxRun);
    }

    private static string ToVisiblePreview(string text, int maxChars, out bool truncated)
    {
        var escaped = text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal)
            .Replace(" ", "<sp>", StringComparison.Ordinal);

        if (escaped.Length <= maxChars)
        {
            truncated = false;
            return escaped;
        }

        truncated = true;
        return escaped[..maxChars];
    }

    private static string BuildLastTranslationKey(string glossaryScope, string normalized)
    {
        return $"{glossaryScope}_{normalized}";
    }

    private sealed record PendingTranslation(
        int UnitId,
        string SourceText,
        UserGlossaryService.GlossaryPreparedText Prepared,
        string NormalizedKey,
        string CacheKey);
}
