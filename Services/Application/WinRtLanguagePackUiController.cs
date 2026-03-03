using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator.Services.Application;

internal sealed class WinRtLanguagePackUiController : IDisposable
{
    private readonly Window _ownerWindow;
    private readonly Dispatcher _dispatcher;
    private readonly SettingsViewModel _settingsViewModel;
    private readonly Func<AppSettings> _settingsAccessor;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Func<WinRtOcrLanguagePackCoordinator> _coordinatorAccessor;
    private readonly BusyOverlayController _busyOverlayController;
    private readonly Func<Task> _flushPendingSettingsSaveAsync;
    private readonly Action<string> _appendLog;
    private readonly Func<bool> _isLoadedAccessor;
    private readonly Func<bool> _isClosingAccessor;
    private readonly TextBlock _statusTextBlock;
    private readonly Button _installButton;
    private readonly SettingsChangeHandler _settingsChangeHandler;

    private CancellationTokenSource? _precheckCts;
    private int _precheckVersion;
    private bool _isDisposed;

    public WinRtLanguagePackUiController(
        Window ownerWindow,
        Dispatcher dispatcher,
        SettingsViewModel settingsViewModel,
        Func<AppSettings> settingsAccessor,
        Func<AppLogger?> loggerAccessor,
        Func<WinRtOcrLanguagePackCoordinator> coordinatorAccessor,
        BusyOverlayController busyOverlayController,
        Func<Task> flushPendingSettingsSaveAsync,
        Action<string> appendLog,
        Func<bool> isLoadedAccessor,
        Func<bool> isClosingAccessor,
        TextBlock statusTextBlock,
        Button installButton)
    {
        _ownerWindow = ownerWindow;
        _dispatcher = dispatcher;
        _settingsViewModel = settingsViewModel;
        _settingsAccessor = settingsAccessor;
        _loggerAccessor = loggerAccessor;
        _coordinatorAccessor = coordinatorAccessor;
        _busyOverlayController = busyOverlayController;
        _flushPendingSettingsSaveAsync = flushPendingSettingsSaveAsync;
        _appendLog = appendLog;
        _isLoadedAccessor = isLoadedAccessor;
        _isClosingAccessor = isClosingAccessor;
        _statusTextBlock = statusTextBlock;
        _installButton = installButton;

        _settingsChangeHandler = new SettingsChangeHandler(
            _settingsViewModel,
            new[]
            {
                nameof(SettingsViewModel.OcrEngineTag),
                nameof(SettingsViewModel.SourceLanguageTag),
                nameof(SettingsViewModel.SourceLanguageCustom)
            },
            SchedulePrecheck);
    }

    public void Start()
    {
        _settingsChangeHandler.Start();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _settingsChangeHandler.Dispose();
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = null;
    }

    public bool ConfirmInstall(string localeTag)
    {
        if (!_dispatcher.CheckAccess())
        {
            return _dispatcher.Invoke(() => ConfirmInstall(localeTag));
        }

        var message =
            $"WinRT OCR language pack '{localeTag}' is not installed.{Environment.NewLine}{Environment.NewLine}" +
            "Install it now? This may require administrator permission and network access.";
        var result = MessageBox.Show(
            _ownerWindow,
            message,
            "OCR language pack required",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        return result == MessageBoxResult.OK;
    }

    public void ShowInstallError(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ShowInstallError(message));
            return;
        }

