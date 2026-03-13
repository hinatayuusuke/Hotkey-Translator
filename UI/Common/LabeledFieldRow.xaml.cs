using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI.Common;

public partial class LabeledFieldRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(
            nameof(Label),
            typeof(string),
            typeof(LabeledFieldRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(LabeledFieldRow),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelWidthProperty =
        DependencyProperty.Register(
            nameof(LabelWidth),
            typeof(double),
            typeof(LabeledFieldRow),
            new PropertyMetadata(150d));

    public static readonly DependencyProperty FieldContentProperty =
        DependencyProperty.Register(
            nameof(FieldContent),
            typeof(object),
            typeof(LabeledFieldRow),
            new PropertyMetadata(null));

    public LabeledFieldRow()
    {
        InitializeComponent();
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public double LabelWidth
    {
        get => (double)GetValue(LabelWidthProperty);
        set => SetValue(LabelWidthProperty, value);
    }

    public object? FieldContent
    {
        get => GetValue(FieldContentProperty);
        set => SetValue(FieldContentProperty, value);
    }
}
