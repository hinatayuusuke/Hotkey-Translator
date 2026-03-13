using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI;

public partial class OverviewControl : UserControl
{
    public static readonly DependencyProperty RoiPresetSlotItemsSourceProperty =
        DependencyProperty.Register(
            nameof(RoiPresetSlotItemsSource),
            typeof(IEnumerable),
            typeof(OverviewControl),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SelectedRoiPresetSlotIndexProperty =
        DependencyProperty.Register(
            nameof(SelectedRoiPresetSlotIndex),
            typeof(int),
            typeof(OverviewControl),
            new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public OverviewControl()
    {
        InitializeComponent();
    }

    public event SelectionChangedEventHandler? RoiPresetSlotSelectionChanged;

    public IEnumerable? RoiPresetSlotItemsSource
    {
        get => (IEnumerable?)GetValue(RoiPresetSlotItemsSourceProperty);
        set => SetValue(RoiPresetSlotItemsSourceProperty, value);
    }

    public int SelectedRoiPresetSlotIndex
    {
        get => (int)GetValue(SelectedRoiPresetSlotIndexProperty);
        set => SetValue(SelectedRoiPresetSlotIndexProperty, value);
    }

    private void OnOverviewRoiPresetSlotSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RoiPresetSlotSelectionChanged?.Invoke(sender, e);
    }
}
