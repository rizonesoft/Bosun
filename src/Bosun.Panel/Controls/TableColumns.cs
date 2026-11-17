using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace Bosun.Panel.Controls;

/// <summary>
/// What a table column is, for the theme to draw (D02 T04 §4). <see cref="NumberProperty"/> on a column right-aligns its
/// header and draws its sort arrow before the header's text; <see cref="IsWordProperty"/> on a cell's text says it
/// shows a word where a number goes ("Not available"), drawn dim by <c>Cell.Number</c>.
/// </summary>
public static class TableColumn
{
    public static readonly DependencyProperty NumberProperty = DependencyProperty.RegisterAttached(
        "Number", typeof(bool), typeof(TableColumn), new PropertyMetadata(false));

    public static bool GetNumber(DependencyObject column) => (bool)(column ?? throw new ArgumentNullException(nameof(column))).GetValue(NumberProperty);

    public static void SetNumber(DependencyObject column, bool value) => (column ?? throw new ArgumentNullException(nameof(column))).SetValue(NumberProperty, value);

    /// <summary>
    /// Set by the theme on every column header: it then says, through <see cref="IsNumberHeaderProperty"/>, whether its
    /// column is a number column. The header's own <c>Column</c> is a plain property with no change notice, so a style
    /// trigger bound through it read the column before the header had one and never again (D02 T04 §4).
    /// </summary>
    public static readonly DependencyProperty WatchProperty = DependencyProperty.RegisterAttached(
        "Watch", typeof(bool), typeof(TableColumn), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not DataGridColumnHeader header || e.NewValue is not true) return;
            header.Loaded += (_, _) => Read(header);
            header.DataContextChanged += (_, _) => Read(header);
            Read(header);
        }));

    public static bool GetWatch(DependencyObject header) => (bool)(header ?? throw new ArgumentNullException(nameof(header))).GetValue(WatchProperty);

    public static void SetWatch(DependencyObject header, bool value) => (header ?? throw new ArgumentNullException(nameof(header))).SetValue(WatchProperty, value);

    /// <summary>On a column header: whether its column is a number column, for the theme's triggers.</summary>
    public static readonly DependencyProperty IsNumberHeaderProperty = DependencyProperty.RegisterAttached(
        "IsNumberHeader", typeof(bool), typeof(TableColumn), new PropertyMetadata(false));

    public static bool GetIsNumberHeader(DependencyObject header) => (bool)(header ?? throw new ArgumentNullException(nameof(header))).GetValue(IsNumberHeaderProperty);

    public static void SetIsNumberHeader(DependencyObject header, bool value) => (header ?? throw new ArgumentNullException(nameof(header))).SetValue(IsNumberHeaderProperty, value);

    private static void Read(DataGridColumnHeader header) => SetIsNumberHeader(header, header.Column is { } column && GetNumber(column));

    public static readonly DependencyProperty IsWordProperty = DependencyProperty.RegisterAttached(
        "IsWord", typeof(bool), typeof(TableColumn), new FrameworkPropertyMetadata(false));

    public static bool GetIsWord(DependencyObject element) => (bool)(element ?? throw new ArgumentNullException(nameof(element))).GetValue(IsWordProperty);

    public static void SetIsWord(DependencyObject element, bool value) => (element ?? throw new ArgumentNullException(nameof(element))).SetValue(IsWordProperty, value);
}

/// <summary>
/// A column of text in a table (D02 T04 §4): its cells' text takes <c>Cell.Text</c> from the theme by resource
/// reference, set on the generated text itself. A column is not in the visual tree, so a DynamicResource set as its
/// ElementStyle never resolved and the cells drew in DataGrid's own style.
/// </summary>
public class TextColumn : DataGridTextColumn
{
    protected virtual string StyleKey => "Cell.Text";

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateElement(cell, dataItem);
        element.SetResourceReference(FrameworkElement.StyleProperty, StyleKey);
        return element;
    }
}

/// <summary>
/// A column of numbers (D02 T04 §4): right-aligned in tabular figures through <c>Cell.Number</c>, its header right-
/// aligned to match. Where a row shows a word instead (<see cref="WordPath"/>, a bool), the word is drawn dim with its
/// reason (<see cref="WhyPath"/>) as tooltip and help text, as SPEC §1 says a value that can't be read is shown.
/// </summary>
public sealed class NumberColumn : TextColumn
{
    public NumberColumn() => TableColumn.SetNumber(this, true);

    protected override string StyleKey => "Cell.Number";

    /// <summary>The row's bool that says this cell shows a word, not a number.</summary>
    public string? WordPath { get; set; }

    /// <summary>The row's text that says why the word is shown.</summary>
    public string? WhyPath { get; set; }

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var element = base.GenerateElement(cell, dataItem);
        if (WordPath is { } word) element.SetBinding(TableColumn.IsWordProperty, new Binding(word));
        if (WhyPath is { } why)
        {
            element.SetBinding(FrameworkElement.ToolTipProperty, new Binding(why));
            element.SetBinding(AutomationProperties.HelpTextProperty, new Binding(why));
        }
        return element;
    }
}
