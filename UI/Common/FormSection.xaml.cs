using System.Windows;
using System.Windows.Controls;

namespace Hotkey_Translator.UI.Common;

public partial class FormSection : UserControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(
            nameof(Header),
            typeof(string),
            typeof(FormSection),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(
            nameof(Description),
            typeof(string),
            typeof(FormSection),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SectionContentProperty =
        DependencyProperty.Register(
            nameof(SectionContent),
            typeof(object),
            typeof(FormSection),
            new PropertyMetadata(null));

    public FormSection()
    {
        InitializeComponent();
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }
}
