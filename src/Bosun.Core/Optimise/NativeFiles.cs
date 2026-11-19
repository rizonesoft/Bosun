using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bosun.Core.Optimise;

/// <summary>
/// The Windows handle operations the elevated helper's confinement stands on (D05 T02 §6): a file or folder is opened
/// once and what the open handle really is gets asked of the handle (<c>GetFinalPathNameByHandle</c>), so there is no
/// gap between checking a path and using it. Every member is Windows only; the callers check <see cref="OperatingSystem.IsWindows"/>.
/// </summary>
internal static class NativeFiles
{
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x00010000;
    private const uint ShareRead = 1, ShareWrite = 2;
    private const uint CreateNew = 1, OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int FileDispositionInfo = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);

    /// <summary>The path the open handle really is, with every link followed, without the <c>\\?\</c> prefix.</summary>
    public static string FinalPath(SafeFileHandle handle)
    {
        var builder = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        if (length > builder.Capacity)
        {
            builder = new StringBuilder((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, builder, (uint)builder.Capacity, 0);
        }
        if (length == 0) throw new IOException("the final path of an open handle could not be read", new Win32Exception(Marshal.GetLastWin32Error()));
        var path = builder.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// Opens a folder so that it can't be removed or renamed while the handle is held (no delete sharing), following any
    /// link: the handle is the folder the path really leads to, and its <see cref="FinalPath"/> says which.
    /// </summary>
    public static SafeFileHandle OpenFolder(string path)
    {
        var handle = CreateFileW(path, GenericRead, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"'{path}' could not be opened", new Win32Exception(Marshal.GetLastWin32Error()));
        return handle;
    }

    /// <summary>Opens a folder that holds nothing back: null when it is not there or can't be opened.</summary>
    public static string? TryFinalPathOfFolder(string path)
    {
        try
        {
            using var handle = OpenFolder(path);
            return FinalPath(handle);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Creates a file that must not exist yet, shared with nobody, that can be deleted through its own handle.</summary>
    public static FileStream CreateExclusive(string path)
    {
        var handle = CreateFileW(path, GenericWrite | Delete, 0, IntPtr.Zero, CreateNew, 0x80 /* FILE_ATTRIBUTE_NORMAL */, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw error is 80 or 183 ? new IOException($"The file '{path}' already exists.") : new IOException($"'{path}' could not be created", new Win32Exception(error));
        }
        return new FileStream(handle, FileAccess.Write);
    }

    /// <summary>Marks the file for deletion through its own open handle: it is gone when the handle closes.</summary>
    public static void DeleteOnClose(FileStream stream)
    {
        byte on = 1;
        if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfo, ref on, 1))
            throw new IOException("the file could not be marked for deletion", new Win32Exception(Marshal.GetLastWin32Error()));
    }
}
