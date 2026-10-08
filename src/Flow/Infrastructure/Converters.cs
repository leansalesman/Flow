using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Flow.Infrastructure;

public static class TimeFormat
{
    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static string Format(double seconds) =>
        Format(TimeSpan.FromSeconds(double.IsFinite(seconds) ? seconds : 0));

    public static string Long(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} hr {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";
}

public sealed class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? TimeFormat.Format(d) : "0:00";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value switch
        {
            bool v => v,
            int i => i != 0,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            _ => true
        };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Returns true when the bound value's string form equals the parameter (for enum radio buttons).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        var p = parameter?.ToString() ?? "";
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t.IsEnum) return Enum.Parse(t, p);
        if (t == typeof(bool)) return bool.Parse(p);
        return p;
    }
}

/// <summary>Picks one of two strings separated by '|' in the parameter based on a bool.</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter?.ToString() ?? "|").Split('|');
        return value is true ? parts[0] : parts.Length > 1 ? parts[1] : "";
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class RepeatGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Services.RepeatMode.One ? "" : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class RepeatActiveConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Services.RepeatMode m && m != Services.RepeatMode.Off;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class VolumeGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        bool muted = values.Length > 1 && values[1] is true;
        double v = values.Length > 0 && values[0] is double d ? d : 1;
        if (muted || v <= 0.001) return "";
        if (v < 0.34) return "";
        if (v < 0.67) return "";
        return "";
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class DbFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is double d ? (d > 0 ? $"+{d:0.#}" : $"{d:0.#}") : "0";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
