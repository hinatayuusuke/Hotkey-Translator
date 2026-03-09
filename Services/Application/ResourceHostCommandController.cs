using System;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class ResourceHostCommandController
{
    private readonly ResourceHostFacade _resourceHostFacade;
    private readonly Func<bool> _isLoaded;
    private readonly Func<bool> _isRunInProgress;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<AppSettings, bool> _syncSettingsToView;
    private readonly Func<Task> _saveSettingsImmediatelyAsync;
    private readonly Action<bool, string?> _setBusyOverlay;
    private readonly Action<string> _appendLog;
    private readonly Action<string> _showLoadFailure;
    private readonly Func<AppLogger?> _loggerAccessor;

    public ResourceHostCommandController(
        ResourceHostFacade resourceHostFacade,
        Func<bool> isLoaded,
        Func<bool> isRunInProgress,
        Func<AppSettings> settingsAccessor,
        Action<AppSettings, bool> syncSettingsToView,
        Func<Task> saveSettingsImmediatelyAsync,
        Action<bool, string?> setBusyOverlay,
        Action<string> appendLog,
        Action<string> showLoadFailure,
        Func<AppLogger?> loggerAccessor)
    {
        _resourceHostFacade = resourceHostFacade;
        _isLoaded = isLoaded;
        _isRunInProgress = isRunInProgress;
        _settingsAccessor = settingsAccessor;
        _syncSettingsToView = syncSettingsToView;
        _saveSettingsImmediatelyAsync = saveSettingsImmediatelyAsync;
        _setBusyOverlay = setBusyOverlay;
        _appendLog = appendLog;
        _showLoadFailure = showLoadFailure;
        _loggerAccessor = loggerAccessor;
    }

    public async Task RestartLlamaCppAsync()
    {
        if (!_isLoaded())
        {
            return;
        }

        try
        {
            _setBusyOverlay(true, "Restarting Llama.cpp...");
            _resourceHostFacade.StopLlama();

            // WHY: Manual restart is expected to keep Llama enabled after this action.
            var settings = _settingsAccessor();
            settings.EnableLlamaCppTranslation = true;
            _syncSettingsToView(settings, true);

            await _saveSettingsImmediatelyAsync().ConfigureAwait(true);
            _appendLog("Llama.cpp restarted.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to restart Llama.cpp host.");
            _showLoadFailure("Failed to restart Llama.cpp. See the logs for details.");
        }
        finally
        {
            _setBusyOverlay(false, null);
        }
    }

    public async Task StopLlamaServerAsync()
    {
        if (!_isLoaded())
        {
            return;
        }

        try
        {
            _resourceHostFacade.StopLlama();

            // WHY: Keep persisted settings consistent with the explicit stop action.
            var settings = _settingsAccessor();
            settings.EnableLlamaCppTranslation = false;
            _syncSettingsToView(settings, true);

            await _saveSettingsImmediatelyAsync().ConfigureAwait(true);
            _appendLog("llama-server stopped.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to stop llama-server.");
            _showLoadFailure("Failed to stop llama-server. See the logs for details.");
        }
    }

    public async Task RestartPaddleOcrHostsAsync()
    {
        if (!_isLoaded())
        {
            return;
        }

        if (_isRunInProgress())
        {
            _appendLog("Restart skipped: OCR is running.");
            return;
        }

        try
        {
            _setBusyOverlay(true, "Applying OCR settings and restarting host...");
            await _saveSettingsImmediatelyAsync().ConfigureAwait(true);

            var settings = _settingsAccessor();
            if (settings.OcrEngine == OcrEngineKind.Paddle)
            {
                _resourceHostFacade.StopPaddle();
                _resourceHostFacade.StopPaddleVl();
                _resourceHostFacade.StopNdl();
                _resourceHostFacade.StopVisionLlm();
            }
            else if (settings.OcrEngine == OcrEngineKind.PaddleVllm)
            {
                _resourceHostFacade.StopPaddleVl();
                _resourceHostFacade.StopPaddle();
                _resourceHostFacade.StopNdl();
                _resourceHostFacade.StopVisionLlm();
            }
            else if (settings.OcrEngine == OcrEngineKind.Ndl)
            {
                _resourceHostFacade.StopNdl();
                _resourceHostFacade.StopPaddle();
                _resourceHostFacade.StopPaddleVl();
                _resourceHostFacade.StopVisionLlm();
            }
            else if (settings.OcrEngine == OcrEngineKind.VisionLlm)
            {
                _resourceHostFacade.StopVisionLlm();
                _resourceHostFacade.StopPaddle();
                _resourceHostFacade.StopPaddleVl();
            }
            else
            {
                _appendLog("OCR host restart skipped: current OCR engine is WinRT.");
                return;
            }

            await _resourceHostFacade.EnsureResourceHostsAsync(settings).ConfigureAwait(true);
            _appendLog("OCR host restarted with latest settings.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to restart OCR host.");
            _showLoadFailure("Failed to restart OCR host. See the logs for details.");
        }
        finally
        {
            _setBusyOverlay(false, null);
        }
    }

    public void StopPaddleVlHost()
    {
        if (!_isLoaded())
        {
            return;
        }

        if (_isRunInProgress())
        {
            _appendLog("Stop skipped: OCR is running.");
            return;
        }

        if (!_resourceHostFacade.IsPaddleVlRunning)
        {
            _appendLog("PaddleOCR-VL host stop skipped: host is not running.");
            return;
        }

        try
        {
            _resourceHostFacade.StopPaddleVl();
            _appendLog("PaddleOCR-VL host stopped.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to stop PaddleOCR-VL host.");
            _showLoadFailure("Failed to stop PaddleOCR-VL host. See the logs for details.");
        }
    }
}
