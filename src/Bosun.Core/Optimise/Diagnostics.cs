using System.Security;

namespace Bosun.Core.Optimise;

/// <summary>
/// One fact about the machine: the value read, or the reason it could not be read. Never an exception and never a
/// stand-in 0, so the Optimise page can say "Not available" with why (D05 T02 §3).
/// </summary>
public sealed record Fact<T>
{
    private Fact(T? value, string? reason)
    {
        Value = value;
        Reason = reason;
    }

    public T? Value { get; }

    /// <summary>Why the fact could not be read; null when it was read.</summary>
    public string? Reason { get; }

    public bool IsRead => Reason is null;

    public static Fact<T> Read(T value) => new(value, null);

    public static Fact<T> Unavailable(string reason) => new(default, reason);
}

/// <summary>One configured pagefile: a <c>PagingFiles</c> line.</summary>
/// <param name="Path">The file as configured; <c>?:\pagefile.sys</c> lets Windows choose the drive.</param>
/// <param name="InitialMb">Initial size in MB; null when Windows manages the size.</param>
/// <param name="MaximumMb">Maximum size in MB; null when Windows manages the size.</param>
/// <param name="SystemManaged">The line says <c>0 0</c> or gives no sizes: Windows sizes the file.</param>
public sealed record PagefileConfig(string Path, int? InitialMb, int? MaximumMb, bool SystemManaged);

/// <summary>
/// The pagefile as the registry holds it: what is configured, what is in use and whether Windows fell back to a
/// temporary file, kept apart so a move waiting on a restart is not reported as done.
/// </summary>
/// <param name="Configured"><c>PagingFiles</c>: what the person asked for.</param>
/// <param name="Active"><c>ExistingPageFiles</c>: the files Windows has created, as drive paths.</param>
/// <param name="TemporaryInUse"><c>TempPageFile</c> is set: Windows is running on a temporary pagefile.</param>
public sealed record Pagefile(IReadOnlyList<PagefileConfig> Configured, IReadOnlyList<string> Active, bool TemporaryInUse)
{
    /// <summary>No pagefile is configured.</summary>
    public bool IsEmpty => Configured.Count == 0;

    /// <summary>
    /// The files the configuration names are not the files in use: a change waits on a restart. A file on a drive
    /// Windows chooses (<c>?:</c>) matches an active file of its name on any drive, and with no such file active the
    /// mismatch is proved, so it is pending too (completion review: an empty active list was read as consistent).
    /// </summary>
    public bool PendingRestart
    {
        get
        {
            var unclaimed = Active.ToList();
            foreach (var want in Configured)
            {
                var wildcard = want.Path.StartsWith("?:", StringComparison.Ordinal);
                var match = unclaimed.FindIndex(have => wildcard
                    ? have.Length >= 2 && have[1] == ':' && string.Equals(have[2..], want.Path[2..], StringComparison.OrdinalIgnoreCase)
                    : string.Equals(have, want.Path, StringComparison.OrdinalIgnoreCase));
                if (match < 0) return true;
                unclaimed.RemoveAt(match);
            }
            return unclaimed.Count > 0;
        }
    }
}

/// <summary>
/// A registry value that is listed but could not be read (its query failed for a reason other than its deletion),
/// with why. Kept, so a reader says it can't read the value, never takes it as absent (completion review: a failed
/// approval query was dropped as deleted and read as no record, which is on).
/// </summary>
public sealed record UnreadableValue(string Why);

/// <summary>
/// The WSL memory cap as <c>.wslconfig</c> configures it, read as a file with WSL never started. It is not the
/// running VM's limit.
/// </summary>
/// <param name="Mb">The <c>[wsl2] memory=</c> value in MB; null when none is set and WSL takes half of memory.</param>
public sealed record WslCap(long? Mb)
{
    public bool NoCapSet => Mb is null;

    public string Describe() => Mb is { } mb ? $"{mb} MB" : "no cap set: WSL takes half of memory";
}

/// <summary>One installed memory module, from an SMBIOS type 17 record (the data <c>Win32_PhysicalMemory</c> serves).</summary>
/// <param name="SpeedMts"><c>Speed</c>, the most the module can do, in MT/s; null when SMBIOS says unknown.</param>
/// <param name="ConfiguredSpeedMts"><c>Configured Memory Speed</c>, what it runs at, in MT/s; null when unknown.</param>
public sealed record MemoryModule(
    string? DeviceLocator,
    string? BankLocator,
    string? Manufacturer,
    string? PartNumber,
    int? SizeMb,
    int? SpeedMts,
    int? ConfiguredSpeedMts);

public enum RunHive
{
    CurrentUser,
    LocalMachine,
}

/// <summary>The registry view an entry lives in: the 32-bit one is <c>Wow6432Node</c>, with approvals under <c>Run32</c>.</summary>
public enum RunView
{
    Registry64,
    Registry32,
}

public enum ApprovalState
{
    /// <summary>Windows' record says on.</summary>
    On,

    /// <summary>Windows' record says the person turned it off.</summary>
    TurnedOffByWindows,

    /// <summary>Windows holds no record: on.</summary>
    NoRecord,

    /// <summary>The record could not be read or is not one Windows writes: never read as on.</summary>
    Unreadable,
}

/// <summary>What <c>StartupApproved</c> says of an entry.</summary>
public sealed record StartupApproval(ApprovalState State, string? Reason = null)
{
    public static readonly StartupApproval NoRecord = new(ApprovalState.NoRecord);

