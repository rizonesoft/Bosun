using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Bosun.Core;

namespace Bosun.Panel.Shell;

/// <summary>A screen as the window sees it: its bounds and work area in screen pixels, and its DPI.</summary>
public sealed record ScreenArea(Int32Rect Bounds, Int32Rect Work, int Dpi, bool Primary = false);

/// <summary>
/// Where the panel's window opens (D02 T03 §12): where it was when it was last hidden or closed, on that screen, fitted
/// to the screens there are now; or, with no place or none that lies on a screen, centred on the main screen as before.
/// A place is kept in screen pixels with the DPI it was saved at; Windows' placement calls take workspace coordinates
/// (offset by the taskbar), so it is converted through the window's monitor both ways.
/// </summary>
public sealed class WindowPlacement
{
    /// <summary>The window's least size (design/SPEC.md §1), and the title bar's height, both in DIPs.</summary>
    public const double MinWidth = 960, MinHeight = 600, TitleBar = 44;

    private readonly Window _window;
    private readonly PanelSettingsStore _store;
    private readonly ActivityLog _log;
    private readonly Func<IReadOnlyList<ScreenArea>> _screens;
    private bool _shownOnce;
    private string? _said; // the failure last written, so a repeat is not written again

    private WindowPlacement(Window window, PanelSettingsStore store, ActivityLog log, Func<IReadOnlyList<ScreenArea>> screens) =>
        (_window, _store, _log, _screens) = (window, store, log, screens);

