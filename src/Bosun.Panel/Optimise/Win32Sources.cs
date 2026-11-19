using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Bosun.Core.Optimise;
using Microsoft.Win32;

namespace Bosun.Panel.Optimise;

/// <summary>The real registry, opened read-only, by hive and view. Nothing here writes.</summary>
public sealed class RegistryKeySource : IRegistrySource
{
    /// <summary>What a value read after its deletion gives: an object no registry value can be.</summary>
    internal static readonly object Vanished = new();

    public IReadOnlyDictionary<string, object?>? ReadKey(RunHive hive, RunView view, string path)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            hive == RunHive.CurrentUser ? RegistryHive.CurrentUser : RegistryHive.LocalMachine,
            view == RunView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(path);
        if (key is null) return null;
        return Collect(key.GetValueNames(), (name, missing) => key.GetValue(name, missing, RegistryValueOptions.DoNotExpandEnvironmentNames),
            name => key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Each listed value read by name. The registry answers the default both for a value deleted since the listing and
    /// for a query that failed, so a default is checked against the key again: a value no longer listed was deleted
    /// (an uninstaller at work) and is skipped, never an entry with no command (round 7); one still listed could not
    /// be read and is kept as <see cref="UnreadableValue"/>, never taken as absent (completion review).
    /// </summary>
    internal static Dictionary<string, object?> Collect(IEnumerable<string> names, Func<string, object, object?> read, Func<string, bool> stillListed)
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var value = read(name, Vanished);
            if (!ReferenceEquals(value, Vanished)) values[name] = value;
            else if (stillListed(name)) values[name] = new UnreadableValue($"the registry would not give the value {name}, though it lists it");
        }
        return values;
    }
}

/// <summary>Files read as files: no process is started, so a <c>.wslconfig</c> read never starts WSL.</summary>
public sealed partial class ProfileFiles : IProfileFiles
{
    /// <summary>
    /// The most of a profile file kept: its settings, a few lines in a <c>.wslconfig</c>. Comment and blank lines are
    /// read past and never kept, so a file of any length is read in bounded memory (round 1) and a long commented one
    /// still gives its settings (round 4).
    /// </summary>
    public const int MaxKeptChars = 64 * 1024;

    /// <summary>
    /// The most of a profile file read at all, in characters: a <c>.wslconfig</c> is kilobytes, so past this the file
    /// is said to be too large rather than scanned on (round 6: a multi-gigabyte comment held the read up).
    /// </summary>
    public const long MaxScannedChars = 16L * 1024 * 1024;

    public string? ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return Settings(reader, path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }
}

public sealed partial class ProfileFiles
{
    /// <summary>
    /// The file's setting lines, read a character at a time: a line is kept unless it is blank or a comment (first
    /// non-blank character <c>#</c> or <c>;</c>, as <c>WslConfigParser</c> reads them), a comment is read past however
    /// long, and the kept text past <see cref="MaxKeptChars"/> is a fault, said, never read on.
    /// </summary>
    internal static string Settings(TextReader reader, string path)
    {
        var kept = new System.Text.StringBuilder();
        var line = new System.Text.StringBuilder();
        var comment = false;     // the line so far is a comment: read past, nothing kept
        var started = false;     // a non-blank character was seen on this line
        int c;
        long scanned = 0;
        while ((c = reader.Read()) != -1)
        {
            if (++scanned > MaxScannedChars)
                throw new IOException($"{path} is longer than {MaxScannedChars / (1024 * 1024)} MB, more than a .wslconfig can be, so it is not read.");
            var ch = (char)c;
            if (ch == '\n')
            {
                if (!comment && started) kept.Append(line).Append('\n');
                line.Clear();
                comment = started = false;
                continue;
            }
            if (comment) continue;
            // Whitespace before a line's first character is neither kept nor counted (round 6: 65,537 leading
            // spaces counted against the bound before the line was known to be blank).
            if (!started && char.IsWhiteSpace(ch)) continue;
            if (!started)
            {
                started = true;
                if (ch is '#' or ';')
                {
                    comment = true;
                    line.Clear();
                    continue;
                }
            }
            line.Append(ch);
            if (kept.Length + line.Length > MaxKeptChars)
                throw new IOException($"{path} holds more than {MaxKeptChars / 1024} KB of settings, more than a .wslconfig can need, so it is not read.");
        }
        if (!comment && started) kept.Append(line).Append('\n');
        return kept.ToString();
    }
}

/// <summary><c>GetSystemFirmwareTable('RSMB')</c>: the SMBIOS table, which an unelevated process may read.</summary>
public sealed class Win32FirmwareTable : IFirmwareTable
{
    /// <summary>The signature 'RSMB' as the API takes it, the value of the C literal 'RSMB': the first character in the highest byte.</summary>
    private const uint Rsmb = ((uint)'R' << 24) | ((uint)'S' << 16) | ((uint)'M' << 8) | 'B';

    public byte[]? ReadSmbios() => Read(GetSystemFirmwareTable);

    /// <summary>
    /// Asks the size, then reads into a buffer of it. The API answers a buffer too small with the size it needs
    /// (round 4: a table that grew between the calls was sliced past its buffer and threw past the guard), so a
    /// larger answer is asked again, a few times, and then said as a fault of this fact alone.
    /// </summary>
    internal static byte[] Read(Func<uint, uint, byte[]?, uint, uint> call)
    {
        var size = call(Rsmb, 0, null, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (size == 0) throw new IOException($"GetSystemFirmwareTable('RSMB') gave no table: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            var buffer = new byte[size];
            var read = call(Rsmb, 0, buffer, size);
            if (read == 0) throw new IOException($"GetSystemFirmwareTable('RSMB') could not read the table: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            if (read <= size) return read == size ? buffer : buffer[..(int)read];
            size = read;   // it grew: read again at the size it needs
        }
        throw new IOException("GetSystemFirmwareTable('RSMB') kept growing between reads, so the table was not read.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint signature, uint tableId, byte[]? buffer, uint size);
}
