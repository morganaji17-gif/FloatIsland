using System.Globalization;
using System.Windows.Data;
using System.Windows;
using System.Windows.Media;

namespace FloatIsland.Converters;

public class ProgressFillConverter : IMultiValueConverter
{
    public static readonly ProgressFillConverter Instance = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0.0;
        if (values[0] is not double val) return 0.0;
        if (values[1] is not double max || max <= 0) return 0.0;
        double trackWidth = 400;
        if (values.Length > 2 && values[2] is double dw) trackWidth = dw;
        return Math.Max(0, Math.Min(trackWidth, (val / max) * trackWidth));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var invert = parameter?.ToString() == "Invert";
        var v = value is bool b && b;
        return (invert ? !v : v) ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
