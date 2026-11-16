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
}
