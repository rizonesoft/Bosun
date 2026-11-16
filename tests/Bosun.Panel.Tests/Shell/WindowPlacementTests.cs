using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Settings;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// Where the window opens (D02 T03 §12): a stored place fitted to the screens there are (DPI, least size, work area,
/// a screen that is gone), converted to and from Windows' workspace coordinates, opened before the window is shown and
/// kept as it closes; a save the store refuses keeps the old place and every other setting, and is one WARN line. No
/// test here shows a window: a window shown would appear on the screen someone is working on.
/// </summary>
public class WindowPlacementTests
{
    static Int32Rect R(int x, int y, int w, int h) => new(x, y, w, h);

    /// <summary>The main screen at the origin (taskbar along the bottom) and one to its left at a negative origin, 150 percent.</summary>
    static readonly ScreenArea Main = new(R(0, 0, 2560, 1440), R(0, 0, 2560, 1392), 96, Primary: true);
    static readonly ScreenArea Left = new(R(-3840, 0, 3840, 2160), R(-3840, 0, 3840, 2088), 144);
    static readonly IReadOnlyList<ScreenArea> Both = [Main, Left];

    // ---- fitting ----

    [Fact]
    public void A_place_on_a_screen_at_a_negative_origin_opens_where_it_was()
    {
        var place = new WindowPlace(-3000, 200, 2040, 1290, 144, false);
        Assert.Equal(R(-3000, 200, 2040, 1290), WindowPlacement.Fit(place, Both));
    }

    [Fact]
    public void A_place_saved_at_another_DPI_is_scaled_to_its_screen_s()
    {
        // Saved on the left screen at 144 DPI; that screen is now at 96: two thirds the size in pixels.
        var place = new WindowPlace(-3000, 200, 2040, 1290, 144, false);
        var now = new ScreenArea(Left.Bounds, Left.Work, 96);
        Assert.Equal(R(-3000, 200, 1360, 860), WindowPlacement.Fit(place, [Main, now]));
    }

    [Fact]
    public void A_place_too_small_is_brought_to_the_least_size_at_its_screen_s_DPI()
    {
        var place = new WindowPlace(-3000, 200, 300, 200, 144, false);
        Assert.Equal(R(-3000, 200, 1440, 900), WindowPlacement.Fit(place, Both)); // 960 x 600 at 150 percent
    }

    [Fact]
    public void A_place_larger_than_the_work_area_is_cut_to_it_and_one_off_its_edge_moved_inside()
    {
        var big = new WindowPlace(100, 100, 4000, 3000, 96, false);
        Assert.Equal(R(0, 0, 2560, 1392), WindowPlacement.Fit(big, Both));
        var hanging = new WindowPlace(2000, 1000, 1360, 860, 96, false); // its title bar on the main screen, its body off the bottom right
        Assert.Equal(R(1200, 532, 1360, 860), WindowPlacement.Fit(hanging, Both));
    }

    [Fact]
    public void A_place_whose_screen_is_gone_is_none_and_the_window_opens_centred()
    {
        var gone = new WindowPlace(-6000, 100, 1360, 860, 96, false);
        Assert.Null(WindowPlacement.Fit(gone, Both));
        Assert.Null(WindowPlacement.Fit(new WindowPlace(-3000, 200, 2040, 1290, 144, false), [Main])); // the left screen unplugged
    }

    [Fact]
    public void A_window_on_no_screen_comes_back_centred_on_the_main_one_and_one_on_a_screen_stays()
    {
        Assert.Equal(R(600, 266, 1360, 860), WindowPlacement.Recover(R(-6000, 100, 1360, 860), Both));
        Assert.Null(WindowPlacement.Recover(R(-3000, 200, 2040, 1290), Both));
    }

    [Theory]
    [InlineData(WindowPlacement.SW_SHOWMAXIMIZED, 0, true)]
    [InlineData(WindowPlacement.SW_SHOWNORMAL, 0, false)]
    [InlineData(WindowPlacement.SW_SHOWMINIMIZED, WindowPlacement.WPF_RESTORETOMAXIMIZED, true)] // minimised from maximised
    [InlineData(WindowPlacement.SW_SHOWMINIMIZED, 0, false)]
    public void A_minimised_window_keeps_whether_it_restores_maximised(int showCmd, int flags, bool maximised) =>
        Assert.Equal(maximised, WindowPlacement.RestoresMaximised(showCmd, flags));