        MessageBox.Show(_ownerWindow, message, "OCR language pack required", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ShowInstallSuccess(string message)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ShowInstallSuccess(message));
            return;
        }

        MessageBox.Show(_ownerWindow, message, "OCR language pack required", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void BeginInstallUi(string localeTag)
    {
        _busyOverlayController.BeginProgressScope($"Installing OCR language pack ({localeTag})...");
        ShowStatusUi(
            $"Installing WinRT OCR language pack ({localeTag})...",
            Brushes.DimGray,
            showInstallButton: false,
            enableInstallButton: false);
    }

    public void UpdateInstallUi(CapabilityInstallProgress progress)
    {
        _busyOverlayController.UpdateProgress(progress);
        if (!string.IsNullOrWhiteSpace(progress.Message))
        {
            ShowStatusUi(
                progress.Message,
                Brushes.DimGray,
                showInstallButton: false,
                enableInstallButton: false);
        }
    }

    public void EndInstallUi()
    {
        _busyOverlayController.EndProgressScope();
        SchedulePrecheck();
    }

    public void SchedulePrecheck()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(SchedulePrecheck);
            return;
        }

        if (_isDisposed || !_isLoadedAccessor() || _isClosingAccessor())
        {
            return;
        }

        var version = Interlocked.Increment(ref _precheckVersion);
        _precheckCts?.Cancel();
        _precheckCts?.Dispose();
        _precheckCts = new CancellationTokenSource();
        var token = _precheckCts.Token;
        _ = RefreshPrecheckUiAsync(version, token);
    }

    public async Task InstallNowAsync()
    {
        if (_busyOverlayController.IsScopedBusyActive)
        {
            return;
        }

        await _flushPendingSettingsSaveAsync().ConfigureAwait(true);
        var settings = _settingsAccessor();
        if (settings.OcrEngine != OcrEngineKind.WinRt)
        {
            SchedulePrecheck();
            return;
        }

        var result = await EnsureLanguagePackAsync(settings).ConfigureAwait(true);
        switch (result.Status)
        {
            case WinRtLanguagePackStatus.Ready:
                _appendLog($"WinRT OCR language pack ready: {result.LocaleTag}.");
                break;
            case WinRtLanguagePackStatus.UserCanceled:
                _appendLog($"WinRT OCR language pack install canceled: {result.LocaleTag}.");
                break;
            case WinRtLanguagePackStatus.InstallFailed:
                _appendLog($"WinRT OCR language pack install failed: {result.LocaleTag}.");
                break;
        }

        SchedulePrecheck();
    }

    private async Task<WinRtLanguagePackResult> EnsureLanguagePackAsync(AppSettings settings)
    {
        var coordinator = _coordinatorAccessor();
        return await coordinator
            .EnsureLanguagePackAsync(settings, CancellationToken.None, enforceSessionPromptLimit: false)
            .ConfigureAwait(true);
    }

    private async Task RefreshPrecheckUiAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(_settingsViewModel.OcrEngineTag, "WinRt", StringComparison.OrdinalIgnoreCase))
            {
                HideStatusUi();
                return;
            }

            ShowStatusUi(
                "WinRT OCR language pack: checking...",
                Brushes.DimGray,
                showInstallButton: false,
                enableInstallButton: false);
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested || version != _precheckVersion)
            {
                return;
            }

            var sourceLanguage = ResolveSelectedSourceLanguage();
            var locale = WinRtLanguageResolver.ResolveOcrLocale(sourceLanguage);
            if (string.IsNullOrWhiteSpace(locale))
            {
                ShowStatusUi(
                    "WinRT OCR language pack: source language is not set.",
                    Brushes.DarkOrange,
                    showInstallButton: false,
                    enableInstallButton: false);
                return;
            }

            var supported = WinRtOcrLanguagePackCoordinator.IsLanguageSupportedForLocale(locale);
            if (cancellationToken.IsCancellationRequested || version != _precheckVersion)
            {
                return;
            }

            if (supported)
            {
                ShowStatusUi(
                    $"WinRT OCR language pack: available ({locale}).",
                    Brushes.DimGray,
                    showInstallButton: false,
                    enableInstallButton: false);
                return;
            }

            ShowStatusUi(
                $"WinRT OCR language pack: missing ({locale}).",
                Brushes.DarkOrange,
                showInstallButton: true,
                enableInstallButton: !_busyOverlayController.IsScopedBusyActive);
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to evaluate WinRT language-pack precheck.");
            ShowStatusUi(
                "WinRT OCR language pack: precheck failed.",
                Brushes.DarkOrange,
                showInstallButton: true,
                enableInstallButton: !_busyOverlayController.IsScopedBusyActive);
        }
    }

    private string ResolveSelectedSourceLanguage()
    {
        var selectedTag = (_settingsViewModel.SourceLanguageTag ?? string.Empty).Trim();
        if (string.Equals(selectedTag, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return (_settingsViewModel.SourceLanguageCustom ?? string.Empty).Trim();
        }

        return selectedTag;
    }

    private void ShowStatusUi(string message, Brush foreground, bool showInstallButton, bool enableInstallButton)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(() => ShowStatusUi(message, foreground, showInstallButton, enableInstallButton));
            return;
        }

        _statusTextBlock.Text = message;
        _statusTextBlock.Foreground = foreground;
        _statusTextBlock.Visibility = Visibility.Visible;
        _installButton.Visibility = showInstallButton ? Visibility.Visible : Visibility.Collapsed;
        _installButton.IsEnabled = enableInstallButton;
    }

    private void HideStatusUi()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.Invoke(HideStatusUi);
            return;
        }

        _statusTextBlock.Visibility = Visibility.Collapsed;
        _installButton.Visibility = Visibility.Collapsed;
        _installButton.IsEnabled = false;
    }
}
