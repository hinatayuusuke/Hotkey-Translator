using CommunityToolkit.Mvvm.ComponentModel;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class RuntimeStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    private string _roiStatusMessage = "ROI: not set";

    [ObservableProperty]
    private string _translationStatusMessage = "Translation status: unknown";
}
