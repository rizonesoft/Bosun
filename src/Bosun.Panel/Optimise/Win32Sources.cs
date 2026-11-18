using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bosun.Core.Optimise;
using Microsoft.Win32;

namespace Bosun.Panel.Optimise;

/// <summary>The real registry, opened read-only, by hive and view. Nothing here writes.</summary>
public sealed class RegistryKeySource : IRegistrySource
{
    public IReadOnlyDictionary<string, object?>? ReadKey(RunHive hive, RunView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            hive == RunHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            view == RunView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        if (key is null) return null;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in key.GetValueNames()) values[name] = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return values;
    }
}

/// <summary>Files read as files: no process is started, so a <c>.wslconfig</c> read never starts WSL.</summary>
public sealed class ProfileFiles : IProfileFiles
{
    /// <summary>The most a profile file is read to: a <c>.wslconfig</c> is a few lines, so anything larger is not one.</summary>
    public const int MaxBytes = 64 * 1024;

    public string? ReadText(string path)
    {
        try
        {
            // Bounded before it is read (round 1: a large file was read and split whole on the UI's process).
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
                throw new IOException($"{path} is {stream.Length:N0} bytes, more than the {MaxBytes / 1024} KB a .wslconfig can need, so it is not read.");
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

/// <summary><c>GetSystemFirmwareTable('RSMB')</c>: the SMBIOS table, which an unelevated process may read.</summary>
public sealed class Win32FirmwareTable : IFirmwareTable
{
    /// <summary>The signature 'RSMB' as the API takes it, the value of the C literal 'RSMB': the first character in the highest byte.</summary>
    private const uint Rsmb = ((uint)'R' << 24) | ((uint)'S' << 16) | ((uint)'M' << 8) | 'B';

    public byte[]? ReadSmbios()
    {
        var size = GetSystemFirmwareTable(Rsmb, 0, null, 0);
        if (size == 0) throw new IOException($"GetSystemFirmwareTable('RSMB') gave no table: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        var buffer = new byte[size];
        var read = GetSystemFirmwareTable(Rsmb, 0, buffer, size);
        if (read == 0) throw new IOException($"GetSystemFirmwareTable('RSMB') could not read the table: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        return read == size ? buffer : buffer[..(int)read];
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);
}
