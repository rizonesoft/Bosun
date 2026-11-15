using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Bosun.Panel.Shell;

/// <summary>
/// Windows 11 shows the snap layouts over a window's maximise button only when the window answers
/// <c>WM_NCHITTEST</c> there with <c>HTMAXBUTTON</c> (design/SPEC.md §1). A window that draws its own buttons has to
/// say so itself, and from then on Windows treats that spot as its own: the button gets no mouse input at all. So
/// this hook also turns the non-client press and release there into the button's click, and the non-client move and
/// leave into its hover, or the button would be dead under the pointer (D02 T02 §4).
///
/// The messages come in as numbers, so a test drives the hook without a window; <see cref="Attach"/> puts it on a
/// real one.
/// </summary>
public sealed class SnapLayoutHook(Func<int, int, bool> overMaximise, Action toggleMaximise, Action<bool> hover)
{
    public const int WM_NCHITTEST = 0x0084, WM_NCMOUSEMOVE = 0x00A0, WM_NCLBUTTONDOWN = 0x00A1, WM_NCLBUTTONUP = 0x00A2;
    public const int WM_MOUSEMOVE = 0x0200, WM_NCMOUSELEAVE = 0x02A2;
    public const int HTMAXBUTTON = 9;

    private bool _pressed;
    private bool _hovering;
    private HwndSource? _source;

    /// <summary>
    /// One window message. <paramref name="handled"/> is set when the hook answered it; otherwise the window carries
    /// on as if the hook were not there.
    /// </summary>
    public IntPtr Handle(int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (message)
        {
            case WM_NCHITTEST:
                var (x, y) = ScreenPoint(lParam);
                if (!overMaximise(x, y)) return IntPtr.Zero;
                handled = true;
                return HTMAXBUTTON;

            case WM_NCMOUSEMOVE:
                Hover(wParam.ToInt64() == HTMAXBUTTON);
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
            case WM_NCMOUSELEAVE:
                Hover(false);
                if (message == WM_NCMOUSELEAVE) _pressed = false;
                return IntPtr.Zero;

            case WM_NCLBUTTONDOWN:
                if (wParam.ToInt64() != HTMAXBUTTON) return IntPtr.Zero;
                // Ours: Windows would otherwise start its own handling of a caption button it does not draw.
                _pressed = true;
                handled = true;
                return IntPtr.Zero;

            case WM_NCLBUTTONUP:
                var wasPressed = _pressed;
                _pressed = false;
                if (wParam.ToInt64() != HTMAXBUTTON) return IntPtr.Zero;
                handled = true;
                // A click is a press and a release on the button: a press dragged off it, or a release that began
                // elsewhere, is not one.
                if (wasPressed) toggleMaximise();
                return IntPtr.Zero;

            default:
                return IntPtr.Zero;
        }
    }

    private void Hover(bool over)
    {
        if (over == _hovering) return;
        _hovering = over;
        hover(over);
        // Windows says the pointer left the non-client area only to a window that asked to be told.
        if (over && _source is not null) TrackNonClientLeave(_source.Handle);
    }

    /// <summary>The point a mouse message carries, in screen pixels: two signed 16-bit numbers, so a monitor left of or above the main one works.</summary>
    public static (int X, int Y) ScreenPoint(IntPtr lParam)
    {
        var packed = unchecked((int)lParam.ToInt64());
        return ((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
    }

    /// <summary>The <c>lParam</c> for a point in screen pixels: what <see cref="ScreenPoint"/> reads back.</summary>
    public static IntPtr Pack(int x, int y) => new(unchecked((int)(((uint)(ushort)(short)y << 16) | (ushort)(short)x)));

    /// <summary>
    /// Whether a point in screen pixels is on a button, given where the button's top-left corner is on screen (in
    /// pixels) and its size in WPF's units, at the monitor's scale (1.0 at 100%, 1.5 at 150%).
    /// </summary>
    public static bool Over(int x, int y, int originX, int originY, Size size, double scale)
    {
        if (scale <= 0 || size.IsEmpty) return false;
        double dx = (x - originX) / scale, dy = (y - originY) / scale;
        return dx >= 0 && dy >= 0 && dx < size.Width && dy < size.Height;
    }

    /// <summary>Puts the hook on a window's message source. <see cref="Detach"/> takes it off.</summary>
    public void Attach(HwndSource source)
    {
        Detach();
        _source = source;
        source.AddHook(Hook);
    }

    public void Detach()
    {
        _source?.RemoveHook(Hook);
        _source = null;
        _pressed = false;
        Hover(false);
    }

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) => Handle(message, wParam, lParam, ref handled);

    private static void TrackNonClientLeave(IntPtr hwnd)
    {
        const uint TME_LEAVE = 0x00000002, TME_NONCLIENT = 0x00000010;
        var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE | TME_NONCLIENT, hwndTrack = hwnd };
        TrackMouseEvent(ref track);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
}
