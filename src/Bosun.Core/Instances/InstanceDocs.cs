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
    public static readonly IReadOnlySet<string> Sources = new HashSet<string> { "sessions", "review-marks", "queue-records" };

    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("distro")] public required string? Distro { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("instances")] public required IReadOnlyList<InstanceDoc> Instances { get; init; }
    [JsonPropertyName("unreadable")] public required long Unreadable { get; init; }
    [JsonPropertyName("problems")] public required IReadOnlyList<SourceProblemDoc> Problems { get; init; }

    public string? Fault()
    {
        if (Lists.NullIn(("instances", Instances), ("problems", Problems)) is { } nulls) return nulls;
        if (Side is not ("windows" or "wsl")) return $"its side is '{Side}', not windows or wsl";
        if (Unreadable < 0) return "its unreadable count is negative";
        foreach (var i in Instances)
        {
            if (!Kinds.Contains(i.Kind)) return $"instance {i.Pid} is of kind '{i.Kind}', which no scanner writes";
            if (!States.Contains(i.State)) return $"instance {i.Pid}'s state is '{i.State}', which no scanner writes";
            if (i.Side is not ("windows" or "wsl")) return $"instance {i.Pid}'s side is '{i.Side}'";
            if (i.Kind != "job" && i.Side != Side) return $"instance {i.Pid} is a {i.Side} {i.Kind} in the {Side} document";
            if (i.Pid <= 0) return $"an instance has the pid {i.Pid}";
            if (i.PrivateMb < 0) return $"instance {i.Pid}'s memory is negative";
            if (i.GuestDistro is { } g && !System.Text.RegularExpressions.Regex.IsMatch(g, "^[A-Za-z0-9._-]{1,64}$")) return $"instance {i.Pid} names the distro '{g}'";
        }
        foreach (var p in Problems)
            if (!Sources.Contains(p.Source)) return $"a problem names the source '{p.Source}', which no scanner reads";
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
