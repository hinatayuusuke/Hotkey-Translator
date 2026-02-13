using System.Threading.Tasks;
using Hotkey_Translator.Services.Application;

namespace Hotkey_Translator.ViewModels;

internal sealed class SettingsViewModel
{
    private readonly ISettingsChangeScheduler _changeScheduler;

    public SettingsViewModel(ISettingsChangeScheduler changeScheduler)
    {
        _changeScheduler = changeScheduler;
    }

    public void RequestSave()
    {
        _changeScheduler.RequestSave();
    }

    public Task FlushPendingSaveAsync()
    {
        return _changeScheduler.FlushAsync();
    }

    public void CancelPendingSave()
    {
        _changeScheduler.CancelPending();
    }
}
