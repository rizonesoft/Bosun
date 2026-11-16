using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bosun.Core.Memory;

/// <summary>
/// Typed access to the core's memory documents (D03 T01 §1, after Steward's readers): <c>bosun status --json</c>,
/// <c>bosun report --json --days N</c>, and each side's live snapshot. Every answer is a <see cref="Reading{T}"/>: the
/// document, or why there is none. A page deals only in values or a reason; a cancelled call throws to its caller,
/// which has stopped asking.
/// </summary>
public sealed class MemoryClient
{
    /// <summary>Each side's snapshot older than this is stale: three of the logger's 30 s intervals.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(90);

    /// <summary>The most of a snapshot file read: a sample is a few kilobytes.</summary>
    public const int MaxSnapshotBytes = 1024 * 1024;

    /// <summary>The most of status kept: a few kilobytes for a busy queue.</summary>
    public const int MaxStatusBytes = 1024 * 1024;

    /// <summary>The most of a report kept: 14 days of a point a minute is about 3 MB.</summary>
    public const int MaxReportBytes = 16 * 1024 * 1024;

    /// <summary>How long a snapshot held in its logger's replace is asked for again.</summary>
    public static readonly TimeSpan HeldFor = TimeSpan.FromSeconds(1);

    private readonly string? _bosun;
    private readonly string _notInstalled;
    private readonly string _logs;
    private readonly ProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <param name="bosun">The tool, or null when it is not there (<paramref name="notInstalled"/> says why).</param>
    /// <param name="logs">The folder of the snapshots: <c>%LOCALAPPDATA%\Bosun\logs</c>.</param>
    /// <param name="environment">Variables added for the tool's calls.</param>
    public MemoryClient(string? bosun, string notInstalled, string logs, ProcessRunner? runner = null, TimeProvider? clock = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _bosun = bosun;
        _notInstalled = notInstalled;
        _logs = logs ?? throw new ArgumentNullException(nameof(logs));
        _runner = runner ?? new ProcessRunner();
        _clock = clock ?? TimeProvider.System;
        _environment = environment;
    }

    /// <summary>The client for this user: the tool <see cref="BosunTool.Locate"/> finds, and the user's log folder.</summary>
    public static MemoryClient ForThisUser()
    {
        var bosun = BosunTool.Locate(out var why);
        var logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bosun", "logs");
        return new MemoryClient(bosun, why, logs);
    }

    /// <summary>How long <c>bosun status</c> may take: it reads a few small files.</summary>
    public TimeSpan StatusTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long <c>bosun report</c> may take: it reads every log of the days asked for.</summary>
    public TimeSpan ReportTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary><c>bosun status --json</c>.</summary>
    public Task<Reading<StatusDoc>> StatusAsync(CancellationToken cancellationToken = default) =>
        CallAsync(["status", "--json"], MemoryJson.Default.StatusDoc, MaxStatusBytes, StatusTimeout, cancellationToken);

