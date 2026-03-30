using CommunityToolkit.Mvvm.ComponentModel;
using Hotkey_Translator.Services;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class RuntimeStatusViewModel : ObservableObject
{
    public RuntimeStatusViewModel()
    {
        _roiStatusMessage = LocalizationService.Instance.GetString("Runtime_RoiStatus_NotSet", "slot 1");
        _translationStatusMessage = LocalizationService.Instance.GetString("Runtime_TranslationStatus_Unknown");
    }

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
    private string _roiStatusMessage = string.Empty;

    [ObservableProperty]
    private string _translationStatusMessage = string.Empty;
}
