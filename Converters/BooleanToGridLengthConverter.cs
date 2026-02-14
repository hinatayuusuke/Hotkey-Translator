using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Hotkey_Translator;

public sealed class BooleanToGridLengthConverter : IValueConverter
{
    public GridLength TrueLength { get; set; } = new(1, GridUnitType.Star);

    public GridLength FalseLength { get; set; } = new(0, GridUnitType.Pixel);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var boolValue = value is bool b && b;
        if (!boolValue)
        {
            return FalseLength;
        }

        // NOTE: "star" parameter lets one converter serve fixed-height rows and star-sized pane columns.
        if (string.Equals(parameter as string, "star", StringComparison.OrdinalIgnoreCase))
        {
            return new GridLength(1, GridUnitType.Star);
        }

        return TrueLength;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is GridLength gridLength)
        {
            return gridLength.Value > 0;
        }

        return false;
    }
}