    [Fact]
    public void Workspace_coordinates_are_screen_pixels_less_the_work_area_s_offset_both_ways()
    {
        // A taskbar along the top and left of a screen: the work area starts 48 and 40 pixels in.
        var screen = new ScreenArea(R(-1920, 0, 1920, 1080), R(-1872, 40, 1872, 1040), 96);
        var workspace = WindowPlacement.ToWorkspace(R(-1800, 100, 1000, 700), screen);
        Assert.Equal((-1848, 60, -848, 760), (workspace.left, workspace.top, workspace.right, workspace.bottom));
        Assert.Equal(R(-1800, 100, 1000, 700), WindowPlacement.ToScreen(workspace, screen));
    }

    // ---- a real window, never shown ----

    sealed record Bench(Window Window, PanelSettingsStore Store, MemoryFiles Files, ActivityLog Log)
    {
        public IntPtr Handle => new WindowInteropHelper(Window).Handle;

        public Int32Rect Frame => GetWindowRect(Handle, out var r) ? new Int32Rect(r.left, r.top, r.right - r.left, r.bottom - r.top) : default;
    }

    static Bench Attach(WindowPlace? place, IReadOnlyList<ScreenArea>? screens = null)
    {
        var files = new MemoryFiles();
        var store = new PanelSettingsStore(@"C:\panel-test\panel.json", files);
        Assert.True(store.Update(c => c with { Theme = ThemeChoice.Light, Window = place }).Succeeded);
        var window = new Window { Width = 1360, Height = 860, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var log = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
        WindowPlacement.Attach(window, store, log, screens is null ? null : () => screens);
        new WindowInteropHelper(window).EnsureHandle(); // made, as the application makes it, and never shown
        return new Bench(window, store, files, log);
    }

    [Fact]
    public void On_each_screen_of_this_machine_the_window_is_placed_before_it_shows_and_its_place_reads_back_the_same()
    {
        Sta.Run(() =>
        {
            foreach (var screen in WindowPlacement.Screens())
            {
                var size = ((int)(1360 * screen.Dpi / 96.0), (int)(860 * screen.Dpi / 96.0));
                var at = WindowPlacement.Centred(screen, size);
                var place = new WindowPlace(at.X, at.Y, at.Width, at.Height, screen.Dpi, false);
                var b = Attach(place);
                Assert.Equal(WindowStartupLocation.Manual, b.Window.WindowStartupLocation);
                Assert.Equal(at, b.Frame); // through Windows' workspace coordinates and back, on this screen
                Assert.Equal(place, WindowPlacement.Capture(b.Handle, WindowPlacement.Screens()));
                b.Window.Close();
            }
            return 0;
        });
    }

    [Fact]
    public void A_place_that_fits_no_screen_leaves_the_window_centred()
    {
        Sta.Run(() =>
        {
            var b = Attach(new WindowPlace(-60000, 0, 1360, 860, 96, false), WindowPlacement.Screens());
            Assert.Equal(WindowStartupLocation.CenterScreen, b.Window.WindowStartupLocation);
            b.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void A_maximised_place_opens_maximised()
    {
        Sta.Run(() =>
        {
            var main = WindowPlacement.Screens().First(s => s.Primary);
            var at = WindowPlacement.Centred(main, (1360, 860));
            var b = Attach(new WindowPlace(at.X, at.Y, at.Width, at.Height, main.Dpi, true));
            Assert.Equal(WindowState.Maximized, b.Window.WindowState);
            b.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void Closing_keeps_the_place_and_a_refused_save_keeps_the_old_one_and_every_other_setting_with_one_WARN_line()
    {
        Sta.Run(() =>
        {
            var main = WindowPlacement.Screens().First(s => s.Primary);
            var at = WindowPlacement.Centred(main, ((int)(1360 * main.Dpi / 96.0), (int)(860 * main.Dpi / 96.0))); // the window's size at the screen's DPI
            var first = new WindowPlace(at.X, at.Y, at.Width, at.Height, main.Dpi, false);
            var b = Attach(first);
            SetWindowPos(b.Handle, IntPtr.Zero, at.X + 40, at.Y + 30, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE); // moved, still hidden
            b.Window.Close();
            Assert.Equal(first with { Left = at.X + 40, Top = at.Y + 30 }, b.Store.Current.Window);

            var c = Attach(first);
            c.Files.FailWrite = new IOException("the disk is full");
            SetWindowPos(c.Handle, IntPtr.Zero, at.X + 80, at.Y + 60, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            c.Window.Close();
            var stored = new PanelSettingsStore(@"C:\panel-test\panel.json", c.Files).Current;
            Assert.Equal((first, ThemeChoice.Light), (stored.Window, stored.Theme));
            var warn = Assert.Single(c.Log.Entries, e => e.Severity == LogSeverity.Warn);
            Assert.StartsWith("The panel won't reopen where it was: the window's place couldn't be saved", warn.Message);
            return 0;
        });
    }

    private const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowPlacement.RECT rect);
}
