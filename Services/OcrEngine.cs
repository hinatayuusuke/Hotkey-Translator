using System;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrEngine : IDisposable
{
    private readonly IOcrProvider _winRtProvider;
    private readonly IOcrProvider _paddleProvider;
    private readonly IOcrProvider _paddleVlProvider;
    private readonly IOcrProvider _ndlProvider;
    private readonly IOcrProvider _visionLlmProvider;
    private readonly IOcrProvider _oneOcrProvider;
    private readonly AppLogger? _logger;

    public OcrEngine(HttpClient httpClient, AppLogger? logger = null)
    {
        _logger = logger;
        _winRtProvider = new WinRtOcrProvider(logger);
        _paddleProvider = new PaddleGrpcOcrProvider(logger);
        _paddleVlProvider = new PaddleVlGrpcOcrProvider(logger);
        _ndlProvider = new NdlGrpcOcrProvider(logger);
        _visionLlmProvider = new VisionLlmGrpcOcrProvider(logger);
        _oneOcrProvider = new OneOcrProcessOcrProvider(logger);
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (settings.OcrEngine == OcrEngineKind.Paddle)
        {
            try
            {
                _logger?.Info("OCR engine: PaddleOCR.");
                return await _paddleProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Paddle OCR failed; falling back to WinRT.");
            }
        }
        else if (settings.OcrEngine == OcrEngineKind.PaddleVllm)
        {
            try
            {
                _logger?.Info("OCR engine: PaddleOCR-VL.");
                return await _paddleVlProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "PaddleOCR-VL failed; falling back to WinRT.");
            }
        }
        else if (settings.OcrEngine == OcrEngineKind.Ndl)
        {
            try
            {
                _logger?.Info("OCR engine: NDLOCR-Lite.");
                return await _ndlProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "NDLOCR-Lite failed; falling back to WinRT.");
            }
        }
        else if (settings.OcrEngine == OcrEngineKind.VisionLlm)
        {
            try
            {
                return await RecognizeVisionTextAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "VisionLLM OCR failed; falling back to WinRT.");
            }
        }
        else if (settings.OcrEngine == OcrEngineKind.OneOcr)
        {
            try
            {
                _logger?.Info("OCR engine: OneOCR.");
                return await _oneOcrProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "OneOCR failed; falling back to WinRT.");
            }
        }

        return await RecognizeWinRtAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_paddleProvider is IDisposable paddleDisposable)
        {
            paddleDisposable.Dispose();
        }

        if (_paddleVlProvider is IDisposable paddleVlDisposable)
        {
            paddleVlDisposable.Dispose();
        }

        if (_ndlProvider is IDisposable ndlDisposable)
        {
            ndlDisposable.Dispose();
        }

        if (_visionLlmProvider is IDisposable visionDisposable)
        {
            visionDisposable.Dispose();
        }

        if (_oneOcrProvider is IDisposable oneOcrDisposable)
        {
            oneOcrDisposable.Dispose();
        }
    }

    public async Task<OcrResultModel> RecognizeVisionTextAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        _logger?.Info("OCR engine: VisionLLM.");
        return await _visionLlmProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OcrResultModel> RecognizeWinRtAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        _logger?.Info("OCR engine: WinRT.");
        return await _winRtProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OcrResultModel?> RecognizeVisionGeometryAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        var provider = ResolveVisionGeometryProvider(settings);
        if (provider is null)
        {
            return null;
        }

        _logger?.Info($"stage=vision_geometry_hybrid event=geometry_begin engine={settings.VisionGeometryHybridBaseEngine}.");
        return await provider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
    }

    private IOcrProvider? ResolveVisionGeometryProvider(AppSettings settings)
    {
        if (settings.OcrEngine != OcrEngineKind.VisionLlm || !settings.EnableVisionGeometryHybridOcr)
        {
            return null;
        }

        return settings.VisionGeometryHybridBaseEngine switch
        {
            VisionGeometryHybridBaseEngineKind.WinRt => _winRtProvider,
            VisionGeometryHybridBaseEngineKind.Ndl => _ndlProvider,
            VisionGeometryHybridBaseEngineKind.Paddle => _paddleProvider,
            VisionGeometryHybridBaseEngineKind.OneOcr => _oneOcrProvider,
            _ => null
        };
    }
}