    /// <summary>
    /// Attaches to a window not yet shown: it opens at the stored place, fitted (placed as its handle is made, before it
    /// is first shown, whether it is shown at once or later from the tray), and its place is kept each time it is hidden
    /// or closed.
    /// </summary>
    public static WindowPlacement Attach(Window window, PanelSettingsStore store, ActivityLog log, Func<IReadOnlyList<ScreenArea>>? screens = null)
    {
        var placement = new WindowPlacement(window, store, log, screens ?? Screens);
        if (store.Current.Window is { } place && Fit(place, placement._screens()) is { } bounds)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual; // the place is set below, before the window shows
            if (place.Maximised) window.WindowState = WindowState.Maximized;
            window.SourceInitialized += (_, _) => placement.Apply(bounds);
        }
        window.IsVisibleChanged += placement.OnVisibleChanged;
        window.Closing += (_, _) => placement.Keep();
        return placement;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false)
        {
            Keep();
            return;
        }
        // Shown again from the tray: a window whose screen has gone meanwhile comes back onto the main one.
        if (_shownOnce && Handle is var handle && handle != IntPtr.Zero && GetWindowRect(handle, out var now) &&
            Recover(new Int32Rect(now.left, now.top, now.right - now.left, now.bottom - now.top), _screens()) is { } back)
            Apply(back);
        _shownOnce = true;
    }

    private IntPtr Handle => new WindowInteropHelper(_window).Handle;

    /// <summary>Keeps the window's place in the settings; a capture or save that fails is one WARN line, and the old place stays.</summary>
    private void Keep()
    {
        var handle = Handle;
        if (handle == IntPtr.Zero) return; // never shown: there is no place to keep
        var place = Capture(handle, _screens());
        if (place is null)
        {
            Warn("the window's place couldn't be read from Windows");
            return;
        }
        if (_store.Current.Window == place) return;
        var outcome = _store.Update(current => current with { Window = place });
        if (outcome.Succeeded) _said = null;
        else Warn($"the window's place couldn't be saved: {outcome.Reason}");
    }

    private void Warn(string what)
    {
        if (what == _said) return;
        _said = what;
        _log.Warn($"The panel won't reopen where it was: {what}.");
    }

    private void Apply(Int32Rect bounds)
    {
        var handle = Handle;
        if (handle == IntPtr.Zero || ScreenOf(bounds, _screens()) is not { } screen) return;
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>(), showCmd = SW_HIDE, rcNormalPosition = ToWorkspace(bounds, screen) };
        if (_window.IsVisible) placement.showCmd = _window.WindowState == WindowState.Maximized ? SW_SHOWMAXIMIZED : SW_SHOWNORMAL;
        if (!SetWindowPlacement(handle, ref placement)) Warn($"Windows refused the window's place (error {Marshal.GetLastPInvokeError()})");
    }

    // ---- the rules, pure, for the tests ----

    /// <summary>
    /// The bounds to open a stored place at on these screens, or null when its title strip lies on none of them. The
    /// size is scaled by the screen's DPI over the one it was saved at, kept at least the window's least size at that
    /// DPI and at most the work area, and the place moved inside the work area.
    /// </summary>
    public static Int32Rect? Fit(WindowPlace place, IReadOnlyList<ScreenArea> screens)
    {
        var strip = new Int32Rect(place.Left, place.Top, place.Width, Math.Max(1, (int)Math.Round(TitleBar * place.Dpi / 96.0)));
        var screen = screens.Select(s => (Screen: s, Area: Overlap(s.Bounds, strip))).Where(x => x.Area > 0).OrderByDescending(x => x.Area).Select(x => x.Screen).FirstOrDefault();
        if (screen is null) return null;
        var scale = screen.Dpi / (double)place.Dpi;
        var work = screen.Work;
        int Within(double wanted, double least, int most) => (int)Math.Round(Math.Clamp(wanted, Math.Min(least, most), most));
        var width = Within(place.Width * scale, MinWidth * screen.Dpi / 96.0, work.Width);
        var height = Within(place.Height * scale, MinHeight * screen.Dpi / 96.0, work.Height);
        var left = Math.Clamp(place.Left, work.X, work.X + work.Width - width);
        var top = Math.Clamp(place.Top, work.Y, work.Y + work.Height - height);
        return new Int32Rect(left, top, width, height);
    }

    /// <summary>
    /// Where a window that is on no screen now goes: centred on the main screen at its size, fitted to it; null when it
    /// is on a screen and stays where it is.
    /// </summary>
    public static Int32Rect? Recover(Int32Rect current, IReadOnlyList<ScreenArea> screens) =>
        ScreenOf(current, screens) is null && screens.FirstOrDefault(s => s.Primary) is { } main ? Centred(main, (current.Width, current.Height)) : null;

    /// <summary>Whether a window restores maximised: maximised now, or minimised from maximised (Windows' own flag).</summary>
    public static bool RestoresMaximised(int showCmd, int flags) =>
        showCmd == SW_SHOWMAXIMIZED || (showCmd == SW_SHOWMINIMIZED && (flags & WPF_RESTORETOMAXIMIZED) != 0);

    /// <summary>Bounds of a size, centred in a screen's work area.</summary>
    public static Int32Rect Centred(ScreenArea screen, (int Width, int Height) size)
    {
        var width = Math.Min(size.Width, screen.Work.Width);
        var height = Math.Min(size.Height, screen.Work.Height);
        return new Int32Rect(screen.Work.X + (screen.Work.Width - width) / 2, screen.Work.Y + (screen.Work.Height - height) / 2, width, height);
    }

    /// <summary>The screen a rectangle lies most on, or null.</summary>
    public static ScreenArea? ScreenOf(Int32Rect bounds, IReadOnlyList<ScreenArea> screens) =>
        screens.Select(s => (Screen: s, Area: Overlap(s.Bounds, bounds))).Where(x => x.Area > 0).OrderByDescending(x => x.Area).Select(x => x.Screen).FirstOrDefault();

    private static long Overlap(Int32Rect a, Int32Rect b)
    {
        long w = Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X);
        long h = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }

    /// <summary>Screen pixels to the workspace coordinates <c>WINDOWPLACEMENT</c> takes: less the work area's offset on its screen.</summary>
    public static RECT ToWorkspace(Int32Rect bounds, ScreenArea screen)
    {
        int dx = screen.Work.X - screen.Bounds.X, dy = screen.Work.Y - screen.Bounds.Y;
        return new RECT { left = bounds.X - dx, top = bounds.Y - dy, right = bounds.X + bounds.Width - dx, bottom = bounds.Y + bounds.Height - dy };
    }

    /// <summary>Workspace coordinates back to screen pixels, through the screen the rectangle is on.</summary>
    public static Int32Rect ToScreen(RECT workspace, ScreenArea screen)
    {
        int dx = screen.Work.X - screen.Bounds.X, dy = screen.Work.Y - screen.Bounds.Y;
        return new Int32Rect(workspace.left + dx, workspace.top + dy, workspace.right - workspace.left, workspace.bottom - workspace.top);
    }

    /// <summary>A window's place now, in screen pixels with its screen's DPI; null when Windows can't say it.</summary>
    public static WindowPlace? Capture(IntPtr window, IReadOnlyList<ScreenArea> screens)
    {
        var placement = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(window, ref placement)) return null;
        var normal = placement.rcNormalPosition;
        // Which screen: the one the workspace rectangle lies on read as screen pixels (a work area's offset is never
        // larger than a taskbar, so this finds the same screen).
        var asIs = new Int32Rect(normal.left, normal.top, normal.right - normal.left, normal.bottom - normal.top);
        if (ScreenOf(asIs, screens) is not { } screen) return null;
        var bounds = ToScreen(normal, screen);
        return new WindowPlace(bounds.X, bounds.Y, bounds.Width, bounds.Height, screen.Dpi, RestoresMaximised(placement.showCmd, placement.flags));
    }

    /// <summary>Every screen there is now: bounds, work area and DPI.</summary>
    public static IReadOnlyList<ScreenArea> Screens()
    {
        var found = new List<ScreenArea>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? (int)x : 96;
                found.Add(new ScreenArea(Rect(info.rcMonitor), Rect(info.rcWork), dpi, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static Int32Rect Rect(RECT r) => new(r.left, r.top, r.right - r.left, r.bottom - r.top);

    // ---- Windows ----

    public const int SW_HIDE = 0, SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_SHOWMAXIMIZED = 3;
    public const int WPF_RESTORETOMAXIMIZED = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int left, top, right, bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x, y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT ptMinPosition, ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
