using System.Globalization;
using System.Runtime.InteropServices;

namespace Bosun.TestChild;

/// <summary>The show command this process was started with, for the runner's tests (StartShow).</summary>
static class StartupShow
{
    public static string Read()
    {
        var info = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        GetStartupInfoW(ref info);
        return string.Create(CultureInfo.InvariantCulture, $"{info.dwFlags} {info.wShowWindow}");
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
