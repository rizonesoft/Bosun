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
/// A line "release" on stdin has the watcher let go of what the smoke holds, from its own list in memory (exact: a
/// person's press takes a key off it in the hook, at once), and answer "released &lt;keys&gt;". A watcher whose record can no
/// longer be written lets go of the same before it ends.
/// A line "person" on stdin stands in for one, so the runner's tests can show it. It watches four hours at most.
/// </summary>
static class InputWatch
{
    private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    private const uint LLKHF_INJECTED = 0x10, LLMHF_INJECTED = 0x01, WM_QUIT = 0x0012;
    private const long SmokeTag = 0x42534E31; // "BSN1", as Native.SmokeTag
    // The watcher's own releases ("BSN2"): smoke input too, and decided again by the hook as each one arrives.
    private const long ReleaseTag = 0x42534E32;

    private static string _file = "";
    private static DateTime _latest = DateTime.MinValue;
    private static readonly AutoResetEvent Wake = new(false);
    private static EventWaitHandle? _person;
    private static readonly object Gate = new();
    private static readonly SortedSet<int> SmokeHeld = [];   // under Gate: the hooks change it, the writer reads it
    private const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105,
        WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208;
    private static HookProc? _keys, _mouse;   // held, so the collector never takes a callback Windows still calls

    /// <param name="person">A named event (manual reset) set the moment a person's input comes, inside the hook: a kernel
    /// object, so the runner hears of a person at once, whatever the disk is doing with the record.</param>
    public static int Run(string file, string? person = null)
    {
        _person = person is null ? null : new EventWaitHandle(false, EventResetMode.ManualReset, person);
        _file = Path.GetFullPath(file);
        var thread = GetCurrentThreadId();

        _keys = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var stroke = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var message = (uint)wParam.ToInt64();
                var injected = (stroke.flags & LLKHF_INJECTED) != 0;
                if (Input(Smokes(injected, stroke.extra), (int)stroke.vkCode, message is WM_KEYDOWN or WM_SYSKEYDOWN, message is WM_KEYUP or WM_SYSKEYUP, Releases(injected, stroke.extra)))
                    return new IntPtr(1);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        _mouse = (code, wParam, lParam) =>
        {
            if (code >= 0)
            {
                var pointer = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                var message = (uint)wParam.ToInt64();
                var button = message switch { WM_LBUTTONDOWN or WM_LBUTTONUP => 1, WM_RBUTTONDOWN or WM_RBUTTONUP => 2, WM_MBUTTONDOWN or WM_MBUTTONUP => 4, _ => 0 };
                var injected = (pointer.flags & LLMHF_INJECTED) != 0;
                if (Input(Smokes(injected, pointer.extra), button, message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN, message is WM_LBUTTONUP or WM_RBUTTONUP or WM_MBUTTONUP, Releases(injected, pointer.extra)))
                    return new IntPtr(1);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        };
        // The first record before the hooks: once they are in, nothing on this thread may wait on the disk.
        Write(null, "");
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
        new Thread(Writer) { IsBackground = true, Name = "watch-input writer" }.Start();
        // The runner's requests on stdin: "release" (let go of what the smoke holds), and "person", standing in for one
        // input not marked injected.
        new Thread(() =>
        {
            for (string? line; (line = Console.In.ReadLine()) is not null;)
            {
                if (line.Trim() == "person") Input(smoke: false, key: 0, down: false, up: false, release: false);
                if (line.Trim() == "release")
                {
                    var let = ReleaseHeld();
                    Console.Out.WriteLine("released " + string.Join(",", let));
                    Console.Out.Flush();
                }
            }
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
    /// <param name="key">The virtual key or mouse button (1, 2, 4) the event presses or lets go of; 0 for none (a move).</param>
    /// <remarks>
    /// One event, one change under the lock: a person's time and who holds the key move together, so the writer never
    /// writes a person's press beside a list that still gives the smoke the key they now hold.
    /// </remarks>
    /// <param name="release">The watcher's own release of a key: it passes only while the smoke still holds that key.</param>
    /// <returns>True when the event is to be stopped here: a release of a key a person took over since it was sent, so
    /// no application ever sees it let go of under them. The decision is made as the event arrives, under the lock.</returns>
    private static bool Input(bool smoke, int key, bool down, bool up, bool release)
    {
        lock (Gate)
        {
            if (release && up && key != 0 && !SmokeHeld.Contains(key)) return true;
            var changed = false;
            if (!smoke) { _latest = DateTime.UtcNow; changed = true; _person?.Set(); }
            if (key != 0) changed |= down && smoke ? SmokeHeld.Add(key) : (up || down) && SmokeHeld.Remove(key);
            if (changed) Wake.Set();
            return false;
        }
    }

    /// <summary>
    /// Lets go of each key and button the smoke holds, by its own list. Each release carries the watcher's own mark, and
    /// the hook passes it only if the smoke still holds the key as it arrives: one a person took in between is left to
    /// them. Not under the lock while sending, since the hook takes it.
    /// </summary>
    private static int[] ReleaseHeld()
    {
        int[] keys;
        lock (Gate) keys = [.. SmokeHeld];
        var let = new List<int>();
        foreach (var key in keys)
        {
            lock (Gate)
                if (!SmokeHeld.Contains(key)) continue;
            var input = new INPUT[1];
            if (key is 1 or 2 or 4)
            {
                input[0].type = 0;
                input[0].u.mi.dwFlags = key == 1 ? 0x0004 : key == 2 ? 0x0010 : 0x0040;
                input[0].u.mi.dwExtraInfo = new IntPtr(ReleaseTag);
            }
            else
            {
                input[0].type = 1;
                input[0].u.ki.wVk = (ushort)key;
                input[0].u.ki.dwFlags = 0x0002;
                input[0].u.ki.dwExtraInfo = new IntPtr(ReleaseTag);
            }
            if (SendInput(1, input, Marshal.SizeOf<INPUT>()) == 1) let.Add(key);
        }
        return [.. let];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public int dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public int type; public INPUTUNION u; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>Whether an input is the smoke's own: injected, and carrying its mark.</summary>
    private static bool Smokes(bool injected, IntPtr extra) => injected && extra.ToInt64() is SmokeTag or ReleaseTag;

    /// <summary>Whether an input is the watcher's own release.</summary>
    private static bool Releases(bool injected, IntPtr extra) => injected && extra.ToInt64() == ReleaseTag;

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
                // What the smoke holds is let go of first: the runner, told only that the watcher ended, cannot know it.
                ReleaseHeld();
                Environment.Exit(71);
            }
        }
    }

    /// <summary>The record, both lines at once: whoever reads it sees one moment, never a time from one and keys from another.</summary>
    /// <remarks>
    /// The replace is tried for a quarter of a second: a reader holding the record that moment (the runner reads it with
    /// delete sharing, so the replace goes through; any other reader may briefly hold it) only delays it.
    /// </remarks>
    private static void Write(DateTime? at, string held)
    {
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, (at is { } t ? t.ToString("o", CultureInfo.InvariantCulture) : "none") + "\n" + held + "\n");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temporary, _file, overwrite: true);
                return;
            }
            // A reader holding the record without delete sharing makes the replace fail, as access denied.
            catch (Exception refused) when (refused is IOException or UnauthorizedAccessException && attempt < 10)
            {
                Thread.Sleep(25);
            }
        }
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
