using CommunityToolkit.Mvvm.ComponentModel;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class RuntimeStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private bool _busyProgressIsIndeterminate = true;

    [ObservableProperty]
    private double _busyProgressPercent;

    [ObservableProperty]
    private bool _canCancelCurrentRun;

    [ObservableProperty]
    private string _roiStatusMessage = "ROI: not set";

    [ObservableProperty]
    private string _translationStatusMessage = "Translation status: unknown";
}
