using System.IO;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// A toast on the shown window (D02 T03 §9): an action's result appears bottom right with its Dismiss button, which real
/// Tab presses reach; the keyboard in it holds the toast past its 6 s, and a real Space dismisses it.
/// </summary>
public class ToastE2ETests
{
    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void A_saved_change_shows_a_toast_the_keyboard_dismisses()
    {
        using var panel = PanelProcess.Start();
        var (window, handle) = KeyboardTests.OnSettings(panel);
        var notify = Ui.Get(window, "NotifyMemoryPressure");
        ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Ui.Invoke(Ui.Get(window, "Save"));
        var dismiss = Ui.Get(window, "Dismiss");
        Assert.StartsWith("Done: Save settings", dismiss.Current.HelpText, StringComparison.Ordinal);
        // Real Tab presses reach it (completion review: SetFocus proved activation, not that the keyboard gets there).
        for (var press = 0; press < 40 && !(Ui.Focused() is { } f && Automation.Compare(f, dismiss)); press++)
        {
            RealInput.Key(handle, Native.VK_TAB);
            Thread.Sleep(60);
        }
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, dismiss) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to reach the toast's Dismiss");
        // The keyboard in the card holds it past its 6 s, the pointer nowhere near it.
        Thread.Sleep(TimeSpan.FromSeconds(7.5));
        Assert.NotNull(Ui.Find(window, "Dismiss"));
        RealInput.Key(handle, Native.VK_SPACE);
        Ui.Until(() => Ui.Find(window, "Dismiss") is null, Ui.Short, "Space on Dismiss to close the toast");
        // Put the setting back, so the next run starts where this one did.
        ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Ui.Invoke(Ui.Get(window, "Save"));
    }

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public void A_long_failure_opens_in_full_by_keyboard_and_stays_until_dismissed()
    {
        // D02 T04 §7: a save the settings file refuses (made read-only here) fails naming the file by its whole path, in
        // a data folder as deep as a long profile or OneDrive path, so the message runs past the five lines it opens to.
        // Tab reaches Show more, Space opens it, Tab reaches the message, Down and End scroll it to its last line, the
        // toast stays past its 6 s, and Dismiss closes it.
        using var panel = PanelProcess.StartDeep(150);
        var (window, handle) = KeyboardTests.OnSettings(panel);
        var file = Path.Combine(panel.DataFolder, "panel.json");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            var notify = Ui.Get(window, "NotifyMemoryPressure");
            ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
            var floor = Ui.Get(window, "PressureFloor");
            ((ValuePattern)floor.GetCurrentPattern(ValuePattern.Pattern)).SetValue("3072");
            var light = Ui.Named(window, "Light", ControlType.RadioButton) ?? Ui.Named(window, "Light")!;
            ((SelectionItemPattern)light.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
            Ui.Invoke(Ui.Get(window, "Save"));
            var more = Ui.Get(window, "ShowMore");
            Assert.Equal("Show the whole message", more.Current.Name);
            for (var press = 0; press < 40 && !(Ui.Focused() is { } f && Automation.Compare(f, more)); press++)
            {
                RealInput.Key(handle, Native.VK_TAB);
                Thread.Sleep(60);
            }
            Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, more) && f.Current.HasKeyboardFocus, Ui.Short, "Tab to reach Show more");
            RealInput.Key(handle, Native.VK_SPACE);
            Ui.Until(() => more.Current.Name == "Show the first lines again", Ui.Short, "Space on Show more to open the message");
            // The keyboard reaches the message itself, to scroll it: Tab goes Show less, Dismiss, then the message.
            var dismissFirst = Ui.Get(window, "Dismiss");
            RealInput.Key(handle, Native.VK_TAB);
            Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, dismissFirst), Ui.Short, "Tab from Show less to Dismiss");
            RealInput.Key(handle, Native.VK_TAB);
            Ui.UntilFocused(f => f.Current.Name == "The whole message", Ui.Short, "Tab on to the whole message");
            var message = Ui.Focused()!;
            Assert.Contains(file, message.Current.HelpText);                                   // the whole of it, to a screen reader too
            // Real keys scroll it, read through UI Automation's own scroll pattern (completion review): Down moves it a
            // line, End reaches its last.
            var scroll = (ScrollPattern)message.GetCurrentPattern(ScrollPattern.Pattern);
            Assert.True(scroll.Current.VerticallyScrollable, $"the opened message runs past its five lines: \"{message.Current.HelpText}\"");
            Assert.Equal(0, scroll.Current.VerticalScrollPercent, 1);
            RealInput.Key(handle, Native.VK_DOWN);
            Ui.Until(() => scroll.Current.VerticalScrollPercent > 0, Ui.Short, "Down to scroll the message");
            RealInput.Key(handle, Native.VK_END);
            Ui.Until(() => Math.Abs(scroll.Current.VerticalScrollPercent - 100) < 0.5, Ui.Short, "End to reach the message's last line");
            Thread.Sleep(TimeSpan.FromSeconds(7.5));
            var dismiss = Ui.Get(window, "Dismiss");
            for (var press = 0; press < 40 && !(Ui.Focused() is { } f && Automation.Compare(f, dismiss)); press++)
            {
                RealInput.Key(handle, Native.VK_TAB);
                Thread.Sleep(60);
            }
            Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, dismiss) && f.Current.HasKeyboardFocus, Ui.Short, "Tab on to Dismiss");
            RealInput.Key(handle, Native.VK_SPACE);
            Ui.Until(() => Ui.Find(window, "ShowMore") is null, Ui.Short, "Space on Dismiss to close the toast");
        }
        finally
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }
}
