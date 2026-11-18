using System.Globalization;
using System.Runtime.InteropServices;

namespace Bosun.TestChild;

/// <summary>
/// Watches for a person at the desk while the real-input smoke runs (D02 T02 §12). Low-level keyboard and mouse hooks
/// see every input, and Windows marks each the smoke sends (SendInput) as injected (LLKHF_INJECTED, LLMHF_INJECTED):
/// any input without that mark is a person, and its time is written to a file the runner reads between classes.
/// A line "person" on stdin stands in for one, so the runner's tests can show it. It watches four hours at most.
/// </summary>
static class InputWatch
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01, WM_QUIT = 0x0012;

    private static string _file = "";
    private static DateTime _written = DateTime.MinValue, _latest = DateTime.MinValue;
    private static readonly object Gate = new();
    private static HookProc? _keys, _mouse;   // held, so the collector never takes a callback Windows still calls

    public static int Run(string file)
    {
        _file = Path.GetFullPath(file);
        Write(null);
        var thread = GetCurrentThreadId();
        // A person stood in for: each "person" line on stdin is one input not marked injected.
        new Thread(() =>
        {
            for (string? line; (line = Console.In.ReadLine()) is not null;)
                if (line.Trim() == "person") Seen();
        }) { IsBackground = true }.Start();
        new Timer(_ => PostThreadMessage(thread, WM_QUIT, IntPtr.Zero, IntPtr.Zero), null, TimeSpan.FromHours(4), Timeout.InfiniteTimeSpan);

        _keys = (code, wParam, lParam) =>
        {
            if (code >= 0 && (Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).flags & LLKHF_INJECTED) == 0) Seen();
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        _mouse = (code, wParam, lParam) =>
        {
            if (code >= 0 && (Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam).flags & LLMHF_INJECTED) == 0) Seen();
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        var module = GetModuleHandle(null);
        var keys = SetWindowsHookEx(WH_KEYBOARD_LL, _keys, module, 0);
        var mouse = SetWindowsHookEx(WH_MOUSE_LL, _mouse, module, 0);
        if (keys == IntPtr.Zero || mouse == IntPtr.Zero)
        {
            Console.Error.WriteLine($"watch-input: the input hooks could not be set (error {Marshal.GetLastWin32Error()})");
            return 70;
        }
        Console.Out.WriteLine("watching");
        Console.Out.Flush();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref message);
        }
        finally
        {
            UnhookWindowsHookEx(keys);
            UnhookWindowsHookEx(mouse);
        }
        return 0;
    }

    /// <summary>A person's input now: written at most once a second, the latest time always kept.</summary>
    private static void Seen()
    {
        lock (Gate)
        {
            _latest = DateTime.UtcNow;
            if (_latest - _written < TimeSpan.FromSeconds(1)) return;
            _written = _latest;
            Write(_latest);
        }
    }

    private static void Write(DateTime? at)
    {
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, at is { } t ? t.ToString("o", CultureInfo.InvariantCulture) : "none");
        File.Move(temporary, _file, overwrite: true);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x, y; public uint mouseData, flags, time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