    public static StartupApproval Unreadable(string reason) => new(ApprovalState.Unreadable, reason);

    /// <summary>The length of the record Windows writes: four bytes of state, then the time it was turned off.</summary>
    public const int RecordLength = 12;

    /// <summary>
    /// Decodes the value as the registry holds it: the first byte of Windows' 12-byte binary record is 02 or 06 when
    /// the entry is on and 03 or 07 once it is turned off (Settings, Task Manager). No value is no record; a value of
    /// another type or length is unreadable, never on (round 1: a text value and a lone 02 read as on).
    /// </summary>
    public static StartupApproval FromValue(object? value) => value switch
    {
        null => NoRecord,
        byte[] record => FromRecord(record),
        UnreadableValue unreadable => Unreadable($"The approval record could not be read: {unreadable.Why}"),
        var other => Unreadable($"The approval record is a {other.GetType().Name}, not the binary record Windows writes."),
    };

    /// <summary>Decodes a binary record: see <see cref="FromValue"/>.</summary>
    public static StartupApproval FromRecord(byte[]? record)
    {
        if (record is null) return NoRecord;
        if (record.Length != RecordLength)
            return Unreadable($"The approval record is {record.Length} bytes, not the {RecordLength} Windows writes.");
        return record[0] switch
        {
            0x02 or 0x06 => new StartupApproval(ApprovalState.On),
            0x03 or 0x07 => new StartupApproval(ApprovalState.TurnedOffByWindows),
            var other => Unreadable($"The approval record starts with 0x{other:X2}, which Windows does not use."),
        };
    }
}

/// <summary>One Run value, with the hive and view it lives in so a change targets the same value.</summary>
public sealed record StartupItem(string Name, string Command, RunHive Hive, RunView View, StartupApproval Approval);

/// <summary>
/// The Run values read, and the Run keys that could not be: an unreadable key is said, never left out as if empty.
/// </summary>
public sealed record StartupInventory(IReadOnlyList<StartupItem> Items, IReadOnlyList<string> Unreadable);

/// <summary>The pagefile values as the registry holds them, before they are parsed.</summary>
public sealed record RawPagefile(IReadOnlyList<string> PagingFiles, IReadOnlyList<string> ExistingPageFiles, int? TempPageFile);

/// <summary>
/// Where the four facts come from. The real one is the panel's (it needs Windows); a test brings its own. Each
/// method throws <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="SecurityException"/>
/// when its source refuses, and the others are still read.
/// </summary>
public interface IMachineReader
{
    RawPagefile ReadPagefile();

    /// <summary>The text of the profile's <c>.wslconfig</c>, or null when the file is not there.</summary>
    string? ReadWslConfig();

    /// <summary>The raw <c>RSMB</c> firmware table, or null when the machine has none.</summary>
    byte[]? ReadSmbios();

    StartupInventory ReadStartup();
}

/// <summary>Why RAM is below what its part number says it can do, with each module's figures.</summary>
public sealed record ModuleEvidence(string? DeviceLocator, string? BankLocator, string PartNumber, int RatedMts, int ConfiguredMts, int? MaximumMts);

public sealed record RamBelowRating(IReadOnlyList<ModuleEvidence> Modules);

/// <summary>The four facts the Optimise page starts from (design/SPEC.md §12), each a value or a reason.</summary>
public sealed record DiagnosticsReport(
    Fact<Pagefile> Pagefile,
    Fact<WslCap> WslCap,
    Fact<IReadOnlyList<MemoryModule>> Memory,
    Fact<StartupInventory> Startup)
{
    /// <summary>The RAM finding, or null when the evidence does not support one (D05 T02 §1).</summary>
    public RamBelowRating? RamFinding => Memory.IsRead ? Diagnostics.RamFinding(Memory.Value!) : null;
}

public static class Diagnostics
{
    /// <summary>Reads each fact on its own, so a source that refuses costs only its own fact.</summary>
    public static DiagnosticsReport Read(IMachineReader reader) => new(
        Guard("The pagefile", () => PagefileParser.Parse(reader.ReadPagefile())),
        Guard("The WSL cap", () => WslConfigParser.ParseCap(reader.ReadWslConfig())),
        Guard("The memory modules", () => SmbiosParser.ParseModules(reader.ReadSmbios())),
        Guard("The startup entries", () => Fact<StartupInventory>.Read(reader.ReadStartup())));

    private static Fact<T> Guard<T>(string what, Func<Fact<T>> read)
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Fact<T>.Unavailable($"{what} could not be read: {e.Message}");
        }
    }

    /// <summary>
    /// RAM below its rating, only with the evidence: every populated module has a rating decoded from its part number
    /// and a known configured speed below it, and the kit's ratings agree. An unknown part number, an unknown (0)
    /// configured speed, a mixed kit, an empty inventory or a module at its rating gives none.
    /// </summary>
    public static RamBelowRating? RamFinding(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return null;
        var evidence = new List<ModuleEvidence>();
        foreach (var m in modules)
        {
            if (RatedSpeed.Of(m.PartNumber) is not { } rated) return null;
            if (m.ConfiguredSpeedMts is not { } configured || configured >= rated) return null;
            evidence.Add(new ModuleEvidence(m.DeviceLocator, m.BankLocator, m.PartNumber!.Trim(), rated, configured, m.SpeedMts));
        }

        return evidence.Select(e => e.RatedMts).Distinct().Count() == 1 ? new RamBelowRating(evidence) : null;
    }
}
