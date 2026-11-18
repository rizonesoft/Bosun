using System.Globalization;
using System.Runtime.InteropServices;

namespace Bosun.TestChild;

/// <summary>
/// Watches for a person at the desk while the real-input smoke runs (D02 T02 §12). Low-level keyboard and mouse hooks
/// see every input. The smoke's own is injected (LLKHF_INJECTED, LLMHF_INJECTED) and carries its mark in dwExtraInfo
/// (Native.SmokeTag in tests/Bosun.Panel.E2E, the same value as <see cref="SmokeTag"/>); anything else is a person,
/// injected or not (a remote-control tool injects too).
/// The record the runner reads is one file of two lines, written whole together (by a writer thread, never inside a
/// hook): the latest person's time (or "none"), and the keys and buttons the smoke pressed and has not let go of
/// (virtual-key codes, comma-separated). A class ended mid-drag leaves those down, and the runner lets go of them, never
/// one a person holds (a person pressing a key the smoke holds takes it off the list).
/// A line "person" on stdin stands in for one, so the runner's tests can show it. It watches four hours at most.
/// </summary>
static class InputWatch
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01, WM_QUIT = 0x0012;
    private const long SmokeTag = 0x42534E31; // "BSN1", as Native.SmokeTag

    private static string _file = "";
    private static DateTime _latest = DateTime.MinValue;
    private static readonly AutoResetEvent Wake = new(false);
    private static readonly object Gate = new();
    private static readonly SortedSet<int> SmokeHeld = [];   // under Gate: the hooks change it, the writer reads it
    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105,
        WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
    private static HookProc? _keys, _mouse;   // held, so the collector never takes a callback Windows still calls

    public static int Run(string file)
    {
        _file = Path.GetFullPath(file);
        var thread = GetCurrentThreadId();

        _keys = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var stroke = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var smoke = Smokes((stroke.flags & LLKHF_INJECTED) != 0, stroke.extra);
                if (!smoke) Seen();
                var message = (uint)wParam.ToInt64();
                Track((int)stroke.vkCode, message is WM_KEYDOWN or WM_SYSKEYDOWN, message is WM_KEYUP or WM_SYSKEYUP, smoke);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        _mouse = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var pointer = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var smoke = Smokes((pointer.flags & LLMHF_INJECTED) != 0, pointer.extra);
                if (!smoke) Seen();
                var message = (uint)wParam.ToInt64();
                var button = message switch { WM_LBUTTONDOWN or WM_LBUTTONUP => 1, WM_RBUTTONDOWN or WM_RBUTTONUP => 2, WM_MBUTTONDOWN or WM_MBUTTONUP => 4, _ => 0 };
                if (button != 0) Track(button, message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN, message is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP, smoke);
            }
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
        // Only now, with both hooks in, is the watcher ready: the file and the line say so, and the runner reads the
        // line as the proof (a watcher whose hooks failed exits 70 having written nothing).
        Write(null, "");
        new Thread(Writer) { IsBackground = true, Name = "watch-input writer" }.Start();
        // A person stood in for: each "person" line on stdin is one input not marked injected.
        new Thread(() =>
        {
            for (string? line; (line = Console.In.ReadLine()) is not null;)
                if (line.Trim() == "person") Seen();
        }) { IsBackground = true }.Start();
        // Held to the end: a timer nothing references can be collected, and the four-hour cap would never come.
        using var quit = new Timer(_ => PostThreadMessage(thread, WM_QUIT, IntPtr.Zero, IntPtr.Zero), null, TimeSpan.FromHours(4), Timeout.InfiniteTimeSpan);
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

    /// <summary>
    /// Keeps the list of what the smoke holds down: its own press adds a key, any release of it, or a person's press of
    /// it (the key is the person's now), takes it off. The hooks only change memory and wake the writer: a hook that
    /// waits on the disk past Windows' hook timeout is removed without a word, and the desk would go unwatched.
    /// </summary>
    private static void Track(int key, bool down, bool up, bool smoke)
    {
        lock (Gate)
        {
            var changed = down && smoke ? SmokeHeld.Add(key) : (up || down) && SmokeHeld.Remove(key);
            if (changed) Wake.Set();
        }
    }

    /// <summary>Whether an input is the smoke's own: injected, and carrying its mark.</summary>
    private static bool Smokes(bool injected, IntPtr extra) => injected && extra.ToInt64() == SmokeTag;

    /// <summary>A person's input now: kept in memory, written by the writer.</summary>
    private static void Seen()
    {
        lock (Gate)
        {
            _latest = DateTime.UtcNow;
            Wake.Set();
        }
    }

    /// <summary>
    /// The writer, on a thread of its own: each time it is woken it writes the record anew, the latest person's time and
    /// the held list taken together, through a temporary file. A stalled disk delays the record and never the hooks. A write
    /// that fails ends the watcher (exit 71): a record it can no longer keep must not go on saying nobody is there, and
    /// the runner stops a run whose watcher has ended.
    /// </summary>
    private static void Writer()
    {
        DateTime written = DateTime.MinValue;
        string heldWritten = "";
        while (true)
        {
            Wake.WaitOne(TimeSpan.FromSeconds(1));
            DateTime latest;
            string held;
            lock (Gate)
            {
                latest = _latest;
                held = string.Join(",", SmokeHeld);
            }
            try
            {
                if (latest != written || held != heldWritten)
                {
                    Write(latest == DateTime.MinValue ? null : latest, held);
                    written = latest;
                    heldWritten = held;
                }
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"watch-input: the record could not be written, so the watcher ends: {failed.Message}");
                Environment.Exit(71);
            }
        }
    }

    /// <summary>The record, both lines at once: whoever reads it sees one moment, never a time from one and keys from another.</summary>
    private static void Write(DateTime? at, string held)
    {
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, (at is { } t ? t.ToString("o", CultureInfo.InvariantCulture) : "none") + "\n" + held + "\n");
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
