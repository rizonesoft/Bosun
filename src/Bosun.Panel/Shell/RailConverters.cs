using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Shell;

/// <summary>A rail row's glyph, from its section's key through the one icon map (D02 T03 §3).</summary>
public sealed class PageGlyph : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string key ? Glyphs.ForPage(key) : DependencyProperty.UnsetValue;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// A rail row's name as a screen reader says it (D02 T03 §3): its title, and its count in words while it has one
/// ("Queue, 3 waiting"). The badge says the number to the eye; this says it to the ear, once.
/// </summary>
public sealed class RailRowName : IMultiValueConverter
{
    public static string Of(string title, int waiting) => waiting > 0 ? string.Create(CultureInfo.InvariantCulture, $"{title}, {waiting} waiting") : title;

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values is [string title, int waiting] ? Of(title, waiting) : values.FirstOrDefault() as string ?? "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
