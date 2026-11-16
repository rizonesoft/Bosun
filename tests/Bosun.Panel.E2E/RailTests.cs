using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>The rail, every page on it, the names of what can be acted on, and the question about unsaved edits.</summary>
public class RailTests
{
    /// <summary>
    /// The rail as src/Bosun.Panel/Shell/Sections.cs has it today: Settings is built, the rest are placeholders. A page
    /// in Data is known by a control of its own (<see cref="DataMark"/>).
    /// </summary>
    private static readonly (string Title, string State)[] Rows =
    [
        // Memory is built (D03 T01 §2): its data is the seeded snapshot, so it shows Data.
        ("Overview", "Empty"), ("Memory", "Data"), ("Instances", "Empty"), ("Queue", "Empty"),
        ("Optimise", "Empty"), ("Settings", "Data"), ("About", "Empty"),
    ];

    [Fact]
    public void Every_rail_row_opens_its_page_in_its_state_and_every_control_has_a_name()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Window();
        var rail = Ui.Get(window, "Rail");
        // The rail has exactly these rows: a row added without an expectation here fails, by name.
        var onRail = rail.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))
            .Cast<AutomationElement>().Select(row => row.Current.Name).ToList();
        Assert.Equal(Rows.Select(row => row.Title), onRail);
        foreach (var (title, state) in Rows)
        {
            var row = Row(rail, title);
            Ui.Select(row);
            Ui.Until(() => Ui.IsSelected(row), Ui.Short, $"{title} to be the rail's selection");
            Assert.Equal(title, Ui.Selected(rail)?.Current.Name);
            var shown = Settle(window, title);
            Assert.Equal((title, state), shown);
            if (state == "Empty") Assert.Equal($"{title} is not built yet.", Ui.Get(window, "EmptyReason").Current.Name);
            Assert.Empty(Ui.Nameless(window));
        }
        Assert.True(panel.Running, "the panel still runs after every page has been shown");
    }

    [Fact]
    public void The_unsaved_changes_question_holds_the_keyboard_and_keeps_or_drops_the_edit()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var rail = Ui.Get(window, "Rail");
        Ui.Select(Row(rail, "Settings"));
        Settle(window, "Settings");
        var notify = Ui.Get(window, "NotifyMemoryPressure");
        var stored = Ui.Toggle(notify);
        ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Assert.NotEqual(stored, Ui.Toggle(notify));

        // Leaving with an edit asks, with Save focused.
        Ui.Select(Row(rail, "Overview"));
        var leave = Ui.Until(() => Ui.Find(window, "Leave") is { Current.IsOffscreen: false } card ? card : null, Ui.Short, "the unsaved-changes question");
        Assert.Equal("Unsaved changes", leave.Current.Name);
        Ui.Until(() => Ui.Focused()?.Current.AutomationId == "Save" && Inside(Ui.Focused()!, leave), Ui.Short, "Save in the question to have the keyboard");
        Assert.Empty(Ui.Nameless(leave));

        // Tab goes round the question's three buttons and never leaves it.
        var visited = new HashSet<string>();
        for (var press = 0; press < 4; press++)
        {
            RealInput.Key(handle, Native.VK_TAB);
            var focused = Ui.Until(() => Ui.Focused() is { } f && f.Current.HasKeyboardFocus ? f : null, Ui.Short, "a focused element after Tab");
            Assert.True(Inside(focused, leave), $"Tab moved the keyboard out of the question, to '{focused.Current.Name}' ({focused.Current.AutomationId})");
            visited.Add(focused.Current.AutomationId);
        }
        Assert.Equal(new HashSet<string> { "Keep", "Discard", "Save" }, visited);

        // Esc keeps editing: the question goes, Settings stays, with the edit.
        RealInput.Key(handle, Native.VK_ESCAPE);
        Ui.Until(() => Ui.Find(window, "Leave") is null or { Current.IsOffscreen: true }, Ui.Short, "the question to close on Esc");
        Assert.Equal(("Settings", "Data"), Settle(window, "Settings"));
        Assert.Equal("Settings", Ui.Selected(rail)?.Current.Name);
        Assert.NotEqual(stored, Ui.Toggle(Ui.Get(window, "NotifyMemoryPressure")));

        // Asked again, Discard goes to the chosen row and the edit is gone.
        Ui.Select(Row(rail, "Overview"));
        leave = Ui.Until(() => Ui.Find(window, "Leave") is { Current.IsOffscreen: false } card ? card : null, Ui.Short, "the question again");
        Ui.Invoke(Ui.Get(leave, "Discard"));
        Assert.Equal(("Overview", "Empty"), Settle(window, "Overview"));
        Assert.Equal("Overview", Ui.Selected(rail)?.Current.Name);
        Ui.Select(Row(rail, "Settings"));
        Settle(window, "Settings");
        Assert.Equal(stored, Ui.Toggle(Ui.Get(window, "NotifyMemoryPressure")));
    }

    /// <summary>A control each built page draws in its Data state; a page that is not here has none yet.</summary>
    internal static string? DataMark(string title) => title switch
    {
        "Settings" => "ThemeBox",
        "Memory" => "ByRepo",
        _ => null,
    };

    internal static AutomationElement Row(AutomationElement rail, string title) =>
        Ui.Until(() => Ui.Named(rail, title, ControlType.ListItem), Ui.Short, $"the rail row {title}");

    /// <summary>
    /// The page once it has settled: its title and state. A page in Failed fails the test at once, and one still
    /// Loading after ten seconds fails it then.
    /// </summary>
    internal static (string Title, string? State) Settle(AutomationElement window, string title)
    {
        (string Title, string? State) page = default;
        Ui.Until(() =>
        {
            page = Ui.Page(window, DataMark(title));
            if (page.State == "Failed") throw new Xunit.Sdk.XunitException($"{title} failed: {Ui.Find(window, "FailureMessage")?.Current.Name}");
            return page.Title == title && page.State is not null and not "Loading";
        }, Ui.Short, $"{title} to show its data, or say why it has none");
        return page;
    }

    /// <summary>Whether an element is the container or one of its descendants in the automation tree.</summary>
    internal static bool Inside(AutomationElement element, AutomationElement container)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var at = element; at is not null; at = walker.GetParent(at))
            if (Automation.Compare(at, container)) return true;
        return false;
    }
}
