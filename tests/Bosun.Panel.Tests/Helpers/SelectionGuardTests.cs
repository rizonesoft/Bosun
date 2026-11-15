using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// The guard that asks before a selection moves (D02 T02 §2), on a real list in the rail's style, in a page that is
/// never shown: a click or a key is refused before the list acts on it, and a move nobody saw coming (UI
/// Automation's select, a binding) is asked about and put back.
/// </summary>
public class SelectionGuardTests
{
    private static readonly string[] Pages = ["Overview", "Memory", "Instances", "Queue"];

    /// <summary>A guarded rail showing its second row, and what the guard was asked.</summary>
    private static void WithARail(Action<ListBox, List<int>, Action<bool>, Border> test)
    {
        ListBox rail = null!;
        var asked = new List<int>();
        var answer = false;
        PageRenderer.InPage("Dark", () =>
        {
            rail = new ListBox { ItemsSource = Pages, SelectedIndex = 1 };
            rail.SetResourceReference(FrameworkElement.StyleProperty, "NavList");
            return rail;
        }, page =>
        {
            SelectionGuard.Attach(rail, index =>
            {
                asked.Add(index);
                return answer;
            });
            test(rail, asked, value => answer = value, page);
            return true;
        });
    }

    private static ListBoxItem Row(ListBox rail, int index) => (ListBoxItem)rail.ItemContainerGenerator.ContainerFromIndex(index);

    /// <summary>A mouse press on a row, as the list first sees it; true when the guard stopped it there.</summary>
    private static bool Press(ListBox rail, int index)
    {
        // PreviewMouseDown is the event that travels down to the row; each element on the way turns it into its own
        // PreviewMouseLeftButtonDown, which is what the guard listens for, and a handler's refusal comes back on it.
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        Row(rail, index).RaiseEvent(press);
        return press.Handled;
    }

