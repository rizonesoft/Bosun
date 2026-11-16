using System.Text.Json.Serialization;

namespace Bosun.Core.Memory;

// The documents the core writes for the panel (D03 T01 §1), each read exactly as its C++ writer writes it
// (src/core.hpp: status_json, snapshot_json, report_json; wsl/bosun_log.py: snapshot_json). Every field the writer
// always writes is required, a null where it writes a null: a document missing one is refused, never read with a
// default standing for a value. A field a later writer adds is passed over.

/// <summary>
/// A document checks what the serializer can't: a null inside a list, which nullable annotations do not cover for
/// elements (round 1 of D03 T01 §1: <c>"repos":[null]</c> read as a document whose first repo threw on use).
/// </summary>
public interface ICheckedDoc
{
    /// <summary>What is wrong with the document, or null when nothing is.</summary>
    string? Fault();
}

internal static class Lists
{
    /// <summary>The first list of <paramref name="lists"/> holding a null, named, or null when none does.</summary>
    public static string? NullIn(params (string Name, System.Collections.IEnumerable? List)[] lists)
    {
        foreach (var (name, list) in lists)
        {
            if (list is null) return $"{name} is missing";
            var index = 0;
            foreach (var item in list)
            {
                if (item is null) return $"{name}[{index}] is null";
                index++;
            }
        }
        return null;
    }
}

/// <summary><c>bosun status --json</c>: the build slots, who holds and waits for them, and memory now.</summary>
public sealed record StatusDoc : ICheckedDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("ns")] public required string Ns { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("slots")] public required IReadOnlyList<SlotDoc> Slots { get; init; }
    [JsonPropertyName("waiters")] public required IReadOnlyList<QueueRecordDoc> Waiters { get; init; }
    [JsonPropertyName("memory")] public required StatusMemoryDoc Memory { get; init; }
    [JsonPropertyName("guest")] public required GuestDoc? Guest { get; init; }
    [JsonPropertyName("logger")] public required LoggerDoc Logger { get; init; }
    [JsonPropertyName("records")] public required RecordsDoc Records { get; init; }

    public string? Fault() => Lists.NullIn(("slots", Slots), ("waiters", Waiters));
}

/// <summary>One build slot: whether its lock is held (null when it couldn't be told), and the record of its holder.</summary>
public sealed record SlotDoc
{
    [JsonPropertyName("index")] public required int Index { get; init; }
    [JsonPropertyName("busy")] public required bool? Busy { get; init; }
    [JsonPropertyName("holder")] public required QueueRecordDoc? Holder { get; init; }
}

/// <summary>A holder's or a waiter's record (dm::record_fields), with what status adds: its state and its times.</summary>
public sealed record QueueRecordDoc
{
    [JsonPropertyName("kind")] public required string Kind { get; init; }
    [JsonPropertyName("pid")] public required long Pid { get; init; }
    [JsonPropertyName("process_start")] public required long ProcessStart { get; init; }
    [JsonPropertyName("started_at")] public required string StartedAt { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonPropertyName("repo")] public required string? Repo { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("slot")] public int? Slot { get; init; }
    [JsonPropertyName("session")] public required long Session { get; init; }
    [JsonPropertyName("ns")] public required string Ns { get; init; }
    [JsonPropertyName("acquired_at")] public string? AcquiredAt { get; init; }
    [JsonPropertyName("command_started_at")] public string? CommandStartedAt { get; init; }
    [JsonPropertyName("min_guest_mb")] public long? MinGuestMb { get; init; }
    [JsonPropertyName("waiting_for")] public string? WaitingFor { get; init; }
    [JsonPropertyName("min_avail_mb")] public long? MinAvailMb { get; init; }
    [JsonPropertyName("max_wait_s")] public long? MaxWaitS { get; init; }
    [JsonPropertyName("state")] public required string State { get; init; }
    [JsonPropertyName("waited_s")] public required long? WaitedS { get; init; }
    [JsonPropertyName("running_s")] public long? RunningS { get; init; }
    [JsonPropertyName("phase")] public string? Phase { get; init; }
}

public sealed record StatusMemoryDoc
{
    [JsonPropertyName("available_mb")] public required long? AvailableMb { get; init; }
    [JsonPropertyName("total_mb")] public required long? TotalMb { get; init; }
    [JsonPropertyName("min_avail_mb")] public required long MinAvailMb { get; init; }
}

/// <summary>The WSL guest's memory as the last WSL job saw it; absent when no guest figure is known.</summary>
public sealed record GuestDoc
{
    [JsonPropertyName("available_mb")] public required long AvailableMb { get; init; }
    [JsonPropertyName("total_mb")] public required long TotalMb { get; init; }
    [JsonPropertyName("min_guest_mb")] public required long MinGuestMb { get; init; }
}

