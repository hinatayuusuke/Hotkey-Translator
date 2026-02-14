using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hotkey_Translator;

public sealed class IntToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int intValue)
        {
            return Visibility.Collapsed;
        }

        if (!int.TryParse(parameter?.ToString(), out var target))
        {
            return Visibility.Collapsed;
        }

        return intValue == target ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
