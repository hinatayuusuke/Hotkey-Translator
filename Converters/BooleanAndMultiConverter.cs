using System;
using System.Globalization;
using System.Linq;
using System.Windows.Data;

namespace Hotkey_Translator;

public sealed class BooleanAndMultiConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0)
        {
            return false;
        }

        return values.All(v => v is bool b && b);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
