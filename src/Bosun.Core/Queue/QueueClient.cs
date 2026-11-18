using System.Globalization;
using System.Text;
using System.Text.Json;
using Bosun.Core.Memory;

namespace Bosun.Core.Queue;

/// <summary>What the Queue page reads and changes (D03 T02 §3): the queue now, its finished jobs and its limits.</summary>
public interface IQueueSource
{
    /// <summary><c>bosun status --json</c>: the slots, who holds and waits for them, and memory now.</summary>
    Task<Reading<StatusDoc>> StatusAsync(CancellationToken cancellationToken = default);

    /// <summary>The newest <paramref name="count"/> finished jobs, newest first, from the job logs.</summary>
    Task<Reading<JobHistory>> HistoryAsync(int count, CancellationToken cancellationToken = default);

    /// <summary><c>bosun config get --json</c>: each limit in force, where its value comes from, and the file's state.</summary>
    Task<Reading<ConfigDoc>> LimitsAsync(CancellationToken cancellationToken = default);

    /// <summary><c>bosun config set &lt;key&gt; &lt;value&gt; --json</c>.</summary>
    Task<Reading<LimitSave>> SetLimitAsync(string key, long value, CancellationToken cancellationToken = default);

    /// <summary><c>bosun config unset &lt;key&gt; --json</c>: the limit goes back to its variable or default.</summary>
    Task<Reading<LimitSave>> UnsetLimitAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>The finished jobs read, newest first, with what could not be read said.</summary>
/// <param name="PassedOver">Lines that were not a record (a torn last line, a hand edit), passed over.</param>
/// <param name="Unreadable">Each job log that could not be read, by name, with why.</param>
public sealed record JobHistory(IReadOnlyList<JobLineDoc> Jobs, int PassedOver, IReadOnlyList<string> Unreadable);

/// <summary>How a save of one limit ended.</summary>
public enum LimitSaveOutcome
{
    /// <summary>Saved (exit 0).</summary>
    Saved,

    /// <summary>The value was refused (exit 64): <see cref="LimitSave.Message"/> is the tool's words, for its field.</summary>
    Refused,

    /// <summary>Another save held the config (exit 75): nothing was changed.</summary>
    Busy,

    /// <summary>The file can't be read or is damaged, so it was not overwritten (exit 74).</summary>
    Damaged,
}

/// <summary>One limit's save: its outcome, the value now saved (null when unset), and the tool's words.</summary>
public sealed record LimitSave(string Key, LimitSaveOutcome Outcome, long? Saved, string Message);

public sealed class QueueClient : IQueueSource
{
    /// <summary>The most of a config answer kept: a few hundred bytes.</summary>
    public const int MaxConfigBytes = 64 * 1024;

    private readonly string? _bosun;
    private readonly string _notInstalled;
    private readonly string _logs;
    private readonly MemoryClient _status;
    private readonly ProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <param name="bosun">The tool, or null when it is not there (<paramref name="notInstalled"/> says why).</param>
    /// <param name="logs">The folder of the job logs: <c>%LOCALAPPDATA%\Bosun\logs</c>.</param>
    /// <param name="environment">Variables added for the tool's calls (a test's own <c>LOCALAPPDATA</c> and namespace).</param>
    public QueueClient(string? bosun, string notInstalled, string logs, ProcessRunner? runner = null, TimeProvider? clock = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _bosun = bosun;
        _notInstalled = notInstalled;
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _runner = runner ?? new ProcessRunner();
        _clock = clock ?? TimeProvider.System;
        _environment = environment;
        // The status call is the Memory page's own, shared rather than copied.
        _status = new MemoryClient(bosun, notInstalled, logs, _runner, _clock, environment);
    }

    /// <summary>The client for this user: the tool <see cref="BosunTool.Locate"/> finds, and the user's log folder.</summary>
    public static QueueClient ForThisUser()
    {
        var bosun = BosunTool.Locate(out var why);
        return new QueueClient(bosun, why, MemoryClient.LogFolder(Environment.GetEnvironmentVariable));
    }

    /// <summary>How long a <c>bosun config</c> call may take: a save waits up to 10 s for another one.</summary>
    public TimeSpan ConfigTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public Task<Reading<StatusDoc>> StatusAsync(CancellationToken cancellationToken = default) => _status.StatusAsync(cancellationToken);

    public async Task<Reading<ConfigDoc>> LimitsAsync(CancellationToken cancellationToken = default) =>
        (await ConfigAsync(["config", "get", "--json"], text => JsonSerializer.Deserialize(text, QueueJson.Default.ConfigDoc), get: true, cancellationToken).ConfigureAwait(false)).Reading;

    public Task<Reading<LimitSave>> SetLimitAsync(string key, long value, CancellationToken cancellationToken = default) =>
        SaveAsync(["config", "set", key, value.ToString(CultureInfo.InvariantCulture), "--json"], key, cancellationToken);

    public Task<Reading<LimitSave>> UnsetLimitAsync(string key, CancellationToken cancellationToken = default) =>
        SaveAsync(["config", "unset", key, "--json"], key, cancellationToken);