    /// <summary><c>bosun report --json --days N</c>, for 1 to 3650 days, as the tool takes.</summary>
    public Task<Reading<ReportDoc>> ReportAsync(int days, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(days, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(days, 3650);
        return CallAsync(["report", "--json", "--days", days.ToString(CultureInfo.InvariantCulture)], MemoryJson.Default.ReportDoc, MaxReportBytes,
            ReportTimeout, cancellationToken);
    }

    private async Task<Reading<T>> CallAsync<T>(string[] arguments, JsonTypeInfo<T> type, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken) where T : class
    {
        if (_bosun is null) return Reading<T>.Fail(ReadingProblem.NotInstalled, _notInstalled);
        var request = new ProcessRequest(_bosun, arguments) { MaxOutputBytes = maxBytes, Timeout = timeout, Environment = _environment };
        var result = await _runner.RunJsonAsync(request, type, 1, cancellationToken).ConfigureAwait(false);
        var command = "bosun " + string.Join(' ', arguments);
        switch (result)
        {
            case JsonResult<T>.Ok ok:
                return Reading<T>.Of(ok.Value, _clock.GetUtcNow());
            case JsonResult<T>.Unsupported unsupported:
                return Reading<T>.Fail(ReadingProblem.Unsupported, $"{command} wrote schema version {unsupported.Found}, and this panel reads {unsupported.Supported}. Update the panel or the tool, so they match.");
            case JsonResult<T>.Invalid invalid:
                return Reading<T>.Fail(ReadingProblem.Invalid, $"{command} wrote something that is not its document: {invalid.Reason}");
            default:
                var process = result.Process;
                // The caller stopped asking (its page left): said to it as a cancellation, never as a failure.
                if (process.Outcome == ProcessOutcome.Cancelled) cancellationToken.ThrowIfCancellationRequested();
                return Reading<T>.Fail(ReadingProblem.Failed, process.Outcome switch
                {
                    ProcessOutcome.NotStarted => $"{command} could not be started: {process.Reason}",
                    ProcessOutcome.TimedOut => $"{command} did not answer within {timeout.TotalSeconds:0} s and was stopped.",
                    ProcessOutcome.Cancelled => $"{command} was stopped before it answered.",
                    _ => $"{command} ended with exit code {process.ExitCode}: {FirstWords(process.StandardError)}",
                });
        }
    }

    private static string FirstWords(string text)
    {
        var said = text.Trim();
        if (said.Length == 0) return "it said nothing.";
        return said.Length > 400 ? said[..400] + "..." : said;
    }

    /// <summary>
    /// A side's live snapshot, <c>current-windows.json</c> or <c>current-wsl.json</c>: stale when it is more than
    /// <see cref="StaleAfter"/> older than this client's clock, and then still carrying its values as the last data.
    /// </summary>
    public async Task<Reading<SnapshotDoc>> SnapshotAsync(string side, CancellationToken cancellationToken = default)
    {
        if (side is not ("windows" or "wsl")) throw new ArgumentException("a side is windows or wsl", nameof(side));
        var file = Path.Combine(_logs, $"current-{side}.json");
        var (text, problem, why) = await ReadSnapshotTextAsync(file, cancellationToken).ConfigureAwait(false);
        if (text is null) return Reading<SnapshotDoc>.Fail(problem, why);

        SnapshotDoc? doc;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file} is JSON but not an object.");
            if (!root.TryGetProperty("schema_version", out var version)) return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file} has no schema_version.");
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var found))
                return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file}'s schema_version is not a whole number.");
            if (found != 1)
                return Reading<SnapshotDoc>.Fail(ReadingProblem.Unsupported, $"{file} is of schema version {found}, and this panel reads 1. Update the panel or the logger, so they match.");
            doc = root.Deserialize(MemoryJson.Default.SnapshotDoc);
        }
        catch (JsonException e)
        {
            return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file} is not a snapshot: {e.Message}");
        }
        if (doc is null) return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file} reads as nothing.");
        if (!DateTimeOffset.TryParseExact(doc.At, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            return Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, $"{file}'s time '{doc.At}' is not a UTC time.");

        var now = _clock.GetUtcNow();
        var age = now > at ? now - at : TimeSpan.Zero; // a clock behind the snapshot is not one that makes it stale
        if (age > StaleAfter)
            return Reading<SnapshotDoc>.Stale(doc, now, age, $"The {(side == "wsl" ? "WSL" : "Windows")} logger last wrote {Words(age)} ago.");
        return Reading<SnapshotDoc>.Of(doc, now, age);
    }

    /// <summary>
    /// A snapshot's text, read as a reader of a file replaced whole must read it: shared for delete (so the logger's
    /// replace is never blocked by this read), asked again for up to <see cref="HeldFor"/> while the file is held in
    /// that replace, and bounded before anything is read.
    /// </summary>
    private async Task<(string? Text, ReadingProblem Problem, string Why)> ReadSnapshotTextAsync(string file, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + HeldFor;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = OpenShared(file);
                if (stream.Length > MaxSnapshotBytes)
                    return (null, ReadingProblem.Invalid, $"{file} is {stream.Length:N0} bytes, more than the {MaxSnapshotBytes:N0} a snapshot may be, so it was not read.");
                var bytes = new byte[stream.Length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                return (Encoding.UTF8.GetString(bytes), default, "");
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, ReadingProblem.Missing, $"{file} is not there: its logger has not written it.");
            }
            catch (UnauthorizedAccessException e)
            {
                return (null, ReadingProblem.Unreadable, $"{file} can't be read: {e.Message}");
            }
            catch (IOException e) when (e is not EndOfStreamException)
            {
                // Held in the moment of its logger's replace, or by another reader that shares nothing: asked again.
                if (DateTime.UtcNow >= until) return (null, ReadingProblem.Unreadable, $"{file} was held by another program for more than a second: {e.Message}");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                // Shortened while it was read: read again, whole.
                if (DateTime.UtcNow >= until) return (null, ReadingProblem.Unreadable, $"{file} kept changing while it was read.");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Opens a snapshot as this client reads it: shared for reading, writing and delete, so its logger can replace it by
    /// a rename while it is open, and this reader goes on reading the old one.
    /// </summary>
    internal static FileStream OpenShared(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>An age in words: "40 seconds", "1 minute", "12 minutes", "3 hours", "2 days".</summary>
    public static string Words(TimeSpan age)
    {
        static string Count(long n, string unit) => n == 1 ? $"1 {unit}" : string.Create(CultureInfo.InvariantCulture, $"{n} {unit}s");
        if (age < TimeSpan.FromMinutes(1)) return Count((long)age.TotalSeconds, "second");
        if (age < TimeSpan.FromHours(1)) return Count((long)age.TotalMinutes, "minute");
        if (age < TimeSpan.FromDays(1)) return Count((long)age.TotalHours, "hour");
        return Count((long)age.TotalDays, "day");
    }
}
