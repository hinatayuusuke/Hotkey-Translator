using System;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class ResourceHostFacade : IDisposable
{
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<bool, string?> _setBusyOverlay;
    private readonly Action<AppSettings, bool> _syncSettingsToView;
    private readonly Action<string> _showLoadFailure;
    private readonly SemaphoreSlim _resourceLoadGate = new(1, 1);
    private PaddleGrpcHost? _paddleGrpcHost;
    private PaddleVlGrpcHost? _paddleVlGrpcHost;
    private CTranslate2GrpcHost? _ct2GrpcHost;
    private LlamaGrpcHost? _llamaGrpcHost;
    private CTranslate2HostConfig? _ct2HostConfig;
    private LlamaHostConfig? _llamaHostConfig;

    public ResourceHostFacade(
        Func<AppLogger?> loggerAccessor,
        Action<bool, string?> setBusyOverlay,
        Action<AppSettings, bool> syncSettingsToView,
        Action<string> showLoadFailure)
    {
        _loggerAccessor = loggerAccessor;
        _setBusyOverlay = setBusyOverlay;
        _syncSettingsToView = syncSettingsToView;
        _showLoadFailure = showLoadFailure;
    }

    public bool IsPaddleVlRunning => _paddleVlGrpcHost is { IsRunning: true };

    public async Task<bool> EnsureResourceHostsAsync(AppSettings settings)
    {
        if (!ShouldLoadPaddle(settings) && !ShouldLoadPaddleVl(settings) &&
            !ShouldLoadCTranslate2(settings) && !ShouldLoadLlama(settings))
        {
            return false;
        }

        await _resourceLoadGate.WaitAsync().ConfigureAwait(true);
        var overlayShown = false;
        var settingsChanged = false;
        try
        {
            if (ShouldLoadPaddle(settings))
            {
                if (_paddleVlGrpcHost is { IsRunning: true })
                {
                    _loggerAccessor()?.Info("Stopping PaddleOCR-VL host before loading PaddleOCR.");
                    _paddleVlGrpcHost.Stop();
                }

                _paddleGrpcHost ??= new PaddleGrpcHost(_loggerAccessor());
                if (_paddleGrpcHost is not { IsRunning: true })
                {
                    _setBusyOverlay(true, "Loading PaddleOCR...");
                    overlayShown = true;
                    if (!await TryStartPaddleGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        settingsChanged = true;
                    }
                }
            }
            else if (ShouldLoadPaddleVl(settings))
            {
                if (_paddleGrpcHost is { IsRunning: true })
                {
                    _loggerAccessor()?.Info("Stopping PaddleOCR host before loading PaddleOCR-VL.");
                    _paddleGrpcHost.Stop();
                }

                _paddleVlGrpcHost ??= new PaddleVlGrpcHost(_loggerAccessor());
                if (_paddleVlGrpcHost is not { IsRunning: true })
                {
                    _setBusyOverlay(true, "Loading PaddleOCR-VL...");
                    overlayShown = true;
                    if (!await TryStartPaddleVlGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        settingsChanged = true;
                    }
                }
            }

            if (ShouldLoadCTranslate2(settings))
            {
                _ct2GrpcHost ??= new CTranslate2GrpcHost(_loggerAccessor());
                var config = BuildCTranslate2HostConfig(settings);
                if (_ct2GrpcHost is { IsRunning: true })
                {
                    if (_ct2HostConfig.HasValue && !_ct2HostConfig.Value.Equals(config))
                    {
                        // NOTE: Keep the host resident until restart; apply changes on next launch.
                        _loggerAccessor()?.Info("CTranslate2 settings changed; reload deferred until restart.");
                    }
                }
                else
                {
                    _setBusyOverlay(true, "Loading CTranslate2...");
                    overlayShown = true;
                    if (await TryStartCTranslate2GrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        _ct2HostConfig = config;
                    }
                    else
                    {
                        settingsChanged = true;
                    }
                }
            }

            if (ShouldLoadLlama(settings))
            {
                if (_ct2GrpcHost is { IsRunning: true })
                {
                    _loggerAccessor()?.Info("Stopping CTranslate2 host to avoid VRAM contention with Llama.");
                    _ct2GrpcHost.Stop();
                    _ct2HostConfig = null;
                }

                _llamaGrpcHost ??= new LlamaGrpcHost(_loggerAccessor());
                var config = BuildLlamaHostConfig(settings);
                if (_llamaGrpcHost is { IsRunning: true })
                {
                    if (_llamaHostConfig.HasValue && !_llamaHostConfig.Value.Equals(config))
                    {
                        // NOTE: Keep the host resident until restart; apply changes on next launch.
                        _loggerAccessor()?.Info("Llama settings changed; reload deferred until restart.");
                    }
                }
                else
                {
                    _setBusyOverlay(true, "Loading Llama.cpp...");
                    overlayShown = true;
                    if (await TryStartLlamaGrpcHostAsync(settings).ConfigureAwait(true))
                    {
                        _llamaHostConfig = config;
                    }
                    else
                    {
                        settingsChanged = true;
                    }
                }
            }
        }
        finally
        {
            if (overlayShown)
            {
                _setBusyOverlay(false, null);
            }

            _resourceLoadGate.Release();
        }

        return settingsChanged;
    }

    public void StopPaddle() => _paddleGrpcHost?.Stop();

    public void StopPaddleVl() => _paddleVlGrpcHost?.Stop();

    public void StopCTranslate2()
    {
        _ct2GrpcHost?.Stop();
        _ct2HostConfig = null;
    }

    public void StopLlama()
    {
        _llamaGrpcHost?.Stop();
        _llamaHostConfig = null;
    }

    public void StopAll()
    {
        StopPaddle();
        StopPaddleVl();
        StopCTranslate2();
        StopLlama();
    }

    public void Dispose()
    {
        StopAll();
        _resourceLoadGate.Dispose();
    }

    private static bool ShouldLoadPaddle(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.Paddle && settings.EnablePaddleGrpcHost;
    }

    private static bool ShouldLoadPaddleVl(AppSettings settings)
    {
        return settings.OcrEngine == OcrEngineKind.PaddleVllm && settings.EnablePaddleVlGrpcHost;
    }

    private static bool ShouldLoadCTranslate2(AppSettings settings)
    {
        // WHY: CTranslate2 translation path is retired; keep host disabled even if legacy settings remain.
        return false;
    }

    private static bool ShouldLoadLlama(AppSettings settings)
    {
        return settings.EnableLlamaCppTranslation;
    }

    private async Task<bool> TryStartPaddleGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _paddleGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Paddle gRPC host failed to start.");
            _paddleGrpcHost?.Stop();
            DisablePaddleOcr(settings);
            _showLoadFailure("Failed to load PaddleOCR. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartPaddleVlGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _paddleVlGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "PaddleOCR-VL gRPC host failed to start.");
            _paddleVlGrpcHost?.Stop();
            DisablePaddleVlOcr(settings);
            _showLoadFailure("Failed to load PaddleOCR-VL. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartCTranslate2GrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _ct2GrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "CTranslate2 gRPC host failed to start.");
            _ct2GrpcHost?.Stop();
            _ct2HostConfig = null;
            DisableCTranslate2(settings);
            _showLoadFailure("Failed to load CTranslate2. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private async Task<bool> TryStartLlamaGrpcHostAsync(AppSettings settings)
    {
        try
        {
            await _llamaGrpcHost!.StartAsync(settings, CancellationToken.None).ConfigureAwait(true);
            return true;
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Llama gRPC host failed to start.");
            _llamaGrpcHost?.Stop();
            _llamaHostConfig = null;
            DisableLlamaTranslation(settings);
            _showLoadFailure("Failed to load Llama.cpp. The setting has been turned OFF. See the logs for details.");
            return false;
        }
    }

    private void DisablePaddleOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisablePaddleVlOcr(AppSettings settings)
    {
        settings.OcrEngine = OcrEngineKind.WinRt;
        _syncSettingsToView(settings, false);
    }

    private void DisableCTranslate2(AppSettings settings)
    {
        settings.EnableCTranslate2 = false;
        _syncSettingsToView(settings, true);
    }

    private void DisableLlamaTranslation(AppSettings settings)
    {
        settings.EnableLlamaCppTranslation = false;
        _syncSettingsToView(settings, true);
    }

    private static CTranslate2HostConfig BuildCTranslate2HostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeCTranslate2Settings(settings);
        return new CTranslate2HostConfig(
            settings.CTranslate2Device,
            settings.CTranslate2Precision,
            settings.CTranslate2ModelId,
            settings.CTranslate2ModelDir,
            settings.CTranslate2GrpcEndpoint,
            settings.CTranslate2GrpcHost,
            settings.CTranslate2GrpcPort,
            settings.CTranslate2GrpcProjectDir,
            settings.CTranslate2GrpcUvPath,
            settings.CTranslate2GrpcServerScript);
    }

    private static LlamaHostConfig BuildLlamaHostConfig(AppSettings settings)
    {
        SettingsUiController.NormalizeLlamaSettings(settings);
        return new LlamaHostConfig(
            settings.LlamaHost,
            settings.LlamaPort,
            settings.LlamaContextSize,
            settings.LlamaGpuLayers,
            settings.LlamaThreads,
            settings.LlamaParallel,
            settings.LlamaBatchSize,
            settings.LlamaMaxTokens,
            settings.LlamaTemperature,
            settings.LlamaTopP,
            settings.LlamaTopK,
            settings.LlamaRepeatPenalty,
            settings.LlamaSelectedModelFileName,
            settings.LlamaGrpcEndpoint,
            settings.LlamaGrpcHost,
            settings.LlamaGrpcPort,
            settings.LlamaGrpcProjectDir,
            settings.LlamaGrpcUvPath,
            settings.LlamaGrpcServerScript);
    }

    private readonly record struct CTranslate2HostConfig(
        string Device,
        string Precision,
        string ModelId,
        string? ModelDir,
        string Endpoint,
        string Host,
        int Port,
        string ProjectDir,
        string UvPath,
        string ServerScript);

    private readonly record struct LlamaHostConfig(
        string Host,
        int Port,
        int ContextSize,
        int GpuLayers,
        int Threads,
        int Parallel,
        int BatchSize,
        int MaxTokens,
        double Temperature,
        double TopP,
        int TopK,
        double RepeatPenalty,
        string ModelFileName,
        string Endpoint,
        string GrpcHost,
        int GrpcPort,
        string ProjectDir,
        string UvPath,
        string ServerScript);
}
