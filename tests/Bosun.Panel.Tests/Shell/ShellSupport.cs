using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;

namespace Bosun.Panel.Tests.Shell;

/// <summary>A window of the tests' own: it does what a window does to its state and writes down what it was asked.</summary>
public sealed class FakeWindow : IWindowSurface
{
    public List<string> Calls { get; } = [];

    public bool IsVisible { get; set; }

    public bool IsMinimised { get; set; }

    public bool IsMaximised { get; set; }

    /// <summary>What a real window asks as it closes: may it? Null lets every close through.</summary>
    public Func<bool>? Closing { get; set; }

    public int Closed { get; private set; }

    public void Show()
    {
        Calls.Add("show");
        IsVisible = true;
    }

    public void Restore()
    {
        Calls.Add("restore");
        IsMinimised = false;
    }

    public void Activate() => Calls.Add("activate");

    public void Minimise()
    {
        Calls.Add("minimise");
        IsMinimised = true;
    }

    public void ToggleMaximise()
    {
        Calls.Add("toggle");
        IsMaximised = !IsMaximised;
    }

    public void Close()
    {
        Calls.Add("close");
        if (Closing is null || Closing()) Closed++;
    }
}

/// <summary>What the shell's tests share: finding things in a drawn view, and doing to them what a person does.</summary>
static class ShellSupport
{
    public static IEnumerable<T> Under<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var deeper in Under<T>(child)) yield return deeper;
        }
    }

    public static Color? Paint(object? brush) => (brush as SolidColorBrush)?.Color;

    /// <summary>Presses a button as UI Automation does, which is how a screen reader does.</summary>
    public static void Invoke(Button button) =>
        ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    /// <summary>The rail's row for an index, grouped or not.</summary>
    public static ListBoxItem Row(ListBox rail, int index) => Under<ListBoxItem>(rail).Single(row => ReferenceEquals(row.DataContext, rail.Items[index]));

    /// <summary>A mouse press on a rail row, as the list first sees it; true when the guard stopped it there.</summary>
    public static bool Press(ListBox rail, int index)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent };
        Row(rail, index).RaiseEvent(press);
        return press.Handled;
    }

    /// <summary>A key on a rail row, as the list first sees it; true when the guard stopped it there.</summary>
    public static bool KeyOn(ListBox rail, int onRow, Key key)
    {
        var press = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(rail)!, 0, key) { RoutedEvent = UIElement.PreviewKeyDownEvent };
        Row(rail, onRow).RaiseEvent(press);
        return press.Handled;
    }

    /// <summary>Selects a rail row as UI Automation does: no click and no key to stop.</summary>
    public static void Select(ListBox rail, int index)
    {
        var item = rail.Items[index];
        var peer = Flatten(new ListBoxAutomationPeer(rail)).OfType<ListBoxItemAutomationPeer>().Single(row => ReferenceEquals(row.Item, item));
        ((ISelectionItemProvider)peer.GetPattern(PatternInterface.SelectionItem)).Select();
    }

    private static IEnumerable<AutomationPeer> Flatten(AutomationPeer peer)
    {
        foreach (var child in peer.GetChildren() ?? [])
        {
            yield return child;
            foreach (var deeper in Flatten(child)) yield return deeper;
        }
    }

    /// <summary>A key pressed and released on an element.</summary>
    public static void Key_(UIElement on, Key key)
    {
        var source = PresentationSource.FromVisual(on)!;
        on.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyDownEvent });
        on.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key) { RoutedEvent = Keyboard.KeyUpEvent });
    }

    /// <summary>A drag of a splitter by a distance, from press to release.</summary>
    public static void Drag(Thumb splitter, double vertical)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0));
        splitter.RaiseEvent(new DragDeltaEventArgs(0, vertical));
        splitter.RaiseEvent(new DragCompletedEventArgs(0, vertical, false));
    }

    /// <summary>Draws a shell view over a shell in a page that is never shown, at the size the renders use, and runs the test there.</summary>
    public static void InShell(ShellViewModel? shell, Action<ShellView, ShellViewModel, System.Windows.Controls.Border> test, string theme = "Dark", Func<ShellViewModel>? make = null)
    {
        ShellView view = null!;
        ShellViewModel model = null!;
        PageRenderer.InPage(theme, () =>
        {
            model = shell ?? (make ?? Shells.Panel)();
            view = new ShellView { DataContext = model };
            TestPage.AddTemplate(view);
            return view;
        }, page =>
        {
            test(view, model, page);
            return true;
        }, PageRenderer.Width, PageRenderer.Height);
    }
}
