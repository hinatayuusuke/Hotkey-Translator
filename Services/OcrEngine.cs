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
    private readonly VisionGeometryHybridAligner _visionGeometryHybridAligner;
    private readonly AppLogger? _logger;

    public OcrEngine(HttpClient httpClient, AppLogger? logger = null)
    {
        _logger = logger;
        _winRtProvider = new WinRtOcrProvider(logger);
        _paddleProvider = new PaddleGrpcOcrProvider(logger);
        _paddleVlProvider = new PaddleVlGrpcOcrProvider(logger);
        _ndlProvider = new NdlGrpcOcrProvider(logger);
        _visionLlmProvider = new VisionLlmGrpcOcrProvider(logger);
        _visionGeometryHybridAligner = new VisionGeometryHybridAligner(logger);
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
                _logger?.Info("OCR engine: VisionLLM.");
                var visionResult = await _visionLlmProvider.RecognizeAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
                if (!settings.EnableVisionGeometryHybridOcr)
                {
                    return visionResult;
                }

                try
                {
                    var geometryResult = await RecognizeVisionGeometryAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
                    if (geometryResult is null)
                    {
                        _logger?.Info("stage=vision_geometry_hybrid event=geometry_skip reason=not_configured.");
                        return visionResult;
                    }

                    var aligned = _visionGeometryHybridAligner.Align(
                        geometryResult.Lines,
                        visionResult.Lines,
                        bitmap.Width,
                        bitmap.Height,
                        settings);
                    if (aligned.Lines.Count > 0)
                    {
                        return new OcrResultModel(aligned.Lines, bitmap.Width, bitmap.Height);
                    }

                    _logger?.Info("stage=vision_geometry_hybrid event=fallback reason=no_output.");
                    return geometryResult.Lines.Count > 0 ? geometryResult : visionResult;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception geometryEx)
                {
                    _logger?.Error(geometryEx, "Vision geometry helper failed; using VisionLLM synthetic lines.");
                    return visionResult;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try
                {
                    if (settings.EnableVisionGeometryHybridOcr)
                    {
                        var geometryResult = await RecognizeVisionGeometryAsync(bitmap, settings, cancellationToken).ConfigureAwait(false);
                        if (geometryResult is not null && geometryResult.Lines.Count > 0)
                        {
                            _logger?.Error(ex, "VisionLLM OCR failed; using geometry OCR fallback.");
                            return geometryResult;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception geometryEx)
                {
                    _logger?.Error(geometryEx, "Geometry fallback after VisionLLM failure also failed.");
                }

                _logger?.Error(ex, "VisionLLM OCR failed; falling back to WinRT.");
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

        if (_ndlProvider is IDisposable ndlDisposable)
        {
            ndlDisposable.Dispose();
        }

        if (_visionLlmProvider is IDisposable visionDisposable)
        {
            visionDisposable.Dispose();
        }
    }

    private async Task<OcrResultModel?> RecognizeVisionGeometryAsync(Bitmap bitmap, AppSettings settings, CancellationToken cancellationToken)
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
            _ => null
        };
    }
}
