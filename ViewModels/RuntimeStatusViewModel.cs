using CommunityToolkit.Mvvm.ComponentModel;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class RuntimeStatusViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;
}
