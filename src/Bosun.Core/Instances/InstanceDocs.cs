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
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "processes", "sessions", "review-marks", "queue-records", "editor-windows" };

    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("distro")] public required string? Distro { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("instances")] public required IReadOnlyList<InstanceDoc> Instances { get; init; }
    [JsonPropertyName("unreadable")] public required long Unreadable { get; init; }
    [JsonPropertyName("problems")] public required IReadOnlyList<SourceProblemDoc> Problems { get; init; }

    /// <summary>A WSL distro's name: letters, digits, dot, underscore and hyphen, at most 64.</summary>
    private static readonly System.Text.RegularExpressions.Regex DistroName = new("^[A-Za-z0-9._-]{1,64}$");

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
