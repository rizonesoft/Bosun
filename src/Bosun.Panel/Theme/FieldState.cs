using System.Windows;

namespace Bosun.Panel.Theme;

/// <summary>
/// A field's own state for the theme to draw (D02 T04 §2): <see cref="InvalidProperty"/> true draws its border in the
/// danger colour, so an error shows on the field and not only in the message under it. Set from the page's own
/// validation, never through WPF's: that is read-only from a view model and adds an error adorner of its own, a second
/// outline beside the focus ring.
/// </summary>
public static class FieldState
{
    public static readonly DependencyProperty InvalidProperty = DependencyProperty.RegisterAttached(
        "Invalid", typeof(bool), typeof(FieldState), new FrameworkPropertyMetadata(false));

    public static bool GetInvalid(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(InvalidProperty);
    }

    public static void SetInvalid(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(InvalidProperty, value);
    }
}
