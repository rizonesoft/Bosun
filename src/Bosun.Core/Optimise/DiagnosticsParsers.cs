using System.Globalization;
using System.Text;

namespace Bosun.Core.Optimise;

/// <summary>
/// <c>PagingFiles</c> against <c>ExistingPageFiles</c> and <c>TempPageFile</c> under
/// <c>HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management</c>.
/// </summary>
public static class PagefileParser
{
    private const string NtPrefix = @"\??\";

    public static Fact<Pagefile> Parse(RawPagefile raw)
    {
        var configured = new List<PagefileConfig>();
        foreach (var line in raw.PagingFiles.Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (ParseLine(line) is not { } config) return Fact<Pagefile>.Unavailable($"A PagingFiles line is not a path with sizes: '{line}'.");
            configured.Add(config);
        }

        var active = raw.ExistingPageFiles
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Select(p => p.StartsWith(NtPrefix, StringComparison.Ordinal) ? p[NtPrefix.Length..] : p)
            .ToList();
        return Fact<Pagefile>.Read(new Pagefile(configured, active, raw.TempPageFile is not (null or 0)));
    }

    /// <summary><c>C:\pagefile.sys 32768 51200</c>, <c>C:\pagefile.sys 0 0</c> or a bare path (Windows sizes it).</summary>
    private static PagefileConfig? ParseLine(string line)
    {
        var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 3
            && int.TryParse(tokens[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var initial)
            && int.TryParse(tokens[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum))
        {
            var path = string.Join(' ', tokens[..^2]);
            return initial == 0 && maximum == 0
                ? new PagefileConfig(path, null, null, SystemManaged: true)
                : new PagefileConfig(path, initial, maximum, SystemManaged: false);
        }

        // A bare path: no sizes, so Windows manages it. Anything else (a stray size) is not a line Windows writes.
        return tokens.Length == 1 ? new PagefileConfig(line, null, null, SystemManaged: true) : null;
    }
}

/// <summary>The <c>[wsl2] memory=</c> value of <c>%USERPROFILE%\.wslconfig</c>, as configured.</summary>
public static class WslConfigParser
{
    public static Fact<WslCap> ParseCap(string? text)
    {
        if (text is null) return Fact<WslCap>.Read(new WslCap(null));

        var inWsl2 = false;
        string? value = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                // The whole header, never a prefix (round 6: "[wsl2]backup" read as [wsl2]): the name in brackets,
                // then nothing but an optional comment.
                var close = line.IndexOf(']');
                var rest = close < 0 ? "x" : line[(close + 1)..].TrimStart();
                inWsl2 = close > 0 && line[1..close].Trim().Equals("wsl2", StringComparison.OrdinalIgnoreCase)
                    && (rest.Length == 0 || rest[0] is '#' or ';');
                continue;
            }

