using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The command palette by real keys on the shown window (D02 T03 §8): what an off-screen test can't prove, because no
/// hidden window holds the keyboard. Ctrl+K opens it from any page, a text field's focus included, with the keyboard in
/// its search field; typing and Enter go to a page; the arrows move the selection; Esc gives the keyboard back.
/// </summary>
public class CommandPaletteE2ETests
{
    const string Search = "Search", Results = "Results";

    static void Type(IntPtr handle, string letters)
    {
        foreach (var c in letters.ToUpperInvariant()) RealInput.Key(handle, c);
    }

    static AutomationElement Open(AutomationElement window, IntPtr handle)
    {
        RealInput.Chord(handle, Native.VK_CONTROL, Native.VK_K);
        var search = Ui.Get(window, Search);
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, search) && f.Current.HasKeyboardFocus, Ui.Short, "Ctrl+K to put the keyboard in the palette's search field");
        return search;
    }

    static bool Shown(AutomationElement window) => Ui.Find(window, Search) is { Current.IsOffscreen: false };

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void Ctrl_K_opens_it_from_three_pages_typing_and_Enter_go_to_a_page()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        RailTests.Settle(window, "Overview");

        Open(window, handle);                                                                  // from Overview
        Type(handle, "mem");
        RealInput.Key(handle, Native.VK_RETURN);
        Assert.Equal("Memory", RailTests.Settle(window, "Memory").Item1);
        Ui.Until(() => !Shown(window), Ui.Short, "the palette to close as it runs");

        Open(window, handle);                                                                  // from Memory
        Type(handle, "set");
        RealInput.Key(handle, Native.VK_RETURN);
        Assert.Equal("Settings", RailTests.Settle(window, "Settings").Item1);

        var floor = Ui.Get(window, "PressureFloor");                                           // from Settings, typing in a field
        RealInput.Focus(floor);
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, floor), Ui.Short, "the threshold field to have the keyboard");
        Open(window, handle);
        RealInput.Key(handle, Native.VK_ESCAPE);
        Ui.Until(() => !Shown(window), Ui.Short, "Esc to close the palette");
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, floor) && f.Current.HasKeyboardFocus, Ui.Short, "Esc to give the keyboard back to the threshold field");
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void The_arrows_move_the_selection_and_a_row_that_cannot_run_says_why_and_stays()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = KeyboardTests.OnSettings(panel);
        Open(window, handle);
        Type(handle, "settings");
        var results = Ui.Get(window, Results);
        Ui.Until(() => Ui.Selected(results)?.Current.Name.StartsWith("Settings, Pages", StringComparison.Ordinal) == true, Ui.Short, "the best match to be selected");
        RealInput.Key(handle, Native.VK_DOWN);
        Ui.Until(() => Ui.Selected(results)?.Current.Name.StartsWith("Save settings", StringComparison.Ordinal) == true, Ui.Short, "Down to select Save settings");
        RealInput.Key(handle, Native.VK_UP);
        Ui.Until(() => Ui.Selected(results)?.Current.Name.StartsWith("Settings, Pages", StringComparison.Ordinal) == true, Ui.Short, "Up to select Settings again");
        RealInput.Key(handle, Native.VK_DOWN);
        RealInput.Key(handle, Native.VK_RETURN);                                               // Save, with nothing to save
        Ui.Until(() => Ui.Find(window, "Message")?.Current.Name == "Save settings: There are no changes to save", Ui.Short, "the reason to be said");
        Assert.True(Shown(window), "a row that can't run leaves the palette open");
        RealInput.Key(handle, Native.VK_ESCAPE);
        Ui.Until(() => !Shown(window), Ui.Short, "Esc to close the palette");
    }
}
