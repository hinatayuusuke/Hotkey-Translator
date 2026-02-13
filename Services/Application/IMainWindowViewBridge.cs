using Hotkey_Translator.Models;

namespace Hotkey_Translator.Services.Application;

internal interface IMainWindowViewBridge
{
    void AppendLog(string message);
    void EnableOverlay();
    void SetBusyOverlay(bool visible, string? message);
    void ShowLoadingSpinnerForRun(AppSettings settings);
    void HideLoadingSpinnerForRun();
    void CancelTranslationOverlay();
}
