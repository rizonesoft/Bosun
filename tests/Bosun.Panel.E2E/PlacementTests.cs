using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Bosun.Core;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>
/// The window's place kept on the real panel (D02 T03 §12, completion review): moved, then closed into the tray, its
/// place is in `panel.json`, read back by a store of its own; maximised, then minimised, then closed, it is kept as
/// maximised with its normal bounds unchanged. Everything here happens on the test screen.
/// </summary>
public class PlacementTests
{
    static WindowPlace? Stored(PanelProcess panel) =>
        new PanelSettingsStore(Path.Combine(panel.DataFolder, "panel.json")).Current.Window;

    /// <summary>
    /// The window's pattern once the window says it is ready for input: UI Automation refuses an operation ("Operation
    /// cannot be performed") on a window just shown again, before it is (found on the first run, 2026-11-16).
    /// </summary>
    static WindowPattern Ready(AutomationElement window)
    {
        var pattern = (WindowPattern)window.GetCurrentPattern(WindowPattern.Pattern);
        Ui.Until(() => pattern.Current.WindowInteractionState == WindowInteractionState.ReadyForUserInteraction, Ui.Short, "the window to be ready for input");
        return pattern;
    }

    static void Close(AutomationElement window) => Ready(window).Close();

    static void Become(AutomationElement window, WindowVisualState state) => Ready(window).SetWindowVisualState(state);

    [RealInputFact]
    [Trait(SmokeInput.Trait, SmokeInput.Real)]
    public async Task Moved_then_hidden_its_place_is_kept_and_a_minimised_maximised_window_is_kept_maximised()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Window();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        var start = Native.Frame(handle);
        var screen = TestScreen.Current;
        // Moved 60 by 40 pixels, still on the test screen.
        Assert.True(SetWindowPos(handle, IntPtr.Zero, start.Left + 60, start.Top + 40, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE));
        Assert.True(screen.Holds(start.Left + 60, start.Top + 40));

        Close(window); // the first close hides it in the tray: its place is kept then
        Ui.Until(() => !Native.IsWindowVisible(handle), Ui.Short, "the window to hide in the tray");
        Ui.Until(() => Stored(panel) is { } p && p.Left == start.Left + 60, Ui.Short, "the moved place to be stored");
        var kept = Stored(panel)!;
        Assert.Equal((start.Left + 60, start.Top + 40, start.Right - start.Left, start.Bottom - start.Top, screen.Dpi, false),
            (kept.Left, kept.Top, kept.Width, kept.Height, kept.Dpi, kept.Maximised));

        // Asked for again, maximised, minimised, closed: kept as maximised, the normal bounds as they were.
        using (var second = panel.StartAgain())
        {
            var ended = await Task.WhenAny(second.Exited, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken));
            Assert.True(ended == second.Exited, "the second launch exits within 15 s");
        }
        window = panel.Window();
        Ui.Until(() => Native.IsWindowVisible(handle), Ui.Short, "the window to be shown again");
        Become(window, WindowVisualState.Maximized);
        Ui.Until(() => Native.IsZoomed(handle), Ui.Short, "the window to be maximised");
        Become(window, WindowVisualState.Minimized);
        Ui.Until(() => Native.IsIconic(handle), Ui.Short, "the window to be minimised");
        Close(window);
        Ui.Until(() => Stored(panel) is { Maximised: true }, Ui.Short, "the window to be kept as maximised");
        Assert.Equal(kept with { Maximised = true }, Stored(panel));
    }

    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
