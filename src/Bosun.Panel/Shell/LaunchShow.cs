using System.Runtime.InteropServices;

namespace Bosun.Panel.Shell;

/// <summary>
/// How the program that started the panel asked its first window to be shown (the show command in STARTUPINFO, the
/// <c>nCmdShow</c> every Windows program is started with). WPF reads none of it, so the panel does: a start asking that
/// the window not be activated (a shortcut set to start minimised, or the end-to-end smoke, D02 T02 §12) leaves the
/// person's window in front.
/// </summary>
public static class LaunchShow
{
    private const int STARTF_USESHOWWINDOW = 0x1;
    private const short SW_SHOWMINNOACTIVE = 7, SW_SHOWNA = 8, SW_SHOWNOACTIVATE = 4;

    /// <summary>Whether a window shown under this show command takes the foreground.</summary>
    public static bool Activates(int flags, short show) =>
        (flags & STARTF_USESHOWWINDOW) == 0 || show is not (SW_SHOWNOACTIVATE or SW_SHOWNA or SW_SHOWMINNOACTIVE);

    /// <summary>Whether this process's first window should take the foreground, as it was started.</summary>
    public static bool FirstWindowActivates()
    {
        var info = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        GetStartupInfoW(ref info);
        return Activates(info.dwFlags, info.wShowWindow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [DllImport("kernel32.dll")]
    private static extern void GetStartupInfoW(ref STARTUPINFO info);
}