    private async Task<Reading<LimitSave>> SaveAsync(string[] arguments, string key, CancellationToken cancellationToken)
    {
        var (reading, exit) = await ConfigAsync(arguments, text => JsonSerializer.Deserialize(text, QueueJson.Default.ConfigReplyDoc), get: false, cancellationToken).ConfigureAwait(false);
        if (reading.Value is not { } reply) return Reading<LimitSave>.Fail(reading.Problem ?? ReadingProblem.Failed, reading.Why ?? "the save gave no answer");
        var outcome = reply.Ok ? LimitSaveOutcome.Saved : exit switch
        {
            75 => LimitSaveOutcome.Busy,
            74 => LimitSaveOutcome.Damaged,
            _ => LimitSaveOutcome.Refused,
        };
        return Reading<LimitSave>.Of(new LimitSave(reply.Key.Length > 0 ? reply.Key : key, outcome, reply.Ok ? reply.Saved : null, reply.Ok ? "saved" : reply.Error ?? "refused, with no reason given"), _clock.GetUtcNow());
    }

    /// <summary>
    /// A <c>bosun config</c> call: its one JSON answer on stdout whatever its exit (a refusal, exit 64, still answers),
    /// read as a document. <paramref name="get"/> calls take only exit 0; a save takes 0, 64, 74 and 75.
    /// </summary>
    private async Task<(Reading<T> Reading, int Exit)> ConfigAsync<T>(string[] arguments, Func<string, T?> parse, bool get, CancellationToken cancellationToken) where T : class
    {
        var command = "bosun " + string.Join(' ', arguments);
        if (_bosun is null) return (Reading<T>.Fail(ReadingProblem.NotInstalled, _notInstalled), -1);
        var request = new ProcessRequest(_bosun, arguments) { MaxOutputBytes = MaxConfigBytes, Timeout = ConfigTimeout, Environment = _environment };
        var process = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (process.Outcome == ProcessOutcome.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        if (process.Outcome != ProcessOutcome.Exited)
            return (Reading<T>.Fail(ReadingProblem.Failed, process.Outcome switch
            {
                ProcessOutcome.NotStarted => $"{command} could not be started: {process.Reason}",
                ProcessOutcome.TimedOut => $"{command} did not answer within {ConfigTimeout.TotalSeconds:0} s and was stopped.",
                _ => $"{command} was stopped before it answered.",
            }), -1);
        var exit = process.ExitCode ?? -1;
        var answered = get ? exit == 0 : exit is 0 or 64 or 74 or 75;
        if (!answered) return (Reading<T>.Fail(ReadingProblem.Failed, $"{command} ended with exit code {exit}: {FirstWords(process.StandardError)}"), exit);
        T? doc;
        try { doc = parse(process.StandardOutput.Trim()); }
        catch (JsonException e) { return (Reading<T>.Fail(ReadingProblem.Invalid, $"{command} wrote something that is not its answer: {e.Message}"), exit); }
        if (doc is null) return (Reading<T>.Fail(ReadingProblem.Invalid, $"{command} wrote nothing that is its answer."), exit);
        var version = doc switch { ConfigDoc c => c.SchemaVersion, ConfigReplyDoc r => r.SchemaVersion, _ => 1 };
        if (version != 1)
            return (Reading<T>.Fail(ReadingProblem.Unsupported, $"{command} wrote schema version {version}, and this panel reads 1. Update the panel or the tool, so they match."), exit);
        return (Reading<T>.Of(doc, _clock.GetUtcNow()), exit);
    }

    /// <summary>
    /// The job logs, newest file first (each is named for its UTC day), read until <paramref name="count"/> valid records
    /// are found; the records ordered newest first by their millisecond end where a line has one, else by its end in
    /// seconds. A file is read while <c>bosun run</c> may append to it: only its complete lines are taken, and a line
    /// that is not a record is passed over and counted. A missing folder is an empty history; a file that can't be read
    /// is said by name, the others kept.
    /// </summary>
    public Task<Reading<JobHistory>> HistoryAsync(int count, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        return Task.Run(() => History(count, cancellationToken), cancellationToken);
    }

    private Reading<JobHistory> History(int count, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_logs)) return Reading<JobHistory>.Of(new JobHistory([], 0, []), _clock.GetUtcNow());
        string[] files;
        try { files = Directory.GetFiles(_logs, "jobs-*.jsonl"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Reading<JobHistory>.Fail(ReadingProblem.Unreadable, $"The job logs in {_logs} can't be listed: {e.Message}");
        }
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        Array.Reverse(files);
        var jobs = new List<JobLineDoc>();
        var passedOver = 0;
        var unreadable = new List<string>();
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (jobs.Count >= count) break;
            string text;
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                text = reader.ReadToEnd();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                unreadable.Add($"{Path.GetFileName(file)}: {e.Message}");
                continue;
            }
            // Only whole lines: a line still being appended has no newline yet, and is read next time.
            var end = text.LastIndexOf('\n');
            var whole = end < 0 ? "" : text[..end];
            foreach (var line in whole.Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize(line, QueueJson.Default.JobLineDoc) is { } job) jobs.Add(job);
                    else passedOver++;
                }
                catch (JsonException) { passedOver++; }
            }
        }
        var newest = jobs.OrderByDescending(EndOf).Take(count).ToList();
        return Reading<JobHistory>.Of(new JobHistory(newest, passedOver, unreadable), _clock.GetUtcNow());
    }

    /// <summary>A job's end in milliseconds: its own where it has one, else its end in whole seconds.</summary>
    public static long EndOf(JobLineDoc job)
    {
        if (job.EndedMs is { } ms) return ms;
        return DateTimeOffset.TryParse(job.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at.ToUnixTimeMilliseconds() : long.MinValue;
    }

    private static string FirstWords(string text)
    {
        var said = text.Trim();
        if (said.Length == 0) return "it said nothing.";
        return said.Length > 400 ? said[..400] + "..." : said;
    }
}