    /// <summary>A key on a row, as the list first sees it; true when the guard stopped it there.</summary>
    private static bool Key_(ListBox rail, int onRow, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rail)!, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        Row(rail, onRow).RaiseEvent(press);
        return press.Handled;
    }

    [Fact]
    public void A_click_on_another_row_is_stopped_when_refused_and_left_alone_when_agreed_or_on_the_selected_row()
    {
        WithARail((rail, asked, answer, _) =>
        {
            Assert.True(Press(rail, 3));
            Assert.Equal([3], asked);
            Assert.Equal(1, rail.SelectedIndex);

            Assert.False(Press(rail, 1)); // the row already shown: nothing to ask
            Assert.Equal([3], asked);

            answer(true);
            Assert.False(Press(rail, 2));
            Assert.Equal([3, 2], asked);
        });
    }

    [Theory]
    [InlineData(Key.Down, 2)]
    [InlineData(Key.Right, 2)]
    [InlineData(Key.Up, 0)]
    [InlineData(Key.Left, 0)]
    [InlineData(Key.Home, 0)]
    [InlineData(Key.End, 3)]
    public void A_key_that_would_move_the_selection_is_stopped_when_refused_and_left_alone_when_agreed(Key key, int to)
    {
        WithARail((rail, asked, answer, _) =>
        {
            Assert.True(Key_(rail, 1, key));
            Assert.Equal([to], asked);
            Assert.Equal(1, rail.SelectedIndex);

            answer(true);
            Assert.False(Key_(rail, 1, key));
            Assert.Equal([to, to], asked);
        });
    }

    [Fact]
    public void A_key_that_moves_nothing_is_not_asked_about()
    {
        WithARail((rail, asked, _, _) =>
        {
            Assert.False(Key_(rail, 1, Key.Enter));
            Assert.False(Key_(rail, 1, Key.A));
            rail.SelectedIndex = 0; // not a key's doing: asked about and refused, so it is back on the second row
            asked.Clear();
            Assert.False(Key_(rail, 1, Key.Tab));
            Assert.Empty(asked);
        });
    }

    [Fact]
    public void A_move_nobody_saw_coming_is_asked_about_once_and_put_back_when_refused()
    {
        WithARail((rail, asked, answer, page) =>
        {
            // As a screen reader selects a row: no click and no key to stop.
            // The row's selection pattern is on the list's peer for the row, as a client finds it.
            var select = (ISelectionItemProvider)new ListBoxAutomationPeer(rail).GetChildren()[3].GetPattern(PatternInterface.SelectionItem);
            select.Select();
            PageRenderer.Settle(page);
            Assert.Equal([3], asked);
            Assert.Equal(1, rail.SelectedIndex);
            Assert.Equal("Memory", rail.SelectedItem);
            Assert.True(Row(rail, 1).IsSelected);
            Assert.False(Row(rail, 3).IsSelected); // not left drawn as selected

            // As a binding or code moves it.
            rail.SelectedIndex = 0;
            PageRenderer.Settle(page);
            Assert.Equal([3, 0], asked);
            Assert.Equal(1, rail.SelectedIndex);
            Assert.True(Row(rail, 1).IsSelected);
            Assert.False(Row(rail, 0).IsSelected);

            answer(true);
            select.Select();
            PageRenderer.Settle(page);
            Assert.Equal([3, 0, 3], asked);
            Assert.Equal(3, rail.SelectedIndex);
            Assert.True(Row(rail, 3).IsSelected);

            // And from where it now is, a refusal puts it back there, not where it began.
            answer(false);
            rail.SelectedIndex = 2;
            PageRenderer.Settle(page);
            Assert.Equal(3, rail.SelectedIndex);
        });
    }

    [Fact]
    public void A_move_the_guard_agreed_to_beforehand_is_not_asked_about_again_when_it_happens()
    {
        WithARail((rail, asked, answer, page) =>
        {
            answer(true);
            Assert.False(Press(rail, 2)); // agreed to at the click
            rail.SelectedIndex = 2;       // and then the list selects it
            PageRenderer.Settle(page);
            Assert.Equal([2], asked);
            Assert.Equal(2, rail.SelectedIndex);

            // The agreement was for that one move: the next one is asked about.
            answer(false);
            rail.SelectedIndex = 0;
            PageRenderer.Settle(page);
            Assert.Equal([2, 0], asked);
            Assert.Equal(2, rail.SelectedIndex);
        });
    }

    [Fact]
    public void A_list_inside_the_guarded_one_changing_its_own_selection_is_not_the_rails_business()
    {
        WithARail((rail, asked, _, page) =>
        {
            // The inner list sits inside a row's content, so its SelectionChanged comes up through the rail.
            var inner = new ListBox { ItemsSource = new[] { "a", "b" } };
            Row(rail, 1).Content = inner;
            PageRenderer.Settle(page);
            inner.SelectedIndex = 1;
            PageRenderer.Settle(page);
            Assert.Empty(asked);
            Assert.Equal(1, inner.SelectedIndex);
            Assert.Equal(1, rail.SelectedIndex);
        });
    }

    [Fact]
    public void Where_a_key_leads_is_worked_out_from_the_key_and_the_modifiers_it_is_given()
    {
        Sta.Run(() =>
        {
            var tabs = new TabControl();
            foreach (var header in new[] { "One", "Two", "Three" }) tabs.Items.Add(new TabItem { Header = header });
            tabs.SelectedIndex = 0;
            var tab = (TabItem)tabs.Items[0];
            var box = new TextBox(); // a field inside a tab's page

            // Ctrl+Tab from anywhere in the strip, wrapping both ways.
            Assert.Equal(1, SelectionGuard.Target(tabs, Key.Tab, ModifierKeys.Control, box));
            Assert.Equal(2, SelectionGuard.Target(tabs, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, box));
            tabs.SelectedIndex = 2;
            Assert.Equal(0, SelectionGuard.Target(tabs, Key.Tab, ModifierKeys.Control, box));
            Assert.Equal(1, SelectionGuard.Target(tabs, Key.Tab, ModifierKeys.Control | ModifierKeys.Shift, box));
            Assert.Null(SelectionGuard.Target(tabs, Key.Tab, ModifierKeys.None, box)); // Tab alone moves focus, not the tab

            // Arrows only while a tab itself has the keyboard: in a text box they move the caret.
            Assert.Equal(1, SelectionGuard.Target(tabs, Key.Left, ModifierKeys.None, tab));
            Assert.Null(SelectionGuard.Target(tabs, Key.Left, ModifierKeys.None, box));
            Assert.Null(SelectionGuard.Target(tabs, Key.Home, ModifierKeys.None, box));

            // Nothing past either end, and nothing with no selection or no items.
            Assert.Null(SelectionGuard.Target(tabs, Key.Right, ModifierKeys.None, tab));
            tabs.SelectedIndex = 0;
            Assert.Null(SelectionGuard.Target(tabs, Key.Left, ModifierKeys.None, tab));
            Assert.Equal(2, SelectionGuard.Target(tabs, Key.End, ModifierKeys.None, tab));
            tabs.SelectedIndex = -1;
            Assert.Null(SelectionGuard.Target(tabs, Key.Right, ModifierKeys.None, tab));
            Assert.Null(SelectionGuard.Target(new ListBox(), Key.Down, ModifierKeys.None, new ListBoxItem()));

            // A list has no Ctrl+Tab of its own.
            var list = new ListBox { ItemsSource = Pages, SelectedIndex = 0 };
            Assert.Null(SelectionGuard.Target(list, Key.Tab, ModifierKeys.Control, new ListBoxItem()));
            Assert.Equal(1, SelectionGuard.Target(list, Key.Down, ModifierKeys.None, new ListBoxItem()));
        });
    }
}
