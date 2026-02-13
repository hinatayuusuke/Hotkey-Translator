using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hotkey_Translator.ViewModels;

internal sealed partial class MainWindowViewModel : ObservableObject
{
    public MainWindowViewModel(
        SettingsViewModel settings,
        RuntimeStatusViewModel runtimeStatus,
        Func<Task> runOnceAsync,
        Func<Task> selectRoiAsync,
        Action swapLanguages,
        Action moveTranslationPriorityUp,
        Action moveTranslationPriorityDown,
        Func<Task> reloadLlamaModelsAsync,
        Func<Task> restartLlamaCppAsync,
        Func<Task> stopLlamaServerAsync,
        Func<Task> restartPaddleOcrHostsAsync,
        Action stopPaddleVlHost,
        Func<Task> saveSettingsAsync)
    {
        Settings = settings;
        RuntimeStatus = runtimeStatus;
        RunOnceCommand = new AsyncRelayCommand(runOnceAsync);
        SelectRoiCommand = new AsyncRelayCommand(selectRoiAsync);
        SwapLanguagesCommand = new RelayCommand(swapLanguages);
        TranslationPriorityUpCommand = new RelayCommand(moveTranslationPriorityUp);
        TranslationPriorityDownCommand = new RelayCommand(moveTranslationPriorityDown);
        ReloadLlamaModelsCommand = new AsyncRelayCommand(reloadLlamaModelsAsync);
        RestartLlamaCppCommand = new AsyncRelayCommand(restartLlamaCppAsync);
        StopLlamaServerCommand = new AsyncRelayCommand(stopLlamaServerAsync);
        RestartPaddleOcrHostsCommand = new AsyncRelayCommand(restartPaddleOcrHostsAsync);
        StopPaddleVlHostCommand = new RelayCommand(stopPaddleVlHost);
        SaveSettingsCommand = new AsyncRelayCommand(saveSettingsAsync);
    }

    public SettingsViewModel Settings { get; }

    public RuntimeStatusViewModel RuntimeStatus { get; }

    public IAsyncRelayCommand RunOnceCommand { get; }

    public IAsyncRelayCommand SelectRoiCommand { get; }

    public IRelayCommand SwapLanguagesCommand { get; }

    public IRelayCommand TranslationPriorityUpCommand { get; }

    public IRelayCommand TranslationPriorityDownCommand { get; }

    public IAsyncRelayCommand ReloadLlamaModelsCommand { get; }

    public IAsyncRelayCommand RestartLlamaCppCommand { get; }

    public IAsyncRelayCommand StopLlamaServerCommand { get; }

    public IAsyncRelayCommand RestartPaddleOcrHostsCommand { get; }

    public IRelayCommand StopPaddleVlHostCommand { get; }

    public IAsyncRelayCommand SaveSettingsCommand { get; }
}
