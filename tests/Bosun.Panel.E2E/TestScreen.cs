using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bosun.Panel.E2E;

/// <summary>A screen as the smoke sees it, in screen pixels (the smoke is per-monitor DPI aware).</summary>
sealed record Screen(string Device, Native.RECT Bounds, Native.RECT Work, int Dpi, bool Primary)
{
    public long Area => (long)(Bounds.Right - Bounds.Left) * (Bounds.Bottom - Bounds.Top);

    public bool Holds(int x, int y) => x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;

    public override string ToString() => FormattableString.Invariant($"{Device} ({Bounds.Right - Bounds.Left} x {Bounds.Bottom - Bounds.Top} at {Bounds.Left}, {Bounds.Top}{(Primary ? ", main" : "")})");
}

/// <summary>
/// The screen the smoke's panels run on (D02 T03 §12, the owner's request of 2026-11-16 that testing keep off the
/// screen they work on): <c>BOSUN_TEST_SCREEN</c>, a device name as Windows gives it (<c>\\.\DISPLAY3</c>), when set,
/// and a run that names no screen here is refused with the screens there are; else the largest screen that is not the
/// main one (the leftmost of equals); else the main one, on a machine with one screen, as CI is. Every panel is started
/// with its window's place centred on it, so it opens there and never first on the main screen.
/// </summary>
static class TestScreen
{
    public const string Variable = "BOSUN_TEST_SCREEN";

    private static readonly Lazy<Screen> Chosen = new(() => Choose(All(), Environment.GetEnvironmentVariable(Variable)));

    /// <summary>The screen of this run.</summary>
    public static Screen Current => Chosen.Value;

    public static Screen Choose(IReadOnlyList<Screen> screens, string? named)
    {
        if (screens.Count == 0) throw new InvalidOperationException("Windows lists no screen, so the smoke has nowhere to run");
        if (!string.IsNullOrWhiteSpace(named))
            return screens.FirstOrDefault(s => string.Equals(s.Device, named.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{Variable} names {named.Trim()}, which is not a screen here; the screens are {string.Join(", ", screens)}");
        return screens.Where(s => !s.Primary).OrderByDescending(s => s.Area).ThenBy(s => s.Bounds.Left).FirstOrDefault()
            ?? screens.First(s => s.Primary);
    }

    /// <summary>The panel's settings file holding a place centred on <paramref name="screen"/>, at the window's own size at its DPI.</summary>
    public static string Settings(Screen screen)
    {
        int workW = screen.Work.Right - screen.Work.Left, workH = screen.Work.Bottom - screen.Work.Top;
        var width = Math.Min((int)Math.Round(1360 * screen.Dpi / 96.0), workW);
        var height = Math.Min((int)Math.Round(860 * screen.Dpi / 96.0), workH);
        var left = screen.Work.Left + (workW - width) / 2;
        var top = screen.Work.Top + (workH - height) / 2;
        return string.Create(CultureInfo.InvariantCulture,
            $"{{\"schema_version\":1,\"window\":{{\"left\":{left},\"top\":{top},\"width\":{width},\"height\":{height},\"dpi\":{screen.Dpi},\"maximised\":false}}}}\n");
    }

    /// <summary>Every screen there is now.</summary>
    public static IReadOnlyList<Screen> All()
    {
        var found = new List<Screen>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfoW(monitor, ref info))
            {
                var dpi = GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? (int)x : 96;
                found.Add(new Screen(info.szDevice, info.rcMonitor, info.rcWork, dpi, (info.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Native.RECT rcMonitor, rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}

/// <summary>
/// Where a process's main window was at the moment it was first shown, seen by a WinEvent hook on a thread of its own,
/// so a window that appears on one screen and then moves is caught where it appeared (plan review of D02 T03 §12).
/// </summary>
sealed class FirstShown : IDisposable
{
    private const uint EVENT_OBJECT_SHOW = 0x8002, WINEVENT_OUTOFCONTEXT = 0, WM_QUIT = 0x0012;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _hooked = new();
    private readonly WinEventProc _callback; // kept alive while the hook is
    private uint _threadId;
    private volatile int _process;
    // Every panel window shown since the hook began, with its process: kept, so one shown before the process's id is
    // known is still found (round 1: the child runs before Start returns).
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int Process, Native.RECT Bounds)> _seen = new();

    public FirstShown()
    {
        _callback = OnEvent;
        _thread = new Thread(Run) { IsBackground = true, Name = "first-shown hook" };
        _thread.Start();
        _hooked.Wait();
    }

    /// <summary>The process watched; set as soon as it is known (events of others are ignored).</summary>
    public int Process { set => _process = value; }

    /// <summary>The bounds of the first window titled "Bosun Panel" the process showed, or null when it has shown none.</summary>
    public Native.RECT? Bounds
    {
        get
        {
            var process = _process;
            foreach (var (shown, bounds) in _seen)
                if (shown == process) return bounds;
            return null;
        }
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        _hooked.Set();
        while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessageW(ref message);
        UnhookWinEvent(hook);
    }

    private void OnEvent(IntPtr hook, uint e, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (objectId != 0 || Native.TitleOf(window) != "Bosun Panel") return;
        _seen.Enqueue((Native.ProcessOf(window), Native.Frame(window)));
    }

    public void Dispose()
    {
        PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _hooked.Dispose();
    }

    private delegate void WinEventProc(IntPtr hook, uint e, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern int GetMessageW(out MSG message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessageW(uint thread, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
