using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bosun.Panel.Memory;

/// <summary>Visible when a flag is <see cref="When"/>, collapsed otherwise: the history card's chart, table and buttons (D03 T01 §3).</summary>
public sealed class ShownWhen : IValueConverter
{
    public bool When { get; set; } = true;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool on && on == When ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
