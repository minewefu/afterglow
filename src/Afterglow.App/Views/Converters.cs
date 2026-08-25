using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace Afterglow.App.Views;

public static class Converters
{
    public static IValueConverter BoolToVisibility { get; } = new BoolToVisibilityConverter(false);

    public static IValueConverter InverseBoolToVisibility { get; } = new BoolToVisibilityConverter(true);

    public static IValueConverter SeverityToBrush { get; } = new SeverityBrushConverter();

    public static IValueConverter InverseBool { get; } = new InverseBoolConverter();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    private readonly bool _invert;

    public BoolToVisibilityConverter(bool invert)
    {
        _invert = invert;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is true;
        if (_invert)
        {
            b = !b;
        }

        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>One-way string equality (nav highlight follows the current page name).</summary>
public sealed class PageMatchConverter : IValueConverter
{
    public static PageMatchConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string current && parameter is string page &&
        current.Equals(page, StringComparison.OrdinalIgnoreCase);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

/// <summary>Binds a RadioButton group to an int index property.</summary>
public static class IndexMatch
{
    public static IValueConverter Zero { get; } = new IndexMatchConverter(0, toVisibility: false);

    public static IValueConverter One { get; } = new IndexMatchConverter(1, toVisibility: false);

    public static IValueConverter Two { get; } = new IndexMatchConverter(2, toVisibility: false);

    public static IValueConverter OneVisible { get; } = new IndexMatchConverter(1, toVisibility: true);
}

public sealed class IndexMatchConverter : IValueConverter
{
    private readonly int _index;
    private readonly bool _toVisibility;

    public IndexMatchConverter(int index, bool toVisibility)
    {
        _index = index;
        _toVisibility = toVisibility;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool match = value is int i && i == _index;
        return _toVisibility ? (match ? Visibility.Visible : Visibility.Collapsed) : match;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? _index : Binding.DoNothing;
}

public sealed class SeverityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Info = Frozen("#3D4B5C");
    private static readonly SolidColorBrush Expected = Frozen("#4A3B22");
    private static readonly SolidColorBrush Warning = Frozen("#5C4416");
    private static readonly SolidColorBrush Critical = Frozen("#5C2320");

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Core.Telemetry.ThrottleDescriber.ThrottleSeverity severity
            ? severity switch
            {
                Core.Telemetry.ThrottleDescriber.ThrottleSeverity.Expected => Expected,
                Core.Telemetry.ThrottleDescriber.ThrottleSeverity.Warning => Warning,
                Core.Telemetry.ThrottleDescriber.ThrottleSeverity.Critical => Critical,
                _ => Info,
            }
            : Info;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
