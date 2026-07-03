using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AxisSdReader.App;

/// <summary>True → Collapsed, False → Visible.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound number equals the numeric ConverterParameter (used for active speed).</summary>
public sealed class NumberEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return double.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
            && Math.Abs(v - p) < 0.0001;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when the bound string equals the ConverterParameter (used for active export format).</summary>
public sealed class StringEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Blue when the bound rate equals the ConverterParameter, else transparent (active speed fill).</summary>
public sealed class RateActiveBackgroundConverter : IValueConverter
{
    private static readonly Brush Active = Freeze("#2F6FED");
    private static readonly Brush Inactive = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        RateMatches(value, parameter) ? Active : Inactive;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    internal static bool RateMatches(object? value, object? parameter) =>
        double.TryParse(System.Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
        && double.TryParse(parameter?.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
        && Math.Abs(v - p) < 0.0001;

    internal static Brush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}

/// <summary>White when the bound rate equals the ConverterParameter, else muted grey (active speed text).</summary>
public sealed class RateActiveForegroundConverter : IValueConverter
{
    private static readonly Brush Active = RateActiveBackgroundConverter.Freeze("#FFFFFF");
    private static readonly Brush Inactive = RateActiveBackgroundConverter.Freeze("#6B747F");

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        RateActiveBackgroundConverter.RateMatches(value, parameter) ? Active : Inactive;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
