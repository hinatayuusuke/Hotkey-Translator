using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hotkey_Translator.Models;
using Windows.Globalization;
using WinRtOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace Hotkey_Translator.Services;

internal enum WinRtLanguagePackStatus
{
    NotApplicable = 0,
    Ready = 1,
    UserCanceled = 2,
    InstallFailed = 3
}

internal readonly record struct WinRtLanguagePackResult(
    WinRtLanguagePackStatus Status,
    string LocaleTag,
    string Message);

internal sealed class WinRtOcrLanguagePackCoordinator
{
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly WindowsCapabilityInstaller _installer;
    private readonly Func<string, bool> _confirmInstall;
    private readonly Action<string> _showInstallError;
    private readonly HashSet<string> _promptedLocales = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public WinRtOcrLanguagePackCoordinator(
        Func<AppLogger?> loggerAccessor,
        WindowsCapabilityInstaller installer,
        Func<string, bool> confirmInstall,
        Action<string> showInstallError)
    {
        _loggerAccessor = loggerAccessor;
        _installer = installer;
        _confirmInstall = confirmInstall;
        _showInstallError = showInstallError;
    }

    public async Task<WinRtLanguagePackResult> EnsureLanguagePackAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (settings.OcrEngine != OcrEngineKind.WinRt)
        {
            return new WinRtLanguagePackResult(WinRtLanguagePackStatus.NotApplicable, string.Empty, "WinRT not selected.");
        }

        var locale = WinRtLanguageResolver.ResolveOcrLocale(settings.SourceLanguage);
        if (string.IsNullOrWhiteSpace(locale))
        {
            return new WinRtLanguagePackResult(WinRtLanguagePackStatus.Ready, string.Empty, "Language not specified; fallback will be used.");
        }

        if (IsLanguageSupported(locale))
        {
            return new WinRtLanguagePackResult(WinRtLanguagePackStatus.Ready, locale, "Language pack already available.");
        }

        var shouldPrompt = false;
        lock (_sync)
        {
            if (!_promptedLocales.Contains(locale))
            {
                _promptedLocales.Add(locale);
                shouldPrompt = true;
            }
        }

        if (!shouldPrompt)
        {
            return new WinRtLanguagePackResult(
                WinRtLanguagePackStatus.InstallFailed,
                locale,
                "Language pack missing (prompt already shown this session).");
        }

        var confirmed = _confirmInstall(locale);
        if (!confirmed)
        {
            return new WinRtLanguagePackResult(
                WinRtLanguagePackStatus.UserCanceled,
                locale,
                "User canceled language pack installation.");
        }

        var installResult = await _installer
            .InstallOcrLanguageCapabilityAsync(locale, cancellationToken)
            .ConfigureAwait(true);
        if (installResult.Status != CapabilityInstallStatus.Succeeded)
        {
            var failureMessage = BuildFailureMessage(locale, installResult);
            _showInstallError(failureMessage);
            return new WinRtLanguagePackResult(WinRtLanguagePackStatus.InstallFailed, locale, failureMessage);
        }

        if (!IsLanguageSupported(locale))
        {
            const string messageTemplate =
                "OCR language pack installation completed, but WinRT OCR still cannot use locale '{0}'. " +
                "Please reboot Windows and try again, or install the language pack manually in Windows Settings.";
            var message = string.Format(messageTemplate, locale);
            _showInstallError(message);
            return new WinRtLanguagePackResult(WinRtLanguagePackStatus.InstallFailed, locale, message);
        }

        _loggerAccessor()?.Info($"stage=winrt_ocr_lang_pack event=ready locale={locale}.");
        return new WinRtLanguagePackResult(WinRtLanguagePackStatus.Ready, locale, "Language pack installed.");
    }

    private static bool IsLanguageSupported(string localeTag)
    {
        try
        {
            var language = new Language(localeTag);
            var engine = WinRtOcrEngine.TryCreateFromLanguage(language);
            return engine != null;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildFailureMessage(string localeTag, CapabilityInstallResult installResult)
    {
        return installResult.Status switch
        {
            CapabilityInstallStatus.PermissionDenied =>
                $"Failed to install OCR language pack '{localeTag}': administrator permission is required.",
            CapabilityInstallStatus.UnsupportedEnvironment =>
                $"Failed to install OCR language pack '{localeTag}': unsupported environment.",
            CapabilityInstallStatus.Canceled =>
                $"OCR language pack install for '{localeTag}' was canceled.",
            _ =>
                $"Failed to install OCR language pack '{localeTag}'. exitCode={installResult.ExitCode}."
        };
    }
}
