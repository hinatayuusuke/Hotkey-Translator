using System;
using System.Drawing;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services;

public sealed class OcrEngine
{
    private readonly IOcrProvider _winRtProvider;
    private readonly IOcrProvider _paddleProvider;
    private readonly IOcrProvider _paddleVllmProvider;
    private readonly IOcrProvider _florenceProvider;
    private readonly AppLogger? _logger;

    public OcrEngine(HttpClient httpClient, AppLogger? logger = null)
    {
        _logger = logger;
        _winRtProvider = new WinRtOcrProvider(logger);
        _paddleProvider = new PaddleGrpcOcrProvider(logger);
        _paddleVllmProvider = new PaddleVllmOcrProvider(httpClient, logger);
        _florenceProvider = new FlorenceOcrProvider(logger);
    }

    public async Task<OcrResultModel> RecognizeAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (settings.OcrEngine == OcrEngineKind.Florence2)
        {
            try
            {
                _logger?.Info("OCR engine: Florence-2.");
                return await _florenceProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Florence-2 OCR failed; falling back to WinRT.");
            }
        }

        if (settings.OcrEngine == OcrEngineKind.PaddleVllm)
        {
            try
            {
                _logger?.Info("OCR engine: PaddleOCR-VL (vLLM).");
                return await _paddleVllmProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "PaddleOCR-VL (vLLM) failed; falling back to WinRT.");
            }
        }

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

        _logger?.Info("OCR engine: WinRT.");
        return await _winRtProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
    }
}
