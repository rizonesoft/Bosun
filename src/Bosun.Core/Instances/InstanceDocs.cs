using System.Text.Json.Serialization;
using Bosun.Core.Memory;

namespace Bosun.Core.Instances;

/// <summary>
/// <c>bosun instances --json</c> from either side (D03 T02 §1): the shape <c>dm::instances_json</c> writes on Windows and
/// <c>wsl/bosun_log.py</c> writes in WSL. Every field is required, a null standing only where the tool writes one.
/// </summary>
public sealed record InstancesDoc : ICheckedDoc
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "claude", "codex", "agy", "code", "antigravity", "job" };
    public static readonly IReadOnlySet<string> States = new HashSet<string> { "working", "reviewing", "idle", "unknown" };
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "processes", "sessions", "review-marks", "queue-records", "editor-windows", "editor-coverage", "automount" };

    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("distro")] public required string? Distro { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("instances")] public required IReadOnlyList<InstanceDoc> Instances { get; init; }
    [JsonPropertyName("unreadable")] public required long Unreadable { get; init; }
    [JsonPropertyName("problems")] public required IReadOnlyList<SourceProblemDoc> Problems { get; init; }

    /// <summary>
    /// The Windows drives this distro has mounted now (D03 T02 §6), from its mount table; null when it could not be read
    /// (with an <c>automount</c> problem) or the report is older than the field: then no guest path is taken for a drive's.
    /// </summary>
    [JsonPropertyName("drives")] public IReadOnlyList<DriveMount>? Drives { get; init; }

    /// <summary>A WSL distro's name: letters, digits, dot, underscore and hyphen, at most 64.</summary>
    private static readonly System.Text.RegularExpressions.Regex DistroName = new("^[A-Za-z0-9._-]{1,64}$");

    private static string Cut(string? v) => v is null ? "" : v.Length > 64 ? v[..64] + "..." : v;

    public string? Fault()
    {
        if (Lists.NullIn(("instances", Instances), ("problems", Problems)) is { } nulls) return nulls;
        if (Side is not ("windows" or "wsl")) return $"its side is '{Side}', not windows or wsl";
        if (Unreadable < 0) return "its unreadable count is negative";
        // A distro's name, as WSL allows it: never a 128 KiB string copied into every notice (round 1).
        if (Distro is { } d && !DistroName.IsMatch(d)) return $"it names the distro '{(d.Length > 64 ? d[..64] + "..." : d)}'";
        foreach (var i in Instances)
        {
            if (!Kinds.Contains(i.Kind)) return $"instance {i.Pid} is of kind '{i.Kind}', which no scanner writes";
            if (!States.Contains(i.State)) return $"instance {i.Pid}'s state is '{i.State}', which no scanner writes";
            if (i.Side is not ("windows" or "wsl")) return $"instance {i.Pid}'s side is '{i.Side}'";
            if (i.Kind != "job" && i.Side != Side) return $"instance {i.Pid} is a {i.Side} {i.Kind} in the {Side} document";
            if (i.Pid <= 0) return $"an instance has the pid {i.Pid}";
            if (i.PrivateMb < 0) return $"instance {i.Pid}'s memory is negative";
            if (i.GuestDistro is { } g && !DistroName.IsMatch(g)) return $"instance {i.Pid} names the distro '{(g.Length > 64 ? g[..64] + "..." : g)}'";
            if (WindowFault(i) is { } window) return window;
        }
        foreach (var p in Problems)
            if (!Sources.Contains(p.Source)) return $"a problem names the source '{p.Source}', which no scanner reads";
        if (Drives is { } drives)
        {
            // Bounded by what the logger reads of its mount table (4 MiB), never by a guess at how many mounts there are (round 7).
            if (drives.Count > 200_000) return $"it lists {drives.Count} drive mounts, more than a mount table the logger reads holds";
            foreach (var mount in drives)
                if (mount is null || !DriveMounts.IsValid(mount)) return $"it lists a drive mount that is not a Linux folder and a Windows folder ('{Cut(mount?.Mount)}' for '{Cut(mount?.Windows)}')";
        }
        return null;
    }

    /// <summary>
    /// An editor's window row (D03 T02 §4): its location is the side it runs on, or WSL for a WSL window of a Windows
    /// editor, which then names its distro and a Linux folder; only an editor row is placed by a window.
    /// </summary>
    private static string? WindowFault(InstanceDoc i)
    {
        if (i.PlacedBy is not (null or "window" or "launch")) return $"instance {i.Pid} is placed by '{i.PlacedBy}'";
        if (i.PlacedBy is not null && i.Kind != "code") return $"instance {i.Pid} is a {i.Kind}, which no window places";
        if ((i.PlacedBy == "window") != (i.Window is not null)) return $"instance {i.Pid} has a window without being placed by one, or the reverse";
        if (i.Window is { Length: 0 or > 200 }) return $"instance {i.Pid} has a window identity no scanner writes";
        if (i.SharedMb < 0) return $"instance {i.Pid}'s shared memory is negative";
        if (i.Location is not (null or "windows" or "wsl")) return $"instance {i.Pid}'s location is '{i.Location}'";
        if (i.Location == "windows" && i.Distro is not null) return $"instance {i.Pid} is in a Windows folder and names a distro";
        if (i.Location == "wsl")
        {
            if (i.Distro is not { } d || !DistroName.IsMatch(d)) return $"instance {i.Pid} is in WSL and names no distro it could be in";
            if (i.Cwd is { } cwd && !cwd.StartsWith('/')) return $"instance {i.Pid} is in WSL with the folder '{cwd}'";
        }
        return null;
    }
}