            if (!inWsl2) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("memory", StringComparison.OrdinalIgnoreCase)) continue;
            var v = line[(eq + 1)..];
            var cut = v.IndexOfAny(['#', ';']);
            value = (cut >= 0 ? v[..cut] : v).Trim().Trim('"');
        }

        if (value is null) return Fact<WslCap>.Read(new WslCap(null));
        return ToMb(value) is { } mb
            ? Fact<WslCap>.Read(new WslCap(mb))
            : Fact<WslCap>.Unavailable($"The .wslconfig memory value '{value}' is not a size WSL accepts.");
    }

    /// <summary>The largest cap read as a size: a petabyte, more than any machine has.</summary>
    public const long MaxCapMb = 1L << 30;

    /// <summary>A number with an optional B, KB, MB, GB or TB suffix; a plain number is bytes.</summary>
    private static long? ToMb(string value)
    {
        var v = value.Trim();
        var digits = v.Length;
        while (digits > 0 && !char.IsAsciiDigit(v[digits - 1])) digits--;
        if (digits == 0) return null;
        var unit = v[digits..].Trim().ToUpperInvariant();
        if (!decimal.TryParse(v[..digits], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
        var bytes = unit switch
        {
            "" or "B" => 1m,
            "KB" => 1024m,
            "MB" => 1024m * 1024,
            "GB" => 1024m * 1024 * 1024,
            "TB" => 1024m * 1024 * 1024 * 1024,
            _ => 0m,
        };
        if (bytes == 0m) return null;
        // Divided first, so no value a decimal holds can overflow (round 1: 1e20 GB threw past the guard and lost
        // all four facts); a cap over a petabyte is no machine's, and is a bad value.
        var perMb = bytes / (1024m * 1024);
        if (n > MaxCapMb / perMb) return null;   // compared before multiplying, so no value can overflow
        var mb = decimal.Floor(n * perMb);
        return mb >= 1 ? (long)mb : null;
    }
}

/// <summary>
/// SMBIOS type 17 (Memory Device) records, by the DMTF layout (DSP0134), from the <c>RSMB</c> firmware table: an
/// 8-byte RawSMBIOSData header (calling method, major, minor, DMI revision, a 32-bit table length), then structures,
/// each a 4-byte header, a formatted area of its stated length and a string set ended by two nulls.
/// </summary>
public static class SmbiosParser
{
    private const int HeaderLength = 8;
    private const int MemoryDeviceType = 17;
    private const int EndOfTableType = 127;

    public static Fact<IReadOnlyList<MemoryModule>> ParseModules(byte[]? raw)
    {
        if (raw is null) return Fact<IReadOnlyList<MemoryModule>>.Unavailable("This machine has no SMBIOS table.");
        if (raw.Length < HeaderLength) return Fact<IReadOnlyList<MemoryModule>>.Unavailable($"The SMBIOS data is {raw.Length} bytes, shorter than its 8-byte header.");
        var declared = BitConverter.ToUInt32(raw, 4);
        if (declared > raw.Length - HeaderLength) return Fact<IReadOnlyList<MemoryModule>>.Unavailable($"The SMBIOS table is truncated: it says {declared} bytes and {raw.Length - HeaderLength} are there.");

        var table = raw.AsSpan(HeaderLength, (int)declared);
        var modules = new List<MemoryModule>();
        var pos = 0;
        while (pos < table.Length)
        {
            if (pos + 4 > table.Length) return Truncated(pos);
            int type = table[pos], length = table[pos + 1];
            if (length < 4) return Fact<IReadOnlyList<MemoryModule>>.Unavailable($"An SMBIOS structure at offset {pos} says it is {length} bytes, less than its header.");
            if (pos + length > table.Length) return Truncated(pos);

            var strings = new List<string>();
            var j = pos + length;
            while (true)
            {
                if (j >= table.Length) return Truncated(pos);
                if (table[j] == 0)
                {
                    // A set with strings ends on one more null; a set with none is two nulls.
                    if (strings.Count == 0)
                    {
                        if (j + 1 >= table.Length || table[j + 1] != 0) return Truncated(pos);
                        j++;
                    }

                    j++;
                    break;
                }

                var end = table[j..].IndexOf((byte)0);
                if (end < 0) return Truncated(pos);
                strings.Add(Encoding.UTF8.GetString(table.Slice(j, end)));
                j += end + 1;
            }

            if (type == EndOfTableType) break;
            if (type == MemoryDeviceType)
            {
                var structure = table.Slice(pos, length).ToArray();
                if (length < 0x0E) return Fact<IReadOnlyList<MemoryModule>>.Unavailable($"A memory device record at offset {pos} is {length} bytes, too short to hold its size.");
                if (Module(structure, strings) is { } module) modules.Add(module);
            }

            pos = j;
        }

        return Fact<IReadOnlyList<MemoryModule>>.Read(modules);
    }

    private static Fact<IReadOnlyList<MemoryModule>> Truncated(int at) =>
        Fact<IReadOnlyList<MemoryModule>>.Unavailable($"The SMBIOS table is truncated in the structure at offset {at}.");

    /// <summary>One type 17 structure; null for an empty slot (Size 0). Each field is read only when the structure is long enough to hold it.</summary>
    private static MemoryModule? Module(byte[] s, List<string> strings)
    {
        var size = BitConverter.ToUInt16(s, 0x0C);
        if (size == 0) return null;
        int? sizeMb = size switch
        {
            0xFFFF => null,
            0x7FFF => s.Length >= 0x20 ? (int)(BitConverter.ToUInt32(s, 0x1C) & 0x7FFFFFFF) : null,
            _ when (size & 0x8000) != 0 => (size & 0x7FFF) / 1024,
            _ => size,
        };

        string? Str(int offset)
        {
            if (s.Length <= offset) return null;
            int index = s[offset];
            if (index == 0 || index > strings.Count) return null;
            var text = strings[index - 1].Trim();
            return text.Length == 0 ? null : text;
        }

        // Speed: MT/s, 0 unknown, 0xFFFF means "use the extended field" (32-bit, bit 31 reserved).
        int? Speed(int offset, int extendedOffset)
        {
            if (s.Length < offset + 2) return null;
            var w = BitConverter.ToUInt16(s, offset);
            if (w == 0) return null;
            if (w != 0xFFFF) return w;
            if (s.Length < extendedOffset + 4) return null;
            var ext = BitConverter.ToUInt32(s, extendedOffset) & 0x7FFFFFFF;
            return ext == 0 ? null : (int)ext;
        }

        return new MemoryModule(Str(0x10), Str(0x11), Str(0x17), Str(0x1A), sizeMb, Speed(0x15, 0x54), Speed(0x20, 0x58));
    }
}
