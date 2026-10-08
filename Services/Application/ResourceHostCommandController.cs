using System;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;
using Hotkey_Translator.Services.GrpcHost;

namespace Hotkey_Translator.Services.Application;

internal sealed class ResourceHostCommandController
{
    private readonly ResourceHostFacade _resourceHostFacade;
    private readonly Func<bool> _isLoaded;
    private readonly Func<bool> _isRunInProgress;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Action<AppSettings, bool> _syncSettingsToView;
    private readonly Func<Task<bool>> _saveSettingsImmediatelyAsync;
    private bool _restartInProgress;
    private readonly Action<string> _appendLog;
    private readonly Action<string> _showLoadFailure;
    private readonly Func<AppLogger?> _loggerAccessor;

    public ResourceHostCommandController(
        ResourceHostFacade resourceHostFacade,
        Func<bool> isLoaded,
        Func<bool> isRunInProgress,
        Func<AppSettings> settingsAccessor,
        Action<AppSettings, bool> syncSettingsToView,
        Func<Task<bool>> saveSettingsImmediatelyAsync,
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
        _appendLog = appendLog;
        _showLoadFailure = showLoadFailure;
        _loggerAccessor = loggerAccessor;
    }

    public async Task RestartLlamaCppAsync()
    {
        if (!_isLoaded() || _isRunInProgress() || _restartInProgress || _resourceHostFacade.IsLoading) return;
        _restartInProgress = true;
        try
        {
            _resourceHostFacade.AllowExplicitReload(translationOnly: true);
            await Task.Run(_resourceHostFacade.StopLlama).ConfigureAwait(true);
            var settings = _settingsAccessor();
            settings.EnableLlamaCppTranslation = true;
            _syncSettingsToView(settings, true);
            if (!await _saveSettingsImmediatelyAsync().ConfigureAwait(true)) return;
            if (_resourceHostFacade.LastLoadResult.Status == ResourceHostLoadStatus.Succeeded && _resourceHostFacade.IsLlamaRunning)
                _appendLog("Llama.cpp restarted.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to restart Llama.cpp host.");
            _showLoadFailure(HostLoadDiagnostics.Sanitize(ex.Message));
        }
        finally { _restartInProgress = false; }
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

            if (!await _saveSettingsImmediatelyAsync().ConfigureAwait(true))
            {
                _appendLog("llama-server stop canceled before resource setup/download.");
                return;
            }
            _appendLog("llama-server stopped.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to stop llama-server.");
            _showLoadFailure("Failed to stop llama-server. See the logs for details.");
        }
    }

    public async Task RestartVisionLlmAsync()
    {
        if (!_isLoaded() || _isRunInProgress() || _restartInProgress || _resourceHostFacade.IsLoading) return;
        _restartInProgress = true;
        try
        {
            _resourceHostFacade.AllowExplicitReload(translationOnly: false);
            await Task.Run(_resourceHostFacade.StopVisionLlm).ConfigureAwait(true);
            if (!await _saveSettingsImmediatelyAsync().ConfigureAwait(true)) return;
            if (_resourceHostFacade.LastLoadResult.Status == ResourceHostLoadStatus.Succeeded && _resourceHostFacade.IsVisionLlmRunning)
                _appendLog("VisionLLM restarted.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to restart VisionLLM host.");
            _showLoadFailure(HostLoadDiagnostics.Sanitize(ex.Message));
        }
        finally { _restartInProgress = false; }
    }

    public void StopVisionLlmHost()
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

        if (!_resourceHostFacade.IsVisionLlmRunning)
        {
            _appendLog("VisionLLM host stop skipped: host is not running.");
            return;
        }

        try
        {
            // NOTE: OCR engine selection still controls auto-start. This stop is intentionally temporary.
            _resourceHostFacade.StopVisionLlm();
            _appendLog("VisionLLM host stopped.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to stop VisionLLM host.");
            _showLoadFailure("Failed to stop VisionLLM. See the logs for details.");
        }
    }

    public async Task RestartPaddleOcrHostsAsync()
    {
        if (!_isLoaded() || _isRunInProgress() || _restartInProgress || _resourceHostFacade.IsLoading) return;
        _restartInProgress = true;
        try
        {
            _resourceHostFacade.AllowExplicitReload(translationOnly: false);
            await Task.Run(() =>
            {
                _resourceHostFacade.StopPaddle();
                _resourceHostFacade.StopPaddleVl();
                _resourceHostFacade.StopNdl();
                _resourceHostFacade.StopVisionLlm();
            }).ConfigureAwait(true);
            if (!await _saveSettingsImmediatelyAsync().ConfigureAwait(true)) return;
            if (_resourceHostFacade.LastLoadResult.Status == ResourceHostLoadStatus.Succeeded)
                _appendLog("OCR host restarted with latest settings.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to restart OCR host.");
            _showLoadFailure(HostLoadDiagnostics.Sanitize(ex.Message));
        }
        finally { _restartInProgress = false; }
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
