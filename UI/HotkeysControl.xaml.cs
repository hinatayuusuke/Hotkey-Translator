using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI;

public partial class HotkeysControl : UserControl
{
    public static readonly DependencyProperty HotkeyKeyOptionsProperty = DependencyProperty.Register(
        nameof(HotkeyKeyOptions),
        typeof(IEnumerable<string>),
        typeof(HotkeysControl),
        new PropertyMetadata(null, OnHotkeyKeyOptionsChanged));

    public HotkeysControl()
    {
        InitializeComponent();
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
}
