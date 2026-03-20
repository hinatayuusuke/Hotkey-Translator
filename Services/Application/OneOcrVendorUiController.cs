using System;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Hotkey_Translator.Models;

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
            ShowFailure("Failed to evaluate OneOCR prerequisites.", ex.Message);
            return false;
        }

        if (!status.HelperExists)
        {
            var message =
                $"OneOCR helper is missing:{Environment.NewLine}{status.HelperPath}{Environment.NewLine}{Environment.NewLine}" +
                "Build or place the helper before selecting OneOCR.";
            _loggerAccessor()?.Error($"stage=oneocr event=vendor_setup_blocked reason=helper_missing path=\"{status.HelperPath}\".");
            ShowFailure("OneOCR helper required", message);
            return false;
        }

        if (status.MissingVendorFiles.Count == 0)
        {
            return true;
        }

        var result = MessageBox.Show(
            _ownerWindow,
            BuildConfirmationMessage(status),
            "OneOCR vendor files required",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.OK)
        {
            _appendLog("OneOCR vendor setup canceled by user.");
            return false;
        }

        _busyOverlayController.BeginProgressScope("Copying OneOCR vendor files from Snipping Tool...");
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
            ShowFailure("OneOCR vendor setup failed", copyResult.ErrorMessage ?? "Unknown error.");
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
        return
            $"OneOCR requires vendor files that are not present:{Environment.NewLine}{missingFiles}{Environment.NewLine}{Environment.NewLine}" +
            $"Target folder:{Environment.NewLine}{status.VendorDirectory}{Environment.NewLine}{Environment.NewLine}" +
            "Select OK to copy them from the installed Snipping Tool package now. " +
            "Select Cancel to keep the previous OCR engine.";
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
