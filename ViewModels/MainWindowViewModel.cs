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
    private bool _suppressSidebarSync;
    private bool _suppressBottomDrawerSync;

    [ObservableProperty]
    private int _selectedSettingsCategoryIndex;

    [ObservableProperty]
    private int _selectedSidebarIndex;

    [ObservableProperty]
    private int _selectedRootTabIndex;

    [ObservableProperty]
    private bool _isBottomPanelOpen = true;

    [ObservableProperty]
    private bool _bottomPreviewPaneVisible = true;

    [ObservableProperty]
    private bool _bottomLogPaneVisible = true;

    [ObservableProperty]
    private int _selectedTranslationPriorityIndex = -1;

    public MainWindowViewModel(
        SettingsViewModel settings,
        RuntimeStatusViewModel runtimeStatus,
        Func<Task> runOnceAsync,
        Action cancelCurrentRun,
        Func<Task> selectRoiAsync,
        Action swapLanguages,
        Action requestSettingsSave,
        Func<Task> reloadLlamaModelsAsync,
        Func<Task> restartLlamaCppAsync,
        Func<Task> stopLlamaServerAsync,
        Func<Task> reloadVisionLlmModelsAsync,
        Func<Task> restartVisionLlmAsync,
        Func<Task> stopVisionLlmAsync,
        Func<Task> restartPaddleOcrHostsAsync,
        Action stopPaddleVlHost,
        Func<Task> saveSettingsAsync)
    {
        _requestSettingsSave = requestSettingsSave;
        Settings = settings;
        RuntimeStatus = runtimeStatus;
        LlamaModelOptions = new ObservableCollection<LlamaModelOption>();
        VisionLlmModelOptions = new ObservableCollection<LlamaModelOption>();
        VisionLlmMmprojOptions = new ObservableCollection<LlamaModelOption>();
        TranslationPriority = new ObservableCollection<string>();
        RunOnceCommand = new AsyncRelayCommand(runOnceAsync);
        CancelCurrentRunCommand = new RelayCommand(cancelCurrentRun);
        SelectRoiCommand = new AsyncRelayCommand(selectRoiAsync);
        SwapLanguagesCommand = new RelayCommand(swapLanguages);
        TranslationPriorityUpCommand = new RelayCommand(MoveTranslationPriorityUp);
        TranslationPriorityDownCommand = new RelayCommand(MoveTranslationPriorityDown);
        ReloadLlamaModelsCommand = new AsyncRelayCommand(reloadLlamaModelsAsync);
        RestartLlamaCppCommand = new AsyncRelayCommand(restartLlamaCppAsync);
        StopLlamaServerCommand = new AsyncRelayCommand(stopLlamaServerAsync);
        ReloadVisionLlmModelsCommand = new AsyncRelayCommand(reloadVisionLlmModelsAsync);
        RestartVisionLlmCommand = new AsyncRelayCommand(restartVisionLlmAsync);
        StopVisionLlmCommand = new AsyncRelayCommand(stopVisionLlmAsync);
        RestartPaddleOcrHostsCommand = new AsyncRelayCommand(restartPaddleOcrHostsAsync);
        StopPaddleVlHostCommand = new RelayCommand(stopPaddleVlHost);
        TogglePreviewPaneCommand = new RelayCommand(TogglePreviewPane);
        ToggleLogPaneCommand = new RelayCommand(ToggleLogPane);
        SaveSettingsCommand = new AsyncRelayCommand(saveSettingsAsync);
        SelectedSidebarIndex = 0;
        SelectedRootTabIndex = 0;
        SelectedSettingsCategoryIndex = 0;
    }

    public SettingsViewModel Settings { get; }

    public RuntimeStatusViewModel RuntimeStatus { get; }

    public ObservableCollection<LlamaModelOption> LlamaModelOptions { get; }

    public ObservableCollection<LlamaModelOption> VisionLlmModelOptions { get; }

    public ObservableCollection<LlamaModelOption> VisionLlmMmprojOptions { get; }

    public ObservableCollection<string> TranslationPriority { get; }

    public IAsyncRelayCommand RunOnceCommand { get; }

    public IRelayCommand CancelCurrentRunCommand { get; }

    public IAsyncRelayCommand SelectRoiCommand { get; }

    public IRelayCommand SwapLanguagesCommand { get; }

    public IRelayCommand TranslationPriorityUpCommand { get; }

    public IRelayCommand TranslationPriorityDownCommand { get; }

    public IAsyncRelayCommand ReloadLlamaModelsCommand { get; }

    public IAsyncRelayCommand RestartLlamaCppCommand { get; }

    public IAsyncRelayCommand StopLlamaServerCommand { get; }

    public IAsyncRelayCommand ReloadVisionLlmModelsCommand { get; }

    public IAsyncRelayCommand RestartVisionLlmCommand { get; }

    public IAsyncRelayCommand StopVisionLlmCommand { get; }

    public IAsyncRelayCommand RestartPaddleOcrHostsCommand { get; }

    public IRelayCommand StopPaddleVlHostCommand { get; }

    public IRelayCommand TogglePreviewPaneCommand { get; }

    public IRelayCommand ToggleLogPaneCommand { get; }

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

    public void ResetVisionLlmModelOptions(IEnumerable<LlamaModelOption> values)
    {
        VisionLlmModelOptions.Clear();
        foreach (var value in values)
        {
            VisionLlmModelOptions.Add(value);
        }
    }

    public void ResetVisionLlmMmprojOptions(IEnumerable<LlamaModelOption> values)
    {
        VisionLlmMmprojOptions.Clear();
        foreach (var value in values)
        {
            VisionLlmMmprojOptions.Add(value);
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

    private void TogglePreviewPane()
    {
        BottomPreviewPaneVisible = !BottomPreviewPaneVisible;
    }

    private void ToggleLogPane()
    {
        BottomLogPaneVisible = !BottomLogPaneVisible;
    }

    partial void OnSelectedSidebarIndexChanged(int value)
    {
        if (_suppressSidebarSync)
        {
            return;
        }

        _suppressSidebarSync = true;
        try
        {
            if (value == 0)
            {
                SelectedRootTabIndex = 0;
                return;
            }

            SelectedRootTabIndex = 1;
            SelectedSettingsCategoryIndex = Math.Max(0, value - 1);
        }
        finally
        {
            _suppressSidebarSync = false;
        }
    }

    partial void OnSelectedRootTabIndexChanged(int value)
    {
        if (_suppressSidebarSync)
        {
            return;
        }

        _suppressSidebarSync = true;
        try
        {
            if (value == 0)
            {
                SelectedSidebarIndex = 0;
            }
            else if (SelectedSidebarIndex == 0)
            {
                SelectedSidebarIndex = 1;
            }
        }
        finally
        {
            _suppressSidebarSync = false;
        }
    }

    partial void OnSelectedSettingsCategoryIndexChanged(int value)
    {
        if (_suppressSidebarSync || SelectedRootTabIndex != 1)
        {
            return;
        }

        _suppressSidebarSync = true;
        try
        {
            SelectedSidebarIndex = Math.Max(1, value + 1);
        }
        finally
        {
            _suppressSidebarSync = false;
        }
    }

    partial void OnIsBottomPanelOpenChanged(bool value)
    {
        if (_suppressBottomDrawerSync)
        {
            return;
        }

        _suppressBottomDrawerSync = true;
        try
        {
            if (!value)
            {
                BottomPreviewPaneVisible = false;
                BottomLogPaneVisible = false;
                return;
            }
        }
        finally
        {
            _suppressBottomDrawerSync = false;
        }
    }

    partial void OnBottomPreviewPaneVisibleChanged(bool value)
    {
        SyncBottomDrawerState(value);
    }

    partial void OnBottomLogPaneVisibleChanged(bool value)
    {
        SyncBottomDrawerState(value);
    }

    private void SyncBottomDrawerState(bool paneNowVisible)
    {
        if (_suppressBottomDrawerSync)
        {
            return;
        }

        _suppressBottomDrawerSync = true;
        try
        {
            if (!IsBottomPanelOpen)
            {
                if (paneNowVisible)
                {
                    IsBottomPanelOpen = true;
                }

                return;
            }

            if (!BottomPreviewPaneVisible && !BottomLogPaneVisible)
            {
                IsBottomPanelOpen = false;
            }
        }
        finally
        {
            _suppressBottomDrawerSync = false;
        }
    }
}
