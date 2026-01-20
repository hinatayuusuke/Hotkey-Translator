using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class PipelineOrchestrator
{
    private readonly CaptureManager _captureManager;
    private readonly OcrEngine _ocrEngine;
    private readonly OcrDiffService _ocrDiffService;
    private readonly PhashService _phashService;
    private readonly NormalizationService _normalizationService;
    private readonly CacheRepository _cacheRepository;
    private readonly CacheKeyBuilder _cacheKeyBuilder;
    private readonly GeminiClient _geminiClient;
    private readonly OverlayPresenter _overlayPresenter;
    private readonly SettingsService _settingsService;
    private readonly AppLogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _lastTranslations = new(StringComparer.Ordinal);
    private IReadOnlyList<OverlayItem> _lastOverlayItems = Array.Empty<OverlayItem>();
    private ulong? _lastHash;

    public PipelineOrchestrator(
        CaptureManager captureManager,
        OcrEngine ocrEngine,
        OcrDiffService ocrDiffService,
        PhashService phashService,
        NormalizationService normalizationService,
        CacheRepository cacheRepository,
        CacheKeyBuilder cacheKeyBuilder,
        GeminiClient geminiClient,
        OverlayPresenter overlayPresenter,
        SettingsService settingsService,
        AppLogger logger)
    {
        _captureManager = captureManager;
        _ocrEngine = ocrEngine;
        _ocrDiffService = ocrDiffService;
        _phashService = phashService;
        _normalizationService = normalizationService;
        _cacheRepository = cacheRepository;
        _cacheKeyBuilder = cacheKeyBuilder;
        _geminiClient = geminiClient;
        _overlayPresenter = overlayPresenter;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settingsService.Settings;
            _ocrDiffService.IouThreshold = settings.OcrIouThreshold;

            _overlayPresenter.Hide();
            using var frame = _captureManager.Capture(settings);

            if (frame.IsBlack)
            {
                _logger.Info($"Black frame detected from {frame.ProviderKind}. Keeping last overlay.");
                _overlayPresenter.ShowLast();
                return;
            }

            RememberPreferredProvider(settings, frame.ProviderKind);
            var roiScreen = GetRoiBounds(settings, frame.Bounds);
            if (roiScreen.IsEmpty)
            {
                _logger.Info("ROI is outside capture bounds.");
                _overlayPresenter.ShowLast();
                return;
            }

            var roiInFrame = new Rect(
                roiScreen.X - frame.Bounds.X,
                roiScreen.Y - frame.Bounds.Y,
                roiScreen.Width,
                roiScreen.Height);

            using var roiBitmap = BitmapHelper.Crop(frame.Bitmap, roiInFrame);

            if (settings.PhashThreshold >= 0)
            {
                var hash = _phashService.ComputeHash(roiBitmap);
                if (_lastHash.HasValue && _phashService.IsSimilar(hash, _lastHash.Value, settings.PhashThreshold))
                {
                    _logger.Info("pHash unchanged; keeping last overlay.");
                    _overlayPresenter.ShowLast();
                    return;
                }

                _lastHash = hash;
            }
            else
            {
                _lastHash = null;
            }

            var ocrResult = await _ocrEngine.RecognizeAsync(roiBitmap, settings.SourceLanguage, cancellationToken).ConfigureAwait(false);
            var mappedLines = ocrResult.Lines
                .Select(line => line with
                {
                    Rect = new Rect(
                        line.Rect.X + roiScreen.X,
                        line.Rect.Y + roiScreen.Y,
                        line.Rect.Width,
                        line.Rect.Height)
                })
                .ToList();

            if (mappedLines.Count == 0)
            {
                _logger.Info("OCR returned no lines.");
                _overlayPresenter.ShowLast();
                return;
            }

            var changedLines = _ocrDiffService.FilterChangedLines(mappedLines);
            var translations = await ResolveTranslationsAsync(mappedLines, changedLines, settings, cancellationToken).ConfigureAwait(false);

            var overlayItems = mappedLines
                .Select(line => new OverlayItem(GetOverlayText(line.Text, translations), line.Rect))
                .ToList();

            _lastOverlayItems = overlayItems;
            _overlayPresenter.Update(overlayItems);
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Pipeline canceled.");
            _overlayPresenter.ShowLast();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline failed.");
            _overlayPresenter.ShowLast();
        }
        finally
        {
            _overlayPresenter.Show();
            _gate.Release();
        }
    }

    private Rect GetRoiBounds(AppSettings settings, Rect frameBounds)
    {
        if (settings.NormalizedRoi is { } normalized && !normalized.IsEmpty)
        {
            return normalized.ToAbsolute(frameBounds);
        }

        if (settings.Roi is null || settings.Roi.Value.IsEmpty)
        {
            return frameBounds;
        }

        var absolute = Rect.Intersect(frameBounds, settings.Roi.Value.ToRect());
        if (!absolute.IsEmpty)
        {
            settings.NormalizedRoi = NormalizedRect.FromAbsolute(absolute, frameBounds);
            // NOTE: Fire-and-forget migration to normalized ROI for DPI-safe persistence.
            _ = _settingsService.SaveAsync();
        }

        return absolute;
    }

    private void RememberPreferredProvider(AppSettings settings, CaptureProviderKind providerKind)
    {
        if (settings.PreferredCaptureProvider == providerKind)
        {
            return;
        }

        settings.PreferredCaptureProvider = providerKind;
        _logger.Info($"Preferred capture provider set to {providerKind}.");
        _ = _settingsService.SaveAsync();
    }

    private async Task<Dictionary<string, string>> ResolveTranslationsAsync(
        IReadOnlyList<OcrLine> lines,
        IReadOnlyList<OcrLine> changedLines,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        var changedSet = new HashSet<OcrLine>(changedLines);
        var pending = new List<PendingTranslation>();
        var pendingNormalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var normalized = _normalizationService.Normalize(line.Text);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            var key = _cacheKeyBuilder.Build(settings, normalized);
            var cached = await _cacheRepository.TryGetAsync(key, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(cached))
            {
                translations[line.Text] = cached;
                _lastTranslations[normalized] = cached;
                continue;
            }

            if (_lastTranslations.TryGetValue(normalized, out var last))
            {
                translations[line.Text] = last;
                continue;
            }

            if (changedSet.Contains(line) && pendingNormalized.Add(normalized))
            {
                pending.Add(new PendingTranslation(line.Text, normalized, key));
            }
        }

        if (pending.Count == 0)
        {
            return translations;
        }

        var pendingTexts = pending.Select(item => item.SourceText).ToList();
        var results = await _geminiClient.TranslateAsync(pendingTexts, settings, cancellationToken).ConfigureAwait(false);
        foreach (var item in pending)
        {
            if (!results.TryGetValue(item.SourceText, out var translated) || string.IsNullOrWhiteSpace(translated))
            {
                continue;
            }

            await _cacheRepository.SaveAsync(item.CacheKey, translated, cancellationToken).ConfigureAwait(false);
            _lastTranslations[item.Normalized] = translated;
        }

        foreach (var line in lines)
        {
            var normalized = _normalizationService.Normalize(line.Text);
            if (_lastTranslations.TryGetValue(normalized, out var translated))
            {
                translations[line.Text] = translated;
            }
        }

        return translations;
    }

    private static string GetOverlayText(string sourceText, Dictionary<string, string> translations)
    {
        return translations.TryGetValue(sourceText, out var translated) ? translated : sourceText;
    }

    private sealed record PendingTranslation(string SourceText, string Normalized, string CacheKey);
}
