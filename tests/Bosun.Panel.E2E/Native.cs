using System.Runtime.InteropServices;
using System.Text;

namespace Bosun.Panel.E2E;

/// <summary>The few Windows calls the smoke makes that UI Automation has no word for.</summary>
static partial class Native
{
    public const int WM_CLOSE = 0x0010, WM_NCHITTEST = 0x0084, HTMAXBUTTON = 9;
    public const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02, VK_MBUTTON = 0x04, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_DOWN = 0x28, VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_HOME = 0x24, VK_END = 0x23;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy, mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public int dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    private const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2;
    private const int MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_RIGHTDOWN = 0x8,
        MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    private const uint GA_ROOT = 2;

    /// <summary>Presses and releases one key, as a person would.</summary>
    public static void Key(int virtualKey)
    {
        var press = new INPUT[2];
        press[0].type = press[1].type = INPUT_KEYBOARD;
        press[0].u.ki.wVk = press[1].u.ki.wVk = (ushort)virtualKey;
        press[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        Send(press);
    }

    /// <summary>A key with a modifier held (Alt+Space, Alt+F4), sent as one input so nothing comes between.</summary>
    public static void Chord(int modifier, int virtualKey) =>
        Send([KeyEvent(modifier, 0), KeyEvent(virtualKey, 0), KeyEvent(virtualKey, KEYEVENTF_KEYUP), KeyEvent(modifier, KEYEVENTF_KEYUP)]);

    /// <summary>Presses one key and leaves it down; <see cref="KeyUp"/> lets it go.</summary>
    public static void KeyDown(int virtualKey) => Send([KeyEvent(virtualKey, 0)]);

    public static void KeyUp(int virtualKey) => Send([KeyEvent(virtualKey, KEYEVENTF_KEYUP)]);

    private static INPUT KeyEvent(int virtualKey, int flags)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki.wVk = (ushort)virtualKey;
        input.u.ki.dwFlags = flags;
        return input;
    }

    /// <summary>Whether a key or mouse button is down now, on the real keyboard or as input already sent.</summary>
    public static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    /// <summary>Moves the pointer to a point in screen pixels.</summary>
    public static void MoveTo(int x, int y) => Send([Mouse(x, y, MOUSEEVENTF_MOVE)]);

    public static void LeftDown(int x, int y) => Send([Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTDOWN)]);

    public static void LeftUp(int x, int y) => Send([Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_LEFTUP)]);

    public static void RightClick(int x, int y) => Send([Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTDOWN), Mouse(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_RIGHTUP)]);

    private static INPUT Mouse(int x, int y, int flags)
    {
        // Absolute positions are given in 0 to 65535 across the whole virtual desktop, every monitor included.
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = GetSystemMetrics(SM_CXVIRTUALSCREEN), height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi.dx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        input.u.mi.dy = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        input.u.mi.dwFlags = flags | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK;
        return input;
    }

    private static void Send(INPUT[] inputs)
    {
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length) throw new InvalidOperationException($"Windows took {sent} of {inputs.Length} input events (error {Marshal.GetLastPInvokeError()})");
    }

    public static POINT Cursor() => GetCursorPos(out var point) ? point : throw new InvalidOperationException("Windows did not say where the pointer is");

    /// <summary>The top-level window under a point.</summary>
    public static IntPtr RootAt(int x, int y)
    {
        var window = WindowFromPoint(new POINT { X = x, Y = y });
        return window == IntPtr.Zero ? IntPtr.Zero : GetAncestor(window, GA_ROOT);
    }

    public static int ProcessOf(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        return (int)id;
    }

    public static string ClassOf(IntPtr window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    public static RECT Frame(IntPtr window) => GetWindowRect(window, out var frame) ? frame : default;

    /// <summary>The top-level windows of a process, visible or not.</summary>
    public static List<IntPtr> WindowsOf(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((window, _) =>
        {
            if (ProcessOf(window) == processId) found.Add(window);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    public static string TitleOf(IntPtr window)
    {
        var title = new StringBuilder(512);
        return GetWindowTextW(window, title, title.Capacity) > 0 ? title.ToString() : "";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, StringBuilder name, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr window, StringBuilder title, int size);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out RECT frame);

    private delegate bool EnumProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc each, IntPtr data);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr window);
}
