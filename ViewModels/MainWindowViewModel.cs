using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hotkey_Translator.Models;
using Hotkey_Translator.Services;

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
    private bool _isBottomPanelOpen = false;

    [ObservableProperty]
    private bool _bottomPreviewPaneVisible = false;

    [ObservableProperty]
    private bool _bottomLogPaneVisible = false;

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
        Func<Task> reloadGeminiModelsAsync,
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
        Settings.PropertyChanged += OnOverviewDependencyChanged;
        RuntimeStatus.PropertyChanged += OnOverviewDependencyChanged;
        LocalizationService.Instance.LanguageChanged += OnLocalizationLanguageChanged;
        LlamaModelOptions = new ObservableCollection<LlamaModelOption>();
        GeminiModelOptions = new ObservableCollection<GeminiModelOption>();
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
        ReloadGeminiModelsCommand = new AsyncRelayCommand(reloadGeminiModelsAsync);
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

    public ObservableCollection<GeminiModelOption> GeminiModelOptions { get; }

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

    public IAsyncRelayCommand ReloadGeminiModelsCommand { get; }

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

    public string CaptureModeSummary => Settings.CaptureModeTag switch
    {
        "Screen" => LocalizationService.Instance.GetString("Summary_CaptureMode_Screen"),
        _ => LocalizationService.Instance.GetString("Summary_CaptureMode_ActiveWindow")
    };

    public string CaptureProviderSummary
    {
        get
        {
            var provider = Settings.CaptureProviderTag switch
            {
                "Wgc" => "WGC",
                "Dxgi" => "DXGI",
                _ => "GDI"
            };

            return Settings.IsCaptureProviderFixed
                ? LocalizationService.Instance.GetString("Summary_CaptureProvider_FixedOnly", provider)
                : provider;
        }
    }

    public string OcrEngineSummary => Settings.OcrEngineTag switch
    {
        "Paddle" => "PaddleOCR (uv)",
        "PaddleVllm" => "PaddleOCR-VL (gRPC)",
        "Ndl" => "NDLOCR-Lite (gRPC)",
        "VisionLlm" => "VisionLLM (gRPC)",
        "OneOcr" => "OneOCR (native helper)",
        _ => "WinRT (Windows)"
    };

    public string TranslationRouteSummary
    {
        get
        {
            var routes = new List<string>();

            if (Settings.EnableDeepL)
            {
                routes.Add("DeepL");
            }

            if (Settings.EnableGoogleWeb)
            {
                routes.Add("GoogleWeb");
            }

            if (Settings.EnableGemini)
            {
                routes.Add("Gemini");
            }

            if (Settings.EnableLlamaCppTranslation)
            {
                routes.Add("Llama.cpp");
            }

            if (Settings.EnableVisionLlmSharedLocalTranslation)
            {
                routes.Add("VisionLLM local");
            }

            return routes.Count == 0
                ? LocalizationService.Instance.GetString("Summary_Translation_NoEngine")
                : string.Join(" + ", routes);
        }
    }

    public string SourceLanguageSummary => FormatLanguage(Settings.SourceLanguageTag, Settings.SourceLanguageCustom);

    public string TargetLanguageSummary => FormatLanguage(Settings.TargetLanguageTag, Settings.TargetLanguageCustom);

    public string HookStatusSummary
    {
        get
        {
            if (Settings.EnableGraphicsHookPipeline)
            {
                var api = Settings.GraphicsHookApiTag switch
                {
                    "Dx9" => "DX9",
                    "Vulkan" => "Vulkan",
                    _ => "DX11"
                };

                var overlay = Settings.GraphicsHookOverlayEnabled
                    ? LocalizationService.Instance.GetString("Summary_Hook_OverlayOn")
                    : LocalizationService.Instance.GetString("Summary_Hook_OverlayOff");
                return LocalizationService.Instance.GetString("Summary_Hook_GraphicsHook", api, overlay);
            }

            if (Settings.EnableMirrorFullscreenMode)
            {
                return LocalizationService.Instance.GetString("Summary_Hook_MirrorFullscreen");
            }

            return LocalizationService.Instance.GetString("Summary_Hook_Off");
        }
    }

    public string OverlayStatusSummary =>
        RuntimeStatus.IsBusy ? RuntimeStatus.BusyMessage : RuntimeStatus.TranslationStatusMessage;

    public string RunOnceHotkeySummary => $"Run OCR / Translate: {FormatHotkey(Settings.HotkeyRunOnceKey, Settings.HotkeyRunOnceCtrl, Settings.HotkeyRunOnceAlt, Settings.HotkeyRunOnceShift)}";

    public string ToggleOverlayHotkeySummary => $"Toggle overlay: {FormatHotkey(Settings.HotkeyToggleOverlayKey, Settings.HotkeyToggleOverlayCtrl, Settings.HotkeyToggleOverlayAlt, Settings.HotkeyToggleOverlayShift)}";

    public string SelectRoiHotkeySummary => $"Select ROI: {FormatHotkey(Settings.HotkeySelectRoiKey, Settings.HotkeySelectRoiCtrl, Settings.HotkeySelectRoiAlt, Settings.HotkeySelectRoiShift)}";

    public string LockWindowHotkeySummary => $"Lock window: {FormatHotkey(Settings.HotkeyLockCaptureWindowKey, Settings.HotkeyLockCaptureWindowCtrl, Settings.HotkeyLockCaptureWindowAlt, Settings.HotkeyLockCaptureWindowShift)}";

    public string ToggleMirrorFullscreenHotkeySummary => $"Mirror fullscreen: {FormatHotkey(Settings.HotkeyToggleMirrorFullscreenKey, Settings.HotkeyToggleMirrorFullscreenCtrl, Settings.HotkeyToggleMirrorFullscreenAlt, Settings.HotkeyToggleMirrorFullscreenShift)}";

    public string RunOnceHotkeyGesture => FormatHotkey(Settings.HotkeyRunOnceKey, Settings.HotkeyRunOnceCtrl, Settings.HotkeyRunOnceAlt, Settings.HotkeyRunOnceShift);

    public string ToggleOverlayHotkeyGesture => FormatHotkey(Settings.HotkeyToggleOverlayKey, Settings.HotkeyToggleOverlayCtrl, Settings.HotkeyToggleOverlayAlt, Settings.HotkeyToggleOverlayShift);

    public string SelectRoiHotkeyGesture => FormatHotkey(Settings.HotkeySelectRoiKey, Settings.HotkeySelectRoiCtrl, Settings.HotkeySelectRoiAlt, Settings.HotkeySelectRoiShift);

    public string LockWindowHotkeyGesture => FormatHotkey(Settings.HotkeyLockCaptureWindowKey, Settings.HotkeyLockCaptureWindowCtrl, Settings.HotkeyLockCaptureWindowAlt, Settings.HotkeyLockCaptureWindowShift);

    public string ToggleMirrorFullscreenHotkeyGesture => FormatHotkey(Settings.HotkeyToggleMirrorFullscreenKey, Settings.HotkeyToggleMirrorFullscreenCtrl, Settings.HotkeyToggleMirrorFullscreenAlt, Settings.HotkeyToggleMirrorFullscreenShift);

    public bool HasOverviewHotkeyWarning => !string.IsNullOrWhiteSpace(OverviewHotkeyWarning);

    public string OverviewHotkeyWarning
    {
        get
        {
            if (Settings.EnableGraphicsHookPipeline)
            {
                // WHY: Lock/unlock target hotkeys do not work while the Graphics Hook pipeline owns capture routing.
                return "Graphics Hook is active, so lock/unlock fixed target hotkeys are unavailable.";
            }

            if (!Settings.EnableRoi)
            {
                return "ROI is disabled. Auto-translate and ROI-specific flows will use the full capture area.";
            }

            return string.Empty;
        }
    }

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

    public void ResetGeminiModelOptions(IEnumerable<GeminiModelOption> values)
    {
        GeminiModelOptions.Clear();
        foreach (var value in values)
        {
            GeminiModelOptions.Add(value);
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

    private void OnOverviewDependencyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(CaptureModeSummary));
        OnPropertyChanged(nameof(CaptureProviderSummary));
        OnPropertyChanged(nameof(OcrEngineSummary));
        OnPropertyChanged(nameof(TranslationRouteSummary));
        OnPropertyChanged(nameof(SourceLanguageSummary));
        OnPropertyChanged(nameof(TargetLanguageSummary));
        OnPropertyChanged(nameof(HookStatusSummary));
        OnPropertyChanged(nameof(OverlayStatusSummary));
        OnPropertyChanged(nameof(RunOnceHotkeySummary));
        OnPropertyChanged(nameof(ToggleOverlayHotkeySummary));
        OnPropertyChanged(nameof(SelectRoiHotkeySummary));
        OnPropertyChanged(nameof(LockWindowHotkeySummary));
        OnPropertyChanged(nameof(ToggleMirrorFullscreenHotkeySummary));
        OnPropertyChanged(nameof(RunOnceHotkeyGesture));
        OnPropertyChanged(nameof(ToggleOverlayHotkeyGesture));
        OnPropertyChanged(nameof(SelectRoiHotkeyGesture));
        OnPropertyChanged(nameof(LockWindowHotkeyGesture));
        OnPropertyChanged(nameof(ToggleMirrorFullscreenHotkeyGesture));
        OnPropertyChanged(nameof(OverviewHotkeyWarning));
        OnPropertyChanged(nameof(HasOverviewHotkeyWarning));
    }

    private static string FormatLanguage(string tag, string customValue)
    {
        if (string.Equals(tag, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(customValue)
                ? LocalizationService.Instance.GetString("Language_Custom")
                : LocalizationService.Instance.GetString("Language_Custom_WithValue", customValue.Trim());
        }

        return tag switch
        {
            "en" => LocalizationService.Instance.GetString("Language_English"),
            "ja" => LocalizationService.Instance.GetString("Language_Japanese"),
            "zh-Hant" => LocalizationService.Instance.GetString("Language_ChineseTraditional"),
            "zh-Hans" => LocalizationService.Instance.GetString("Language_ChineseSimplified"),
            "ru" => LocalizationService.Instance.GetString("Language_Russian"),
            _ => tag
        };
    }

    private void OnLocalizationLanguageChanged(object? sender, EventArgs e)
    {
        OnOverviewDependencyChanged(this, new PropertyChangedEventArgs(null));
    }

    private static string FormatHotkey(string key, bool ctrl, bool alt, bool shift)
    {
        var parts = new List<string>(4);
        if (ctrl)
        {
            parts.Add("Ctrl");
        }

        if (alt)
        {
            parts.Add("Alt");
        }

        if (shift)
        {
            parts.Add("Shift");
        }

        parts.Add(string.IsNullOrWhiteSpace(key) ? "(unassigned)" : key.Trim());
        return string.Join("+", parts);
    }
}
