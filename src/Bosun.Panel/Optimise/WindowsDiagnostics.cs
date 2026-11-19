using System.IO;
using Bosun.Core.Optimise;

namespace Bosun.Panel.Optimise;

/// <summary>
/// The registry as the diagnostics read it, by hive and view. The real one is <see cref="RegistryKeySource"/>; a test
/// brings its own. Implementations throw <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or
/// <see cref="System.Security.SecurityException"/> when the registry refuses.
/// </summary>
public interface IRegistrySource
{
    /// <summary>Every value of the key by name (strings, string arrays, numbers, byte arrays), or null when the key is not there.</summary>
    IReadOnlyDictionary<string, object?>? ReadKey(RunHive hive, RunView view, string path);
}

/// <summary>The profile's files, read as files.</summary>
public interface IProfileFiles
{
    /// <summary>The file's text, or null when it is not there.</summary>
    string? ReadText(string path);
}

/// <summary>The firmware tables Windows hands to an unelevated process.</summary>
public interface IFirmwareTable
{
    /// <summary>The raw <c>RSMB</c> table, or null when the machine has none.</summary>
    byte[]? ReadSmbios();
}

/// <summary>
/// The four facts read on Windows, with no elevation and without starting WSL: the registry for the pagefile and the
/// Run keys, the profile's <c>.wslconfig</c> as a file, and the SMBIOS table. The rules are in
/// <c>Bosun.Core.Optimise</c>; this only gathers (as <c>RegistryRunKey</c> does for <c>StartupEntry</c>).
/// </summary>
public sealed class WindowsDiagnostics(IRegistrySource registry, IProfileFiles files, IFirmwareTable firmware, string profilePath) : IMachineReader
{
    public const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    public const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ApprovedRun32 = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";

    public DiagnosticsReport Read() => Diagnostics.Read(this);

    public RawPagefile ReadPagefile()
    {
        var values = registry.ReadKey(RunHive.LocalMachine, RunView.Registry64, MemoryManagement)
            ?? throw new IOException("The Memory Management key is not in the registry.");
        return new RawPagefile(Lines(values, "PagingFiles"), Lines(values, "ExistingPageFiles"), Flag(values, "TempPageFile"));
    }

    public string? ReadWslConfig() => files.ReadText(Path.Combine(profilePath, ".wslconfig"));

    public byte[]? ReadSmbios() => firmware.ReadSmbios();

    /// <summary>
    /// The Run values of the user, and of the machine in both registry views, each with Windows' approval record
    /// from the matching <c>StartupApproved</c> key (<c>Run32</c> for the 32-bit view). A key that cannot be read is
    /// named in <see cref="StartupInventory.Unreadable"/>; an approval key that cannot be read makes its entries
    /// unreadable, never on. Throws only when every Run key is refused.
    /// </summary>
    public StartupInventory ReadStartup()
    {
        var items = new List<StartupItem>();
        var unreadable = new List<string>();
        Scope(RunHive.CurrentUser, RunView.Registry64, ApprovedRun, "HKCU Run");
        Scope(RunHive.LocalMachine, RunView.Registry64, ApprovedRun, "HKLM Run (64-bit)");
        Scope(RunHive.LocalMachine, RunView.Registry32, ApprovedRun32, "HKLM Run (32-bit)");
        if (items.Count == 0 && unreadable.Count == 3) throw new IOException(string.Join(" ", unreadable));
        return new StartupInventory(items, unreadable);

        void Scope(RunHive hive, RunView view, string approvedPath, string label)
        {
            IReadOnlyDictionary<string, object?>? run;
            try
            {
                run = registry.ReadKey(hive, view, Run);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable.Add($"{label} could not be read: {e.Message}");
                return;
            }

            if (run is null || run.Count == 0) return;

            // Approvals live in the 64-bit view of the hive, under Run for 64-bit entries and Run32 for 32-bit ones.
            IReadOnlyDictionary<string, object?>? approved = null;
            string? approvedFailure = null;
            try
            {
                approved = registry.ReadKey(hive, RunView.Registry64, approvedPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                approvedFailure = $"{label} approvals could not be read: {e.Message}";
            }

            // Indexed once by name, as Windows compares value names (case-insensitively): each entry's approval is
            // one lookup, never a scan of every approval (round 8: 10,000 of each was 100 million comparisons).
            var approvals = approved is null ? null : Index(approved);
            foreach (var (name, value) in run)
            {
                var command = value as string ?? $"(a {value?.GetType().Name ?? "missing"} value, not text)";
                var approval = approvedFailure is not null
                    ? StartupApproval.Unreadable(approvedFailure)
                    : StartupApproval.FromValue(approvals?.GetValueOrDefault(name));
                items.Add(new StartupItem(name, command, hive, view, approval));
            }
        }
    }

    /// <summary>A key's values by name, compared as Windows compares value names; the first of two that differ only by case is kept.</summary>
    internal static Dictionary<string, object?> Index(IReadOnlyDictionary<string, object?> values)
    {
        var index = new Dictionary<string, object?>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) index.TryAdd(name, value);
        return index;
    }

    private static object? Find(IReadOnlyDictionary<string, object?> values, string name) =>
        values.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>
    /// A DWORD flag: null when the value is not there; a value of another type is the registry's fault, said, as for
    /// the file lists, never read as absent (round 9).
    /// </summary>
    private static int? Flag(IReadOnlyDictionary<string, object?> values, string name) => Find(values, name) switch
    {
        null => null,
        int flag => flag,
        var other => throw new IOException($"{name} is a {other.GetType().Name}, not the number Windows writes."),
    };

    /// <summary>
    /// A multi-string value's lines: none when the value is not there; a value of another type is the registry's fault,
    /// said, never read as an empty configuration (round 2).
    /// </summary>
    private static string[] Lines(IReadOnlyDictionary<string, object?> values, string name) => Find(values, name) switch
    {
        null => [],
        string[] many => many,
        string one => [one],
        var other => throw new IOException($"{name} is a {other.GetType().Name}, not the list of files Windows writes."),
    };
}
