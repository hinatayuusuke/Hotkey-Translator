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
    private readonly AppLogger? _logger;

    public OcrEngine(HttpClient httpClient, AppLogger? logger = null)
    {
        _logger = logger;
        _winRtProvider = new WinRtOcrProvider(logger);
        _paddleProvider = new PaddleGrpcOcrProvider(logger);
        _paddleVlProvider = new PaddleVlGrpcOcrProvider(logger);
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

        _logger?.Info("OCR engine: WinRT.");
        return await _winRtProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
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
    }
}
