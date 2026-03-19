using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Hotkey_Translator.ViewModels;

namespace Hotkey_Translator.UI;

public partial class HotkeysControl : UserControl
{
    private const string HotkeyConflictBorderBrushKey = "HotkeyConflictBorderBrush";
    private const string HotkeyConflictBackgroundBrushKey = "HotkeyConflictBackgroundBrush";
    private const string HotkeyConflictTextBrushKey = "HotkeyConflictTextBrush";
    private SettingsViewModel? _settingsViewModel;
    private readonly Dictionary<string, HotkeyRowVisual> _hotkeyRows;

    public static readonly DependencyProperty HotkeyKeyOptionsProperty = DependencyProperty.Register(
        nameof(HotkeyKeyOptions),
        typeof(IEnumerable<string>),
        typeof(HotkeysControl),
        new PropertyMetadata(null, OnHotkeyKeyOptionsChanged));

    public HotkeysControl()
    {
        InitializeComponent();
        _hotkeyRows = new Dictionary<string, HotkeyRowVisual>(15, System.StringComparer.Ordinal)
        {
            [SettingsViewModel.HotkeyIdRunOnce] = new(HotkeyRunOnceRowBorder, HotkeyRunOnceLabel, HotkeyRunOnceConflictHint),
            [SettingsViewModel.HotkeyIdRunNextRoi] = new(HotkeyRunNextRoiRowBorder, HotkeyRunNextRoiLabel, HotkeyRunNextRoiConflictHint),
            [SettingsViewModel.HotkeyIdRunNextNextRoi] = new(HotkeyRunNextNextRoiRowBorder, HotkeyRunNextNextRoiLabel, HotkeyRunNextNextRoiConflictHint),
            [SettingsViewModel.HotkeyIdToggleOverlay] = new(HotkeyToggleOverlayRowBorder, HotkeyToggleOverlayLabel, HotkeyToggleOverlayConflictHint),
            [SettingsViewModel.HotkeyIdForceRun] = new(HotkeyForceRunRowBorder, HotkeyForceRunLabel, HotkeyForceRunConflictHint),
            [SettingsViewModel.HotkeyIdForceRunNextRoi] = new(HotkeyForceRunNextRoiRowBorder, HotkeyForceRunNextRoiLabel, HotkeyForceRunNextRoiConflictHint),
            [SettingsViewModel.HotkeyIdForceRunNextNextRoi] = new(HotkeyForceRunNextNextRoiRowBorder, HotkeyForceRunNextNextRoiLabel, HotkeyForceRunNextNextRoiConflictHint),
            [SettingsViewModel.HotkeyIdForceGeminiStrict] = new(HotkeyForceGeminiStrictRowBorder, HotkeyForceGeminiStrictLabel, HotkeyForceGeminiStrictConflictHint),
            [SettingsViewModel.HotkeyIdOverlayText] = new(HotkeyOcrOnlyRowBorder, HotkeyOcrOnlyLabel, HotkeyOcrOnlyConflictHint),
            [SettingsViewModel.HotkeyIdSceneAutoTranslate] = new(HotkeyToggleSceneAutoTranslateRowBorder, HotkeyToggleSceneAutoTranslateLabel, HotkeyToggleSceneAutoTranslateConflictHint),
            [SettingsViewModel.HotkeyIdSelectRoi] = new(HotkeySelectRoiRowBorder, HotkeySelectRoiLabel, HotkeySelectRoiConflictHint),
            [SettingsViewModel.HotkeyIdSelectUserFrame] = new(HotkeySelectFixedOverlayFrameRowBorder, HotkeySelectFixedOverlayFrameLabel, HotkeySelectFixedOverlayFrameConflictHint),
            [SettingsViewModel.HotkeyIdLockWindow] = new(HotkeyLockCaptureWindowRowBorder, HotkeyLockCaptureWindowLabel, HotkeyLockCaptureWindowConflictHint),
            [SettingsViewModel.HotkeyIdUnlockWindow] = new(HotkeyUnlockCaptureWindowRowBorder, HotkeyUnlockCaptureWindowLabel, HotkeyUnlockCaptureWindowConflictHint),
            [SettingsViewModel.HotkeyIdMirrorFullscreen] = new(HotkeyToggleMirrorFullscreenRowBorder, HotkeyToggleMirrorFullscreenLabel, HotkeyToggleMirrorFullscreenConflictHint)
        };
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    public IEnumerable<string>? HotkeyKeyOptions
    {
        get => (IEnumerable<string>?)GetValue(HotkeyKeyOptionsProperty);
        set => SetValue(HotkeyKeyOptionsProperty, value);
    }

    private static void OnHotkeyKeyOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((HotkeysControl)d).ApplyHotkeyKeyOptions(e.NewValue as IEnumerable<string>);
    }

