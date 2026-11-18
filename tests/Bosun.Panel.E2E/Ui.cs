using System.Windows;
using System.Windows.Automation;

namespace Bosun.Panel.E2E;

/// <summary>What the smoke asks of UI Automation, said once: find, wait, and read.</summary>
static class Ui
{
    /// <summary>Asks until the answer is not null, or fails the test after <paramref name="limit"/> naming what it waited for.</summary>
    public static T Until<T>(Func<T?> probe, TimeSpan limit, string what) where T : class
    {
        var end = DateTime.UtcNow + limit;
        Exception? last = null;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
            }
            catch (ElementNotAvailableException e)
            {
                last = e; // the element went away while it was read: ask again
            }
            if (DateTime.UtcNow > end) throw new TimeoutException($"waited {limit.TotalSeconds:0.#} s for {what}" + (last is null ? "" : $" ({last.Message})"));
            Thread.Sleep(50);
        }
    }

    /// <summary>Asks until true, or fails the test after <paramref name="limit"/> naming what it waited for.</summary>
    public static void Until(Func<bool> probe, TimeSpan limit, string what) =>
        Until(() => probe() ? (object)true : null, limit, what);

    public static readonly TimeSpan Short = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits until the keyboard focus is on an element <paramref name="wanted"/> accepts, or fails naming where the
    /// focus was instead, its type, name and automation id, so a keyboard path that goes astray says where it went.
    /// </summary>
    public static void UntilFocused(Func<AutomationElement, bool> wanted, TimeSpan limit, string what)
    {
        var end = DateTime.UtcNow + limit;
        string seen = "nothing";
        while (true)
        {
            try
            {
                if (Focused() is { } focus)
                {
                    if (wanted(focus)) return;
                    seen = $"{focus.Current.ControlType.ProgrammaticName} '{focus.Current.Name}' ({focus.Current.AutomationId})";
                }
            }
            catch (ElementNotAvailableException)
            {
                // the element went away while it was read: ask again
            }
            if (DateTime.UtcNow > end) throw new TimeoutException($"waited {limit.TotalSeconds:0.#} s for {what}; the focus was on {seen}");
            Thread.Sleep(50);
        }
    }

    /// <summary>Asks until true, for at most <paramref name="limit"/>: whether it became true, without failing the test.</summary>
    public static bool Wait(Func<bool> probe, TimeSpan limit)
    {
        var end = DateTime.UtcNow + limit;
        while (!probe())
        {
            if (DateTime.UtcNow > end) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    /// <summary>A shown top-level window of a process, by its title.</summary>
    public static AutomationElement? TopLevel(int processId, string name) =>
        AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.NameProperty, name)));

    /// <summary>An element under <paramref name="scope"/> by its automation id (a WPF element's x:Name), or null.</summary>
    public static AutomationElement? Find(AutomationElement scope, string id) =>
        scope.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    /// <summary>As <see cref="Find"/>, waiting up to ten seconds for it.</summary>
    public static AutomationElement Get(AutomationElement scope, string id) => Until(() => Find(scope, id), Short, $"'{id}' to be on screen");

    public static AutomationElement? Named(AutomationElement scope, string name, ControlType? type = null)
    {
        System.Windows.Automation.Condition condition = new PropertyCondition(AutomationElement.NameProperty, name);
        if (type is not null) condition = new AndCondition(condition, new PropertyCondition(AutomationElement.ControlTypeProperty, type));
        return scope.FindFirst(TreeScope.Descendants, condition);
    }

    public static void Invoke(AutomationElement element) => ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    public static void Select(AutomationElement element) => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();

    public static bool IsSelected(AutomationElement element) => ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Current.IsSelected;

    public static ToggleState Toggle(AutomationElement element) => ((TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern)).Current.ToggleState;

    public static ExpandCollapseState Expansion(AutomationElement element) =>
        ((ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Current.ExpandCollapseState;

    /// <summary>The middle of an element, in screen pixels.</summary>
    public static (int X, int Y) Centre(AutomationElement element)
    {
        var box = element.Current.BoundingRectangle;
        if (box.IsEmpty) throw new InvalidOperationException($"'{element.Current.Name}' has no place on screen");
        return ((int)Math.Round(box.X + box.Width / 2), (int)Math.Round(box.Y + box.Height / 2));
    }

    public static Rect Box(AutomationElement element) => element.Current.BoundingRectangle;

    /// <summary>The element with keyboard focus, anywhere on the desktop.</summary>
    public static AutomationElement? Focused()
    {
        try
        {
            return AutomationElement.FocusedElement;
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>The selected item of a list or combo box, or null.</summary>
    public static AutomationElement? Selected(AutomationElement container) =>
        ((SelectionPattern)container.GetCurrentPattern(SelectionPattern.Pattern)).Current.GetSelection().FirstOrDefault();

    /// <summary>
    /// The page on screen: its level 1 heading's text, and its state as the page frame shows it (src/Bosun.Panel/Shell/
    /// PageFrame.xaml). A collapsed state is not in the tree, so the one state present is the page's. The Data state is
    /// the page's own content, which has no element of the frame's own in the tree (a ContentControl has no automation
    /// peer), so it is known by <paramref name="dataMark"/>, the automation id of a control the page draws.
    /// </summary>
    public static (string Title, string? State) Page(AutomationElement window, string? dataMark = null)
    {
        var heading = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.HeadingLevelProperty, AutomationHeadingLevel.Level1));
        string? state = null;
        var marks = new List<(string Id, string State)> { ("Loading", "Loading"), ("EmptyReason", "Empty"), ("FailedHeading", "Failed") };
        if (dataMark is not null) marks.Add((dataMark, "Data"));
        foreach (var (id, name) in marks)
            if (Find(window, id) is { } shown && !shown.Current.IsOffscreen) state = state is null ? name : $"{state} and {name}";
        return (heading?.Current.Name ?? "", state);
    }

    /// <summary>
    /// Every element a person can act on, under <paramref name="scope"/>, that says nothing to a screen reader. The
    /// control types are those a person operates; a focusable element of any type is one too.
    /// </summary>
    public static List<string> Nameless(AutomationElement scope)
    {
        var acting = new[]
        {
            ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.Hyperlink, ControlType.List,
            ControlType.ListItem, ControlType.MenuItem, ControlType.RadioButton, ControlType.Slider, ControlType.Spinner, ControlType.SplitButton,
            ControlType.Tab, ControlType.TabItem, ControlType.Thumb, ControlType.Tree, ControlType.TreeItem, ControlType.DataGrid, ControlType.DataItem,
        };
        var nameless = new List<string>();
        var walker = TreeWalker.ControlViewWalker;
        void Visit(AutomationElement element, string path)
        {
            var current = element.Current;
            var here = $"{path}/{current.ControlType.ProgrammaticName.Replace("ControlType.", "")}" + (current.AutomationId is { Length: > 0 } id ? $"#{id}" : "");
            // Disabled or not: a control that is off says what it is, as one that is on does.
            if ((acting.Contains(current.ControlType) || current.IsKeyboardFocusable) && string.IsNullOrWhiteSpace(current.Name))
                nameless.Add(here);
            for (var child = walker.GetFirstChild(element); child is not null; child = walker.GetNextSibling(child)) Visit(child, here);
        }
        Visit(scope, "");
        return nameless;
    }
}
