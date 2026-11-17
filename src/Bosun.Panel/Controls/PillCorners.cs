using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Bosun.Panel.Controls;

/// <summary>
/// A pill's corners from its drawn height: half of it, so its ends are true semicircles (D02 T04 §6 completion review).
/// A radius larger than half the height, as the R.Pill token's 999 is, WPF draws as an ellipse: a bordered pill read as
/// a lens.
/// </summary>
public sealed class PillCorners : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && height > 0 ? new CornerRadius(height / 2) : new CornerRadius(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
