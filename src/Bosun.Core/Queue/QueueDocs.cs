using System.Text.Json.Serialization;

namespace Bosun.Core.Queue;

/// <summary>
/// <c>bosun config get --json</c> (src/app.cpp <c>cmd_config</c>): the limits file, whether it could be read and is
/// valid, what in it was refused, and each limit in force with where its value comes from (D03 T02 §3).
/// </summary>
public sealed record ConfigDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("path")] public required string Path { get; init; }
    [JsonPropertyName("exists")] public required bool Exists { get; init; }
    [JsonPropertyName("readable")] public required bool Readable { get; init; }
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("error")] public required string? Error { get; init; }
    [JsonPropertyName("refused")] public required IReadOnlyList<RefusedDoc> Refused { get; init; }
    [JsonPropertyName("limits")] public required IReadOnlyDictionary<string, LimitDoc> Limits { get; init; }
}

/// <summary>A value in the file the tool would not take, and why.</summary>
public sealed record RefusedDoc
{
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("error")] public required string Error { get; init; }
}

/// <summary>One limit: the value in force and its source (flag, variable, file or default), its range and its variable.</summary>
public sealed record LimitDoc
{
    [JsonPropertyName("value")] public required long Value { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("saved")] public required long? Saved { get; init; }
    [JsonPropertyName("min")] public required long Min { get; init; }
    [JsonPropertyName("max")] public required long Max { get; init; }
    [JsonPropertyName("default")] public required long Default { get; init; }
    [JsonPropertyName("variable")] public required string Variable { get; init; }
    [JsonPropertyName("variable_error")] public required string? VariableError { get; init; }
}

/// <summary><c>bosun config set|unset --json</c>'s one-line answer, written on every exit.</summary>
public sealed record ConfigReplyDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("ok")] public required bool Ok { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
    [JsonPropertyName("saved")] public long? Saved { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

/// <summary>
/// One finished job, a line of <c>%LOCALAPPDATA%\Bosun\logs\jobs-&lt;UTC date&gt;.jsonl</c> (src/core.hpp
/// <c>job_line_json</c>), appended by <c>bosun run</c> as it ends.
/// </summary>
public sealed record JobLineDoc
{
    [JsonPropertyName("schema_version")] public required int SchemaVersion { get; init; }
    [JsonPropertyName("started_at")] public required string StartedAt { get; init; }
    [JsonPropertyName("ended_at")] public required string EndedAt { get; init; }
    [JsonPropertyName("pid")] public required long Pid { get; init; }
    [JsonPropertyName("command")] public required string Command { get; init; }
    [JsonPropertyName("repo")] public required string? Repo { get; init; }
    [JsonPropertyName("side")] public required string Side { get; init; }
    [JsonPropertyName("ns")] public required string Ns { get; init; }
    [JsonPropertyName("slot")] public required int? Slot { get; init; }
    [JsonPropertyName("waited_s")] public required double WaitedS { get; init; }
    [JsonPropertyName("ran_s")] public required double RanS { get; init; }
    [JsonPropertyName("exit_code")] public required long? ExitCode { get; init; }
    [JsonPropertyName("timed_out_into_queue")] public required bool TimedOutIntoQueue { get; init; }
    /// <summary>Milliseconds since the epoch at the end; lines an older bosun.exe wrote have none.</summary>
    [JsonPropertyName("ended_ms")] public long? EndedMs { get; init; }
}

[JsonSourceGenerationOptions(RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(ConfigDoc))]
[JsonSerializable(typeof(ConfigReplyDoc))]
[JsonSerializable(typeof(JobLineDoc))]
public sealed partial class QueueJson : JsonSerializerContext;
