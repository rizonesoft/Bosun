using System.Runtime.InteropServices;
using System.Windows.Automation;
using Xunit;

namespace Bosun.Panel.E2E;

/// <summary>The window's own chrome under a real pointer: the snap layouts on the maximise button, and the splitter.</summary>
public class WindowTests
{
    [Fact]
    public void Holding_the_pointer_on_maximise_shows_the_snap_layouts_and_clicks_maximise_then_restore()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        using var pointer = RealInput.KeepPointer();

        var maximise = Ui.Get(window, "Maximise");
        var (x, y) = Ui.Centre(maximise);
        var box = Ui.Box(maximise);
        // Below the button, where the flyout opens: the panel's own window until the pointer rests on the button.
        var belowX = x;
        var belowY = (int)(box.Bottom + box.Height * 2);
        Assert.Equal(handle, Native.RootAt(belowX, belowY));
        for (var step = 6; step >= 0; step--)
        {
            RealInput.MoveOnWindow(handle, x - step * 3, y);
            Thread.Sleep(30);
        }
        Ui.Until(() => IsSnapFlyout(Native.RootAt(belowX, belowY)), TimeSpan.FromSeconds(5), "the snap layouts flyout under the maximise button");

        RealInput.ClickOnWindow(handle, x, y);
        Ui.Until(() => Native.IsZoomed(handle), Ui.Short, "a click on maximise to maximise the window");
        Ui.Until(() => Ui.Get(window, "Maximise").Current.Name == "Restore", Ui.Short, "the button to be named Restore");

        (x, y) = Ui.Centre(Ui.Get(window, "Maximise")); // the button moved with the window
        RealInput.ClickOnWindow(handle, x, y);
        Ui.Until(() => !Native.IsZoomed(handle), Ui.Short, "a second click to restore the window");
        Ui.Until(() => Ui.Get(window, "Maximise").Current.Name == "Maximise", Ui.Short, "the button to be named Maximise again");
    }

    [Fact]
    public void Dragging_the_splitter_resizes_the_Activity_pane_within_its_limits()
    {
        using var panel = PanelProcess.Start();
        var window = panel.Front();
        var handle = new IntPtr(window.Current.NativeWindowHandle);
        using var pointer = RealInput.KeepPointer();
        var scale = GetDpiForWindow(handle) / 96.0;
        var activity = Ui.Get(window, "Activity");
        var splitter = Ui.Get(window, "PaneSplitter");

        var before = Ui.Box(activity).Height;
        var (x, y) = Ui.Centre(splitter);
        RealInput.DragOnWindow(handle, x, y, x, y - (int)(100 * scale));
        Ui.Until(() => Math.Abs(Ui.Box(activity).Height - before - 100 * scale) <= 4 * scale, Ui.Short, $"the pane to grow by 100 px (from {before:0})");

        // Far past each limit: the pane's row stays within 64 to 400 px (src/Bosun.Core/PanelSettings.cs). The list is
        // the row less its margins (6 px above and below) and the pane's 1 px top line.
        (x, y) = Ui.Centre(Ui.Get(window, "PaneSplitter"));
        RealInput.DragOnWindow(handle, x, y, x, y - (int)(2000 * scale));
        var tallest = Ui.Box(activity).Height / scale + 13;
        Assert.InRange(tallest, 64, 400.5);

        (x, y) = Ui.Centre(Ui.Get(window, "PaneSplitter"));
        RealInput.DragOnWindow(handle, x, y, x, y + (int)(2000 * scale));
        var shortest = Ui.Box(activity).Height / scale + 13;
        Assert.InRange(shortest, 63.5, 70);
        Assert.True(tallest > shortest + 100, $"the pane went from {shortest:0} to {tallest:0} px");
    }

    /// <summary>The snap layouts flyout: a windowed XAML popup of the shell (explorer.exe).</summary>
    private static bool IsSnapFlyout(IntPtr window)
    {
        if (Native.ClassOf(window) != "Xaml_WindowedPopupClass") return false;
        using var owner = System.Diagnostics.Process.GetProcessById(Native.ProcessOf(window));
        return owner.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
}
