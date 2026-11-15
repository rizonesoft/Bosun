using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Bosun.Panel.Tray;

/// <summary>
/// The calls to Windows that a tray icon is made of (D02 T02 §7), and nothing else: every decision is in
/// <see cref="NativeTrayIcon"/>, which is therefore the same class in a test as in the panel. The real one is
/// <see cref="Win32TrayShell"/>; a test stands in its own, so no test puts an icon in the person's tray.
/// </summary>
public interface ITrayShell : IDisposable
{
    /// <summary>The message Windows sends for the icon's own events.</summary>
    int CallbackMessage { get; }

    /// <summary>The message Windows broadcasts when its taskbar has started again; 0 when it is not known.</summary>
    int TaskbarCreatedMessage { get; }

    /// <summary>A message for the icon arrived: its number and its lParam.</summary>
    event Action<int, IntPtr>? Message;

    /// <summary>Adds the icon. False when Windows refuses.</summary>
    bool Add(TrayState state, string tooltip);

    /// <summary>Changes the icon that is there.</summary>
    bool Modify(TrayState state, string tooltip);

    /// <summary>Shows a notification from the icon. False when Windows refuses.</summary>
    bool Notify(string title, string text);

    /// <summary>Removes the icon.</summary>
    void Delete();

    /// <summary>Shows a popup menu at the pointer and waits for it. The id of the line chosen, or null when it was dismissed.</summary>
    int? TrackMenu(IReadOnlyList<(int Id, string Text)> items);
}

/// <summary>A native popup menu, built from lines of text and readable back, as Windows holds it.</summary>
public sealed class NativeMenu : IDisposable
{
    private const uint MF_STRING = 0, MF_BYPOSITION = 0x400;

    public NativeMenu(IReadOnlyList<(int Id, string Text)> items)
    {
        Handle = CreatePopupMenu();
        if (Handle == IntPtr.Zero) throw new InvalidOperationException("Windows did not make the menu");
        foreach (var (id, text) in items)
            if (!AppendMenuW(Handle, MF_STRING, (UIntPtr)(uint)id, text)) throw new InvalidOperationException("Windows did not add a line to the menu");
    }

    public IntPtr Handle { get; private set; }

    /// <summary>The menu's lines as Windows holds them now.</summary>
    public IReadOnlyList<(int Id, string Text)> Items()
    {
        var lines = new List<(int, string)>();
        var buffer = new char[512];
        for (var at = 0; at < GetMenuItemCount(Handle); at++)
        {
            var length = GetMenuStringW(Handle, (uint)at, buffer, buffer.Length, MF_BYPOSITION);
            lines.Add(((int)GetMenuItemID(Handle, at), new string(buffer, 0, Math.Max(0, length))));
        }
        return lines;
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        DestroyMenu(Handle);
        Handle = IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint GetMenuItemID(IntPtr menu, int position);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint item, [Out] char[] text, int max, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);
}

/// <summary>
/// The real calls: Shell_NotifyIcon on a window of its own that is never shown, with the icon's messages at version 4.
/// Made and used on the UI thread.
/// </summary>
public sealed class Win32TrayShell : ITrayShell
{
    private const int WM_APP_TRAY = 0x8001, IconId = 1;
    private const int NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const int NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const int NIIF_INFO = 0x1, NOTIFYICON_VERSION_4 = 4;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
    private const int WS_POPUP = unchecked((int)0x80000000), WS_EX_TOOLWINDOW = 0x80, SM_CXSMICON = 49;

    private readonly HwndSource _source;
    private readonly Dictionary<TrayState, System.Drawing.Icon> _icons = [];
    private bool _disposed;

    public Win32TrayShell()
    {
        // A top-level window, not a message-only one: the taskbar's restart is broadcast, and only top-level windows
        // hear a broadcast. It has no size and is never shown, and as a tool window it is in no task list.
        _source = new HwndSource(new HwndSourceParameters("Bosun Panel tray") { WindowStyle = WS_POPUP, ExtendedWindowStyle = WS_EX_TOOLWINDOW, Width = 0, Height = 0 });
        _source.AddHook(Hook);
        TaskbarCreatedMessage = (int)RegisterWindowMessageW("TaskbarCreated");
    }

    public int CallbackMessage => WM_APP_TRAY;

    public int TaskbarCreatedMessage { get; }

    public event Action<int, IntPtr>? Message;

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_APP_TRAY || (TaskbarCreatedMessage != 0 && message == TaskbarCreatedMessage)) Message?.Invoke(message, lParam);
        return IntPtr.Zero;
    }

    /// <summary>The side of a small icon on this screen, in pixels.</summary>
    public static int SmallIconSize => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    /// <summary>The icon for a state, from the panel's own resources, at the frame nearest a size.</summary>
    public static System.Drawing.Icon LoadIcon(TrayState state, int size)
    {
        var name = "Bosun.Panel.Tray." + TrayViewModel.IconFile(state);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"the panel does not hold {name}");
        return new System.Drawing.Icon(stream, size, size);
    }

    private IntPtr IconFor(TrayState state)
    {
        if (!_icons.TryGetValue(state, out var icon)) _icons[state] = icon = LoadIcon(state, SmallIconSize);
        return icon.Handle;
    }

    private NOTIFYICONDATAW Data(int flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _source.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_APP_TRAY,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private NOTIFYICONDATAW Shown(TrayState state, string tooltip)
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        data.hIcon = IconFor(state);
        data.szTip = tooltip;
        return data;
    }

    public bool Add(TrayState state, string tooltip)
    {
        var data = Shown(state, tooltip);
        if (!Shell_NotifyIconW(NIM_ADD, ref data)) return false;
        data.uVersion = NOTIFYICON_VERSION_4;
        if (Shell_NotifyIconW(NIM_SETVERSION, ref data)) return true;
        // Without version 4 the icon's messages would not mean what they are read as: no icon, said as refused.
        Shell_NotifyIconW(NIM_DELETE, ref data);
        return false;
    }

    public bool Modify(TrayState state, string tooltip)
    {
        var data = Shown(state, tooltip);
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public bool Notify(string title, string text)
    {
        var data = Data(NIF_INFO);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NIIF_INFO;
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Delete()
    {
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
    }

    public int? TrackMenu(IReadOnlyList<(int Id, string Text)> items)
    {
        using var menu = new NativeMenu(items);
        if (!GetCursorPos(out var at)) return null;
        // Windows' own rule for a tray menu: the window is brought to the front first, or the menu does not close
        // when the person clicks away, and it is sent a message after, so the next click on the icon is not lost.
        SetForegroundWindow(_source.Handle);
        var chosen = TrackPopupMenuEx(menu.Handle, TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_RETURNCMD, at.X, at.Y, _source.Handle, IntPtr.Zero);
        PostMessageW(_source.Handle, 0, IntPtr.Zero, IntPtr.Zero);
        return chosen == 0 ? null : chosen;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(Hook);
        _source.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(int message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