    private void ApplyHotkeyKeyOptions(IEnumerable<string>? options)
    {
        HotkeyRunOnceKeyBox.ItemsSource = options;
        HotkeyRunNextRoiKeyBox.ItemsSource = options;
        HotkeyRunNextNextRoiKeyBox.ItemsSource = options;
        HotkeyToggleOverlayKeyBox.ItemsSource = options;
        HotkeyForceRunKeyBox.ItemsSource = options;
        HotkeyForceRunNextRoiKeyBox.ItemsSource = options;
        HotkeyForceRunNextNextRoiKeyBox.ItemsSource = options;
        HotkeyForceGeminiStrictKeyBox.ItemsSource = options;
        HotkeyOcrOnlyKeyBox.ItemsSource = options;
        HotkeyToggleSceneAutoTranslateKeyBox.ItemsSource = options;
        HotkeySelectRoiKeyBox.ItemsSource = options;
        HotkeySelectFixedOverlayFrameKeyBox.ItemsSource = options;
        HotkeyLockCaptureWindowKeyBox.ItemsSource = options;
        HotkeyUnlockCaptureWindowKeyBox.ItemsSource = options;
        HotkeyToggleMirrorFullscreenKeyBox.ItemsSource = options;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        AttachSettingsViewModel(ResolveSettingsViewModel(e.NewValue));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachSettingsViewModel(ResolveSettingsViewModel(DataContext));
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        AttachSettingsViewModel(null);
    }

    private void AttachSettingsViewModel(SettingsViewModel? settingsViewModel)
    {
        if (_settingsViewModel != null)
        {
            _settingsViewModel.PropertyChanged -= OnSettingsViewModelPropertyChanged;
        }

        _settingsViewModel = settingsViewModel;
        if (_settingsViewModel != null)
        {
            _settingsViewModel.PropertyChanged += OnSettingsViewModelPropertyChanged;
        }

        ApplyHotkeyConflictState();
    }

    private static SettingsViewModel? ResolveSettingsViewModel(object? dataContext)
    {
        return dataContext switch
        {
            SettingsViewModel settings => settings,
            MainWindowViewModel mainWindow => mainWindow.Settings,
            _ => null
        };
    }

    private void OnSettingsViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null ||
            e.PropertyName.StartsWith("Hotkey", System.StringComparison.Ordinal) ||
            e.PropertyName == nameof(SettingsViewModel.HasHotkeyConflicts))
        {
            ApplyHotkeyConflictState();
        }
    }

    private void ApplyHotkeyConflictState()
    {
        if (_settingsViewModel == null)
        {
            HotkeyConflictSummaryText.Visibility = Visibility.Collapsed;
            HotkeyConflictSummaryText.Text = string.Empty;
            HotkeyConflictSummaryText.ClearValue(TextBlock.ForegroundProperty);
            foreach (var row in _hotkeyRows.Values)
            {
                ApplyNormalState(row);
            }

            return;
        }

        if (_settingsViewModel.HasHotkeyConflicts)
        {
            HotkeyConflictSummaryText.Visibility = Visibility.Visible;
            HotkeyConflictSummaryText.Text = _settingsViewModel.HotkeyConflictSummary;
            HotkeyConflictSummaryText.SetResourceReference(TextBlock.ForegroundProperty, HotkeyConflictTextBrushKey);
        }
        else
        {
            HotkeyConflictSummaryText.Visibility = Visibility.Collapsed;
            HotkeyConflictSummaryText.Text = string.Empty;
            HotkeyConflictSummaryText.ClearValue(TextBlock.ForegroundProperty);
        }

        foreach (var pair in _hotkeyRows)
        {
            if (_settingsViewModel.TryGetHotkeyConflict(pair.Key, out var message))
            {
                ApplyConflictState(pair.Value, message);
            }
            else
            {
                ApplyNormalState(pair.Value);
            }
        }
    }

    private static void ApplyConflictState(HotkeyRowVisual row, string message)
    {
        row.Container.SetResourceReference(Border.BorderBrushProperty, HotkeyConflictBorderBrushKey);
        row.Container.SetResourceReference(Border.BackgroundProperty, HotkeyConflictBackgroundBrushKey);
        row.Label.SetResourceReference(TextBlock.ForegroundProperty, HotkeyConflictTextBrushKey);
        row.Hint.Text = message;
        row.Hint.Visibility = Visibility.Visible;
        row.Hint.SetResourceReference(TextBlock.ForegroundProperty, HotkeyConflictTextBrushKey);
    }

    private static void ApplyNormalState(HotkeyRowVisual row)
    {
        row.Container.ClearValue(Border.BorderBrushProperty);
        row.Container.ClearValue(Border.BackgroundProperty);
        row.Label.ClearValue(TextBlock.ForegroundProperty);
        row.Hint.Text = string.Empty;
        row.Hint.Visibility = Visibility.Collapsed;
        row.Hint.ClearValue(TextBlock.ForegroundProperty);
    }

    private readonly record struct HotkeyRowVisual(
        Border Container,
        TextBlock Label,
        TextBlock Hint);
}