/// <summary>One agent, editor or job, as its side's scanner saw it.</summary>
public sealed record InstanceDoc
{
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("pid")] public required long Pid { get; init; }
    [JsonPropertyName("cwd")] public required string? Cwd { get; init; }
    [JsonPropertyName("repo")] public required string? Repo { get; init; }
    [JsonPropertyName("private_mb")] public required long? PrivateMb { get; init; }
    [JsonPropertyName("started_at")] public required string? StartedAt { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("evidence")] public required string? Evidence { get; init; }
    [JsonPropertyName("slot")] public required int? Slot { get; init; }
    [JsonPropertyName("command")] public required string? Command { get; init; }
    [JsonPropertyName("guest_pid")] public required long? GuestPid { get; init; }
    [JsonPropertyName("guest_distro")] public required string? GuestDistro { get; init; }

    // An editor placed by its windows (D03 T02 §4). The Windows scanner writes these; WSL's writes none, so each is
    // optional, and absent reads as null.

    /// <summary>The window and root this row is, stable across scans: the window's session and a hash of the root's URI (<c>#empty</c> for a window with none), so removing one root never moves another's.</summary>
    [JsonPropertyName("window")] public string? Window { get; init; }

    /// <summary>Where the row's folder is: "windows", or "wsl" for a WSL window of a Windows editor; null when not said.</summary>
    [JsonPropertyName("location")] public string? Location { get; init; }

    /// <summary>The distro a WSL-located row's folder is in.</summary>
    [JsonPropertyName("distro")] public string? Distro { get; init; }

    /// <summary>"window" (by its open window) or "launch" (no window record: the folder it was started in).</summary>
    [JsonPropertyName("placed_by")] public string? PlacedBy { get; init; }

    /// <summary>The editor's memory, which every row of its process shares; its first row alone carries <see cref="PrivateMb"/>.</summary>
    [JsonPropertyName("shared_mb")] public long? SharedMb { get; init; }

    /// <summary>True when the row's folder is in WSL: a WSL instance, or a WSL window of a Windows editor.</summary>
    [JsonIgnore] public bool InWsl => Location is { } location ? location == "wsl" : Side == "wsl";
}

/// <summary>A source a scanner could not read (session records, review marks, the queue's records), and why.</summary>
public sealed record SourceProblemDoc
{
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("why")] public required string Why { get; init; }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(InstancesDoc))]
public sealed partial class InstanceJson : JsonSerializerContext;

/// <summary>
/// One folder of a distro's mount table that decides what a guest path is (D03 T02 §6): a drive's folder mounted there
/// (<see cref="Windows"/> its Windows folder, the drive's root or, for a bind of a subfolder, that subfolder), or a guest
/// file system mounted inside a drive's folder (<see cref="Windows"/> null), which hides the drive there.
/// </summary>
public sealed record DriveMount
{
    [JsonPropertyName("mount")] public required string Mount { get; init; }

    [JsonPropertyName("windows")] public required string? Windows { get; init; }
}

/// <summary>
/// A distro's drive mounts as its logger read them (D03 T02 §6): the one mapping from a guest path to Windows, used for
/// grouping and opening alike. A path is a Windows folder only when the longest mount it is at or under (compared exactly,
/// as Linux compares paths) is a drive's; every other path is the distro's own.
/// </summary>
public sealed record DriveMounts(IReadOnlyList<DriveMount> Drives)
{
    /// <summary>A mount a logger writes: a Linux folder of at most PATH_MAX (4,096) characters (any character but NUL, as Linux allows: a tab is a real folder name's, round 5), and a Windows folder (<c>C:\</c>, <c>C:\src</c>) or none.</summary>
    public static bool IsValid(DriveMount d) =>
        d.Mount is { Length: >= 1 and <= 4096 } m && m[0] == '/' && !m.Contains((char)0)
        && (d.Windows is null || (d.Windows.Length is >= 3 and <= 4096 && char.IsAsciiLetterUpper(d.Windows[0]) && d.Windows[1] == ':' && d.Windows[2] == '\\' && !d.Windows.Any(char.IsControl)));

    /// <summary>The Windows folder of a guest path (<c>C:\app</c>), or null when no drive's mount holds it.</summary>
    public string? WindowsPath(string linuxPath)
    {
        ArgumentNullException.ThrowIfNull(linuxPath);
        DriveMount? best = null;
        var bestLength = -1;
        foreach (var d in Drives)
        {
            var mount = d.Mount.Length > 1 ? d.Mount.TrimEnd('/') : d.Mount;
            var under = linuxPath == mount || linuxPath.StartsWith(mount == "/" ? "/" : mount + "/", StringComparison.Ordinal);
            if (under && mount.Length > bestLength) (best, bestLength) = (d, mount.Length);
        }
        if (best?.Windows is not { } windows) return null;   // none, or a guest file system over the drive there
        var tail = linuxPath.Length > bestLength ? linuxPath[bestLength..].Trim('/').Replace('/', '\\') : "";
        return tail.Length == 0 ? windows : windows.TrimEnd('\\') + "\\" + tail;
    }
}
