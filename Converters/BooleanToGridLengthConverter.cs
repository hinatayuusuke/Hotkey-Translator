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

        var parameterText = parameter as string;

        // NOTE: "star" and weighted star parameters let one converter serve fixed-height rows
        // and fixed-ratio pane columns without introducing a second converter.
        if (TryParseStarLength(parameterText, out var starLength))
        {
            return starLength;
        }

        if (string.Equals(parameterText, "star", StringComparison.OrdinalIgnoreCase))
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

    private static bool TryParseStarLength(string? parameterText, out GridLength starLength)
    {
        starLength = default;
        if (string.IsNullOrWhiteSpace(parameterText))
        {
            return false;
        }

        if (!parameterText.EndsWith('*'))
        {
            return false;
        }

        var weightText = parameterText[..^1];
        if (weightText.Length == 0)
        {
            starLength = new GridLength(1, GridUnitType.Star);
            return true;
        }

        if (!double.TryParse(weightText, NumberStyles.Float, CultureInfo.InvariantCulture, out var weight)
            || weight <= 0)
        {
            return false;
        }

        starLength = new GridLength(weight, GridUnitType.Star);
        return true;
    }
}
