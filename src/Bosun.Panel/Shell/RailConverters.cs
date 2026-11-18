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
/// A rail row's name as a screen reader says it (D02 T03 §3): its title, and its count in words while it has one, each
/// kind said as what it is (D03 T01 §5): "Queue, 3 waiting", "Memory, 1 needs you", "Queue, 2 waiting, 1 needs you".
/// The badge says the sum to the eye; this says what it counts to the ear, once.
/// </summary>
public sealed class RailRowName : IMultiValueConverter
{
    public static string Of(string title, int waiting, int needsYou = 0)
    {
        var parts = new List<string>();
        if (waiting > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{waiting} waiting"));
        if (needsYou > 0) parts.Add(string.Create(CultureInfo.InvariantCulture, $"{needsYou} {(needsYou == 1 ? "needs" : "need")} you"));
        return parts.Count == 0 ? title : $"{title}, {string.Join(", ", parts)}";
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) => values switch
    {
        [string title, int waiting, int needsYou] => Of(title, waiting, needsYou),
        [string title, int waiting] => Of(title, waiting),
        _ => values.FirstOrDefault() as string ?? "",
    };

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