/// <summary>Whether each side's logger runs, in words; the WSL side null when it can't be told.</summary>
public sealed record LoggerDoc
{
    [JsonPropertyName("windows")] public required string Windows { get; init; }
    [JsonPropertyName("wsl")] public required string? Wsl { get; init; }
}

public sealed record RecordsDoc
{
    [JsonPropertyName("readable")] public required bool Readable { get; init; }
    [JsonPropertyName("skipped")] public required long Skipped { get; init; }
    [JsonPropertyName("dir")] public required string Dir { get; init; }
}

/// <summary><c>current-windows.json</c> or <c>current-wsl.json</c>: one side's live sample, replaced every interval.</summary>
public sealed record SnapshotDoc : ICheckedDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("at")] public required string At { get; init; }
    [JsonPropertyName("interval_s")] public required long IntervalS { get; init; }

    /// <summary>
    /// The side's counters, null where not measured. Windows writes <c>available_mb</c>, <c>total_mb</c>,
    /// <c>commit_mb</c>, <c>commit_limit_mb</c> and <c>pages_in_per_sec</c>; WSL writes <c>available_mb</c>,
    /// <c>total_mb</c>, <c>swap_used_mb</c>, <c>psi_full_avg10_x100</c> and <c>oom_kills</c>.
    /// </summary>
    [JsonPropertyName("memory")] public required IReadOnlyDictionary<string, long?> Memory { get; init; }

    [JsonPropertyName("wsl_vm")] public required WslVmDoc? WslVm { get; init; }
    [JsonPropertyName("repos")] public required IReadOnlyList<GroupDoc> Repos { get; init; }
    [JsonPropertyName("processes")] public required IReadOnlyList<GroupDoc> Processes { get; init; }

    /// <summary>Whether this sample's history rows were written: false while history is being lost.</summary>
    [JsonPropertyName("history_written")] public required bool HistoryWritten { get; init; }

    /// <summary>A counter by name: its value, or null when it was not measured or this side does not write it.</summary>
    public long? Counter(string name) => Memory.TryGetValue(name, out var value) ? value : null;

    public string? Fault() => Lists.NullIn(("repos", Repos), ("processes", Processes));
}

/// <summary>The Windows side's view of the WSL VM process.</summary>
public sealed record WslVmDoc
{
    [JsonPropertyName("running")] public required bool Running { get; init; }
    [JsonPropertyName("processes")] public required long Processes { get; init; }
    [JsonPropertyName("private_mb")] public required long PrivateMb { get; init; }
    [JsonPropertyName("rss_mb")] public required long RssMb { get; init; }
}

/// <summary>A group of processes: a repo (<see cref="Repo"/>) or a program (<see cref="Name"/>).</summary>
public sealed record GroupDoc
{
    [JsonPropertyName("repo")] public string? Repo { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("processes")] public required long Processes { get; init; }
    [JsonPropertyName("private_mb")] public required long PrivateMb { get; init; }
    [JsonPropertyName("rss_mb")] public required long RssMb { get; init; }
}

/// <summary><c>bosun report --json --days N</c>: the logged history, its peaks, and a point a minute.</summary>
public sealed record ReportDoc : ICheckedDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("generated_at")] public required string GeneratedAt { get; init; }
    [JsonPropertyName("since")] public required string Since { get; init; }
    [JsonPropertyName("days_requested")] public required int DaysRequested { get; init; }
    [JsonPropertyName("days_covered")] public required int DaysCovered { get; init; }
    [JsonPropertyName("first_sample")] public required string? FirstSample { get; init; }
    [JsonPropertyName("last_sample")] public required string? LastSample { get; init; }
    [JsonPropertyName("low_mb")] public required long LowMb { get; init; }
    [JsonPropertyName("read")] public required ReportReadDoc Read { get; init; }
    [JsonPropertyName("windows")] public required ReportWindowsDoc Windows { get; init; }
    [JsonPropertyName("wsl")] public required ReportWslDoc Wsl { get; init; }
    [JsonPropertyName("builds")] public required ReportBuildsDoc Builds { get; init; }
    [JsonPropertyName("active_repos")] public required ReportActiveDoc ActiveRepos { get; init; }
    [JsonPropertyName("repos")] public required IReadOnlyList<RepoPeakDoc> Repos { get; init; }
    [JsonPropertyName("series")] public required IReadOnlyList<SeriesPointDoc> Series { get; init; }

    public string? Fault() => Lists.NullIn(("windows.worst_repos", Windows?.WorstRepos), ("repos", Repos), ("series", Series));
}

/// <summary>Whether every log was read: an incomplete read is history with holes, which a page says.</summary>
public sealed record ReportReadDoc
{
    [JsonPropertyName("complete")] public required bool Complete { get; init; }
    [JsonPropertyName("files")] public required long Files { get; init; }
    [JsonPropertyName("unreadable_files")] public required long UnreadableFiles { get; init; }
    [JsonPropertyName("rejected_rows")] public required long RejectedRows { get; init; }
}

