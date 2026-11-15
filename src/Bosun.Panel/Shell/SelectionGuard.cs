using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Bosun.Panel.Shell;

/// <summary>
/// Asks before a list or tab strip moves its selection, so a page can refuse (Keep editing) (D02 T02 §2, after
/// Steward's SelectionGuard).
///
/// A click or a key is asked about before the control acts on it, so a refused item is never selected at all: undoing
/// a selection after the fact shows the refused item for a moment, and a tab selects itself again when keyboard focus
/// returns to it from a prompt. A move that can't be seen coming (UI Automation's select, type-ahead, Page Up and
/// Page Down, a binding) is asked about when it happens, and put back when it is refused.
/// </summary>
public static class SelectionGuard
{
    /// <param name="mayGoTo">Given the index the selection would move to; false keeps it where it is.</param>
    public static void Attach(Selector selector, Func<int, bool> mayGoTo)
    {
        // Where the selection is, as far as the guard has agreed; the index it agreed to beforehand, if any.
        var at = selector.SelectedIndex;
        int? approved = null;
        var puttingBack = false;

        bool Ask(int index)
        {
            if (!mayGoTo(index)) return false;
            approved = index;
            return true;
        }

        selector.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Container(selector, e.OriginalSource) is { } index && index != selector.SelectedIndex && !Ask(index)) e.Handled = true;
        };

        selector.PreviewKeyDown += (_, e) =>
        {
            if (Target(selector, e.Key, Keyboard.Modifiers, e.OriginalSource) is { } index && index != selector.SelectedIndex && !Ask(index)) e.Handled = true;
        };

        selector.SelectionChanged += (_, e) =>
        {
            // A list inside a page inside a tab raises its own SelectionChanged up through this one.
            if (!ReferenceEquals(e.OriginalSource, selector) || puttingBack) return;
            var to = selector.SelectedIndex;
            if (to == at) return;
            // Agreed beforehand (a click or a key), a selection that was cleared (its items were replaced), or a move
            // nobody saw coming that is agreed to now.
            if (approved == to || to < 0 || mayGoTo(to))
            {
                approved = null;
                at = to;
                return;
            }
            approved = null;
            puttingBack = true;
            try
            {
                // The current value, not a local one: setting the property itself would replace a one-way binding
                // on SelectedIndex, and the view model's later moves would no longer reach the list.
                selector.SetCurrentValue(Selector.SelectedIndexProperty, at);
            }
            finally
            {
                puttingBack = false;
            }
            // A view model bound two-way made this move and still holds the refused value: WPF does not write back
            // to a source while it is reading from it. Once that is over, it is told where the selection really is.
            selector.Dispatcher.BeginInvoke(() => TellTheSource(selector));
        };
    }

    private static readonly DependencyProperty[] Selections = [Selector.SelectedIndexProperty, Selector.SelectedItemProperty, Selector.SelectedValueProperty];

    private static void TellTheSource(Selector selector)
    {
        foreach (var property in Selections)
        {
            if (BindingOperations.GetBindingExpression(selector, property) is { ParentBinding.Mode: BindingMode.TwoWay or BindingMode.OneWayToSource or BindingMode.Default } bound)
                bound.UpdateSource();
        }
    }

    /// <summary>The index of the item container a click landed in, or null outside every item.</summary>
    private static int? Container(Selector selector, object source)
    {
        for (var node = source as DependencyObject; node is not null && !ReferenceEquals(node, selector); node = Parent(node))
        {
            if (node is TabItem or ListBoxItem)
            {
                // A grouped list generates its containers per group, so the outer generator does not know them; the
                // container's data does.
                var item = selector.ItemContainerGenerator.ItemFromContainer(node);
                if (item == DependencyProperty.UnsetValue) item = ((FrameworkElement)node).DataContext;
                var index = selector.Items.IndexOf(item);
                return index >= 0 ? index : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Where a key would move the selection: arrows, Home and End only while an item itself has focus (never in a
    /// text box inside a tab), and Ctrl+Tab from anywhere in a tab strip, as WPF moves them. Null when the key moves
    /// nothing. The modifiers are given, not read, so this can be asked without a keyboard.
    /// </summary>
    internal static int? Target(Selector selector, Key key, ModifierKeys modifiers, object? source)
    {
        var count = selector.Items.Count;
        var at = selector.SelectedIndex;
        if (count == 0 || at < 0) return null;

        if (selector is TabControl && key == Key.Tab && modifiers.HasFlag(ModifierKeys.Control))
            return (at + (modifiers.HasFlag(ModifierKeys.Shift) ? count - 1 : 1)) % count;

        if (source is not (TabItem or ListBoxItem)) return null;

        int? to = key switch
        {
            Key.Left or Key.Up => at - 1,
            Key.Right or Key.Down => at + 1,
            Key.Home => 0,
            Key.End => count - 1,
            _ => null,
        };
        return to is >= 0 && to < count ? to : null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
