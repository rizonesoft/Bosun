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
    [Fact]
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

    [Fact]
    public void A_long_failure_opens_in_full_by_keyboard_and_stays_until_dismissed()
    {
        // D02 T04 §7: a save the settings file refuses (made read-only here) fails with a reason for each setting,
        // longer than three lines. Tab reaches Show more, Space opens it, the toast stays past its 6 s, and Dismiss closes it.
        using var panel = PanelProcess.Start();
        var (window, handle) = KeyboardTests.OnSettings(panel);
        var file = Path.Combine(panel.DataFolder, "panel.json");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            // Three settings changed, each refused with its own reason: together longer than three lines.
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
            Ui.Until(() => Ui.Focused()?.Current.Name == "The whole message", Ui.Short, "Tab on to the whole message");
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