/// <summary>What a report's read lost, in words: "1 file could not be read and 7 rows were rejected".</summary>
public static class ReadLoss
{
    public static string Words(ReportReadDoc read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return $"{read.UnreadableFiles} {(read.UnreadableFiles == 1 ? "file" : "files")} could not be read and "
            + $"{read.RejectedRows} {(read.RejectedRows == 1 ? "row was" : "rows were")} rejected";
    }
}

public sealed record ReportWindowsDoc
{
    [JsonPropertyName("samples")] public required long Samples { get; init; }
    [JsonPropertyName("low_samples")] public required long LowSamples { get; init; }
    [JsonPropertyName("min_available_mb")] public required long? MinAvailableMb { get; init; }
    [JsonPropertyName("worst_minute")] public required string? WorstMinute { get; init; }
    [JsonPropertyName("worst_repos")] public required IReadOnlyList<WorstRepoDoc> WorstRepos { get; init; }
    [JsonPropertyName("max_commit_mb")] public required long? MaxCommitMb { get; init; }
    [JsonPropertyName("commit_limit_mb")] public required long? CommitLimitMb { get; init; }
    [JsonPropertyName("max_pages_in_per_sec")] public required long? MaxPagesInPerSec { get; init; }

    // The decision's evidence (D03 T01 §6), counted at the installed memory now. Absent from a report of an older
    // bosun.exe, and so null: not known, never 0.
    [JsonPropertyName("low_minutes")] public long? LowMinutes { get; init; }
    [JsonPropertyName("days_with_samples")] public long? DaysWithSamples { get; init; }
    [JsonPropertyName("peak_commit_ratio")] public double? PeakCommitRatio { get; init; }
    [JsonPropertyName("total_mb")] public long? TotalMb { get; init; }
    [JsonPropertyName("capacity_since")] public string? CapacitySince { get; init; }
}

public sealed record WorstRepoDoc
{
    [JsonPropertyName("repo")] public required string Repo { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("private_mb")] public required long PrivateMb { get; init; }
}

public sealed record ReportWslDoc
{
    [JsonPropertyName("samples")] public required long Samples { get; init; }
    [JsonPropertyName("min_available_mb")] public required long? MinAvailableMb { get; init; }
    [JsonPropertyName("max_swap_mb")] public required long? MaxSwapMb { get; init; }
    [JsonPropertyName("max_psi_full_avg10_x100")] public required long? MaxPsiFullAvg10X100 { get; init; }
    [JsonPropertyName("max_oom_kills")] public required long? MaxOomKills { get; init; }

    /// <summary>Out-of-memory kills within the period (D03 T01 §6); null when none was sampled in it, or from an older bosun.exe.</summary>
    [JsonPropertyName("oom_kills_in_period")] public long? OomKillsInPeriod { get; init; }

    /// <summary>Whether the count starts from a sample before the period; false counts from the first sample in it.</summary>
    [JsonPropertyName("oom_baseline_known")] public bool? OomBaselineKnown { get; init; }
}

public sealed record ReportBuildsDoc
{
    [JsonPropertyName("jobs")] public required long Jobs { get; init; }
    [JsonPropertyName("max_overlap")] public required long MaxOverlap { get; init; }
    [JsonPropertyName("at")] public required string? At { get; init; }
}

public sealed record ReportActiveDoc
{
    [JsonPropertyName("max")] public required long Max { get; init; }
    [JsonPropertyName("minute")] public required string? Minute { get; init; }
    [JsonPropertyName("total_private_mb")] public required long TotalPrivateMb { get; init; }
}

public sealed record RepoPeakDoc
{
    [JsonPropertyName("repo")] public required string Repo { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("peak_private_mb")] public required long PeakPrivateMb { get; init; }
    [JsonPropertyName("peak_rss_mb")] public required long PeakRssMb { get; init; }
    [JsonPropertyName("peak_processes")] public required long PeakProcesses { get; init; }
}

/// <summary>One minute of history: each side's available memory (null where that side wrote no sample) and the repos.</summary>
public sealed record SeriesPointDoc
{
    [JsonPropertyName("minute")] public required string Minute { get; init; }
    [JsonPropertyName("win_available_mb")] public required long? WinAvailableMb { get; init; }
    [JsonPropertyName("wsl_available_mb")] public required long? WslAvailableMb { get; init; }
    [JsonPropertyName("repos")] public required long Repos { get; init; }
    [JsonPropertyName("repo_private_mb")] public required long RepoPrivateMb { get; init; }
}

/// <summary>The serializer for the documents, made at build time: no reflection, and a null where a value is required refused.</summary>
[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(StatusDoc))]
[JsonSerializable(typeof(SnapshotDoc))]
[JsonSerializable(typeof(ReportDoc))]
public sealed partial class MemoryJson : JsonSerializerContext;
