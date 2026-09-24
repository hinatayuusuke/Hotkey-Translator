using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Func<OneOcrProcessHost?> _hostAccessor;
    private bool _repairDialogOpen;

    public bool IsRepairing { get; private set; }

    public OneOcrVendorUiController(
        Window ownerWindow,
        Dispatcher dispatcher,
        BusyOverlayController busyOverlayController,
        Func<AppLogger?> loggerAccessor,
        Action<string> appendLog,
        Func<OneOcrProcessHost?> hostAccessor)
    {
        _ownerWindow = ownerWindow;
        _dispatcher = dispatcher;
        _busyOverlayController = busyOverlayController;
        _loggerAccessor = loggerAccessor;
        _appendLog = appendLog;
        _hostAccessor = hostAccessor;
    }

    public async Task RepairAfterFailureAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!_dispatcher.CheckAccess())
        {
            await _dispatcher.InvokeAsync(() => RepairAfterFailureAsync(settings, cancellationToken)).Task.Unwrap();
            return;
        }
        if (_repairDialogOpen || cancellationToken.IsCancellationRequested) return;

        _repairDialogOpen = true;
        var strings = LocalizationService.Instance;
        try
        {
            // WHY: OCR hotkeys also run while the main window is minimized; make repair progress visible.
            if (_ownerWindow.WindowState == WindowState.Minimized) _ownerWindow.WindowState = WindowState.Normal;
            _ownerWindow.Activate();
            if (!ConfirmRepair() || cancellationToken.IsCancellationRequested) return;

            IsRepairing = true;
            _busyOverlayController.BeginProgressScope(strings["OneOcr_RepairBusy"]);
            var wasEnabled = _ownerWindow.IsEnabled;
            _ownerWindow.IsEnabled = false;
            try
            {
                var host = _hostAccessor() ?? throw new InvalidOperationException("OneOCR host is unavailable.");
                await Task.Run(() => new OneOcrRepairService(_loggerAccessor()).RepairAsync(settings, host, cancellationToken));
            }
            finally
            {
                IsRepairing = false;
                _ownerWindow.IsEnabled = wasEnabled;
                _busyOverlayController.EndProgressScope();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                MessageBox.Show(_ownerWindow, strings["OneOcr_RepairSucceeded"], strings["OneOcr_RepairTitle"],
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _appendLog("OneOCR repair canceled.");
        }
        catch (Exception ex)
        {
            _loggerAccessor()?.Error(ex, "OneOCR repair failed.");
            if (!cancellationToken.IsCancellationRequested)
            {
                ShowFailure(strings["OneOcr_RepairTitle"], strings["OneOcr_RepairFailed"] + Environment.NewLine + ex.Message);
            }
        }
        finally
        {
            IsRepairing = false;
            _repairDialogOpen = false;
        }
    }

    private bool ConfirmRepair()
    {
        var strings = LocalizationService.Instance;
        var dialog = new Window
        {
            Owner = _ownerWindow,
            Title = strings["OneOcr_RepairTitle"],
            Width = 460,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        dialog.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
        var content = new StackPanel { Margin = new Thickness(24) };
        content.Children.Add(new TextBlock { Text = strings["OneOcr_RepairPrompt"], TextWrapping = TextWrapping.Wrap });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };
        var repair = new Wpf.Ui.Controls.Button { Content = strings["OneOcr_RepairButton"], Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, IsDefault = true, MinWidth = 100, Padding = new Thickness(12, 6, 12, 6) };
        var cancel = new Wpf.Ui.Controls.Button { Content = strings["Common_Cancel"], IsCancel = true, MinWidth = 100, Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6) };
        repair.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(repair);
        buttons.Children.Add(cancel);
        content.Children.Add(buttons);
        dialog.Content = content;
        return dialog.ShowDialog() == true;
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

    internal static bool IsOneOcrRequired(AppSettings settings)
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
