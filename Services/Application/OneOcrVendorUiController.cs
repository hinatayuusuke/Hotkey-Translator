using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.Services.Application;

internal sealed class OneOcrVendorUiController
{
    private readonly Window _ownerWindow;
    private readonly Dispatcher _dispatcher;
    private readonly BusyOverlayController _busyOverlayController;
    private readonly Func<AppLogger?> _loggerAccessor;
    private readonly Action<string> _appendLog;
    private readonly OneOcrVendorProvisioner _provisioner = new();

    public OneOcrVendorUiController(
        Window ownerWindow,
        Dispatcher dispatcher,
        BusyOverlayController busyOverlayController,
        Func<AppLogger?> loggerAccessor,
        Action<string> appendLog)
    {
        _ownerWindow = ownerWindow;
        _dispatcher = dispatcher;
        _busyOverlayController = busyOverlayController;
        _loggerAccessor = loggerAccessor;
        _appendLog = appendLog;
    }

    public bool EnsureVendorAvailable(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            return _dispatcher.Invoke(() => EnsureVendorAvailable(settings));
        }

        if (!IsOneOcrRequired(settings))
        {
            return true;
        }

        OneOcrVendorStatus status;
        try
        {
            status = _provisioner.GetStatus(settings);
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "Failed to evaluate OneOCR vendor prerequisites.");
            ShowFailure(LocalizationService.Instance.GetString("Dialog_OneOcrVendorFailed_Title"), ex.Message);
            return false;
        }

        if (!status.HelperExists)
        {
            var message = LocalizationService.Instance.GetString("OneOcr_HelperMissing_Message", status.HelperPath);
            _loggerAccessor()?.Error($"stage=oneocr event=vendor_setup_blocked reason=helper_missing path=\"{status.HelperPath}\".");
            ShowFailure(LocalizationService.Instance.GetString("Dialog_OneOcrHelperRequired_Title"), message);
            return false;
        }

        if (status.MissingVendorFiles.Count == 0)
        {
            return true;
        }

        var result = MessageBox.Show(
            _ownerWindow,
            BuildConfirmationMessage(status),
            LocalizationService.Instance.GetString("Dialog_OneOcrVendorRequired_Title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            _appendLog("OneOCR vendor setup canceled by user.");
            return false;
        }

        _busyOverlayController.BeginProgressScope(LocalizationService.Instance.GetString("OneOcr_VendorCopyBusy"));
        OneOcrVendorCopyResult copyResult;
        try
        {
            copyResult = _provisioner.TryProvisionFromInstalledSnippingTool(settings);
        }
        finally
        {
            _busyOverlayController.EndProgressScope();
        }

        if (!copyResult.Succeeded)
        {
            _loggerAccessor()?.Error(
                $"stage=oneocr event=vendor_copy_failed target=\"{copyResult.VendorDirectory}\" reason=\"{copyResult.ErrorMessage ?? "unknown"}\".");
            _appendLog($"OneOCR vendor setup failed: {copyResult.ErrorMessage ?? "unknown"}");
            ShowFailure(
                LocalizationService.Instance.GetString("Dialog_OneOcrVendorFailed_Title"),
                copyResult.ErrorMessage ?? LocalizationService.Instance.GetString("OneOcr_UnknownError"));
            return false;
        }

        if (copyResult.CopiedFiles)
        {
            _loggerAccessor()?.Info(
                $"stage=oneocr event=vendor_copied source=\"{copyResult.SourceDirectory}\" target=\"{copyResult.VendorDirectory}\".");
            _appendLog($"OneOCR vendor files copied from Snipping Tool to {copyResult.VendorDirectory}.");
        }

        return true;
    }

    private static string BuildConfirmationMessage(OneOcrVendorStatus status)
    {
        var missingFiles = string.Join(
            Environment.NewLine,
            status.MissingVendorFiles.Select(fileName => $"- {fileName}"));
        return LocalizationService.Instance.GetString("OneOcr_VendorConfirmation_Message", missingFiles, status.VendorDirectory);
    }

    private static bool IsOneOcrRequired(AppSettings settings)
    {
        if (!settings.EnableOneOcrHelper)
        {
            return false;
        }

        if (settings.OcrEngine == OcrEngineKind.OneOcr)
        {
            return true;
        }

        return settings.OcrEngine == OcrEngineKind.VisionLlm &&
               settings.EnableVisionGeometryHybridOcr &&
               settings.VisionGeometryHybridBaseEngine == VisionGeometryHybridBaseEngineKind.OneOcr;
    }

    private void ShowFailure(string title, string message)
    {
        MessageBox.Show(_ownerWindow, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
