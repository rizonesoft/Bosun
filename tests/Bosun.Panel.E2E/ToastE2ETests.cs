using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// A toast on the shown window (D02 T03 §9): an action's result appears bottom right with its Dismiss button, which the
/// keyboard reaches and a real key presses.
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
        dismiss.SetFocus();
        Ui.Until(() => Ui.Focused() is { } f && Automation.Compare(f, dismiss) && f.Current.HasKeyboardFocus, Ui.Short, "the toast's Dismiss to have the keyboard");
        RealInput.Key(handle, Native.VK_SPACE);
        Ui.Until(() => Ui.Find(window, "Dismiss") is null, Ui.Short, "Space on Dismiss to close the toast");
        // Put the setting back, so the next run starts where this one did.
        ((TogglePattern)notify.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        Ui.Invoke(Ui.Get(window, "Save"));
    }
}
