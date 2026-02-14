using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hotkey_Translator.ViewModels;

internal sealed record LlamaModelOption(string Value, string Display);

internal sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly Action _requestSettingsSave;

    [ObservableProperty]
    private int _selectedSettingsCategoryIndex;

    [ObservableProperty]
    private int _selectedTranslationPriorityIndex = -1;

    public MainWindowViewModel(
        SettingsViewModel settings,
        RuntimeStatusViewModel runtimeStatus,
        Func<Task> runOnceAsync,
        Func<Task> selectRoiAsync,
        Action swapLanguages,
        Action requestSettingsSave,
        Func<Task> reloadLlamaModelsAsync,
        Func<Task> restartLlamaCppAsync,
        Func<Task> stopLlamaServerAsync,
        Func<Task> restartPaddleOcrHostsAsync,
        Action stopPaddleVlHost,
        Func<Task> saveSettingsAsync)
    {
        _requestSettingsSave = requestSettingsSave;
        Settings = settings;
        RuntimeStatus = runtimeStatus;
        LlamaModelOptions = new ObservableCollection<LlamaModelOption>();
        TranslationPriority = new ObservableCollection<string>();
        RunOnceCommand = new AsyncRelayCommand(runOnceAsync);
        SelectRoiCommand = new AsyncRelayCommand(selectRoiAsync);
        SwapLanguagesCommand = new RelayCommand(swapLanguages);
        TranslationPriorityUpCommand = new RelayCommand(MoveTranslationPriorityUp);
        TranslationPriorityDownCommand = new RelayCommand(MoveTranslationPriorityDown);
        ReloadLlamaModelsCommand = new AsyncRelayCommand(reloadLlamaModelsAsync);
        RestartLlamaCppCommand = new AsyncRelayCommand(restartLlamaCppAsync);
        StopLlamaServerCommand = new AsyncRelayCommand(stopLlamaServerAsync);
        RestartPaddleOcrHostsCommand = new AsyncRelayCommand(restartPaddleOcrHostsAsync);
        StopPaddleVlHostCommand = new RelayCommand(stopPaddleVlHost);
        SaveSettingsCommand = new AsyncRelayCommand(saveSettingsAsync);
    }

    public SettingsViewModel Settings { get; }

    public RuntimeStatusViewModel RuntimeStatus { get; }

    public ObservableCollection<LlamaModelOption> LlamaModelOptions { get; }

    public ObservableCollection<string> TranslationPriority { get; }

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

    public void ResetTranslationPriority(IEnumerable<string> values)
    {
        TranslationPriority.Clear();
        foreach (var value in values)
        {
            TranslationPriority.Add(value);
        }

        SelectedTranslationPriorityIndex = TranslationPriority.Count > 0 ? 0 : -1;
    }

    public List<string> GetTranslationPriorityOrDefault(IReadOnlyList<string> defaultOrder)
    {
        if (TranslationPriority.Count == 0)
        {
            return defaultOrder.ToList();
        }

        return TranslationPriority.ToList();
    }

    public void ResetLlamaModelOptions(IEnumerable<LlamaModelOption> values)
    {
        LlamaModelOptions.Clear();
        foreach (var value in values)
        {
            LlamaModelOptions.Add(value);
        }
    }

    private void MoveTranslationPriorityUp()
    {
        var index = SelectedTranslationPriorityIndex;
        if (index <= 0 || index >= TranslationPriority.Count)
        {
            return;
        }

        var item = TranslationPriority[index];
        TranslationPriority.RemoveAt(index);
        TranslationPriority.Insert(index - 1, item);
        SelectedTranslationPriorityIndex = index - 1;
        _requestSettingsSave();
    }

    private void MoveTranslationPriorityDown()
    {
        var index = SelectedTranslationPriorityIndex;
        if (index < 0 || index >= TranslationPriority.Count - 1)
        {
            return;
        }

        var item = TranslationPriority[index];
        TranslationPriority.RemoveAt(index);
        TranslationPriority.Insert(index + 1, item);
        SelectedTranslationPriorityIndex = index + 1;
        _requestSettingsSave();
    }
}
