using System.Text.Json;
using System.Text.RegularExpressions;
using Bosun.Core.Memory;

namespace Bosun.Core.Instances;

/// <summary>What one side, or one WSL distro, answered: its document, or why there is none.</summary>
public sealed record SideReading(string Side, string? Distro, Reading<InstancesDoc> Reading)
{
    /// <summary>"Windows" or "WSL (Ubuntu)": the side's name in a page's words.</summary>
    public string Name => Distro is null ? (Side == "wsl" ? "WSL" : "Windows") : $"WSL ({Distro})";
}

/// <summary>One instance as the Instances page shows it: its side's row, with the distro it runs in (null on Windows).</summary>
/// <param name="Mounts">The Windows drives the row's distro has mounted (D03 T02 §6), from that distro's own report: null
/// when no report of it gave them, so a guest path is never taken for a drive's folder on a guess.</param>
public sealed record InstanceRow(InstanceDoc Instance, string? Distro, DriveMounts? Mounts = null);

/// <summary>
/// The instances of one repo, from every side: <see cref="Repo"/> is the repo's path as its side writes it (a Windows
/// path for a WSL repo under <c>/mnt/&lt;drive&gt;/</c>, since that is the same folder), null for instances outside any
/// checkout; <see cref="Distro"/> names the distro whose own file system holds it.
/// </summary>
public sealed record RepoGroup(string? Repo, string? Distro, IReadOnlyList<InstanceRow> Rows);

/// <summary>
/// Everything running, merged by repo (D03 T02 §1): the groups, what each side answered (a side that failed keeps the
/// others' rows and says why, and a stale distro its last word), and a notice when no distro has reported.
/// </summary>
public sealed record InstancesView(IReadOnlyList<RepoGroup> Groups, IReadOnlyList<SideReading> Sides, string? WslNotice)
{
    /// <summary>
    /// The groups again, with each stale distro's last rows kept (D03 T02 §2): for a page that marks them as old.
    /// <see cref="Groups"/> never lists them, so nothing else reads a stopped distro's rows as running.
    /// </summary>
    public IReadOnlyList<RepoGroup> Merged(bool keepStale) => keepStale ? InstanceClient.Merge(Sides, keepStale: true) : Groups;
}

/// <summary>What the Instances page reads everything running through: <see cref="InstanceClient"/>, or a test's fake.</summary>
public interface IInstanceSource
{
    Task<InstancesView> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads every agent, editor and job (D03 T02 §1): <c>bosun instances --json</c> on Windows, and each WSL distro's own
/// report, <c>instances-wsl-&lt;distro&gt;.json</c>, which its logger writes into the Windows log folder every sample.
/// WSL is never asked through wsl.exe: that would start a distro the person had stopped (round 1). A report older than
/// <see cref="StaleAfter"/> is that distro's last word, said, and its rows are not listed as running.
/// </summary>
public sealed partial class InstanceClient : IInstanceSource
{
    /// <summary>The most of a document kept: a few hundred bytes an instance.</summary>
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    /// <summary>A distro's report older than this is stale: three of its logger's 30 s intervals.</summary>
    public static readonly TimeSpan StaleAfter = MemoryClient.StaleAfter;

    private readonly string? _bosun;
    private readonly string _notInstalled;
    private readonly string _logs;
    private readonly ProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <param name="bosun">The tool, or null when it is not there (<paramref name="notInstalled"/> says why).</param>
    /// <param name="logs">The log folder the WSL loggers write their reports to: <c>%LOCALAPPDATA%\Bosun\logs</c>.</param>
    /// <param name="environment">Variables added for the tool's call.</param>
    public InstanceClient(string? bosun, string notInstalled, string logs, ProcessRunner? runner = null, TimeProvider? clock = null,
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
    public static InstanceClient ForThisUser(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var bosun = BosunTool.Locate(out var why, environment);
        // The folder the loggers write, as the variable says it, like bosun.exe and MemoryClient (the known folder alone
        // read another folder than the logger whenever LOCALAPPDATA was changed).
        var logs = MemoryClient.LogFolder(environment);
        return new InstanceClient(bosun, why, logs);
    }

    /// <summary>The folder each distro's report is read from.</summary>
    public string Logs => _logs;

    /// <summary>How long the Windows scan may take: it lists processes and reads a few small files.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<InstancesView> ReadAsync(CancellationToken cancellationToken = default)
    {
        var windows = WindowsAsync(cancellationToken);
        var (guests, notice) = await GuestsAsync(cancellationToken).ConfigureAwait(false);
        var sides = new List<SideReading> { await windows.ConfigureAwait(false) };
        sides.AddRange(guests);
        return new InstancesView(Merge(sides), sides, notice);
    }

    private async Task<SideReading> WindowsAsync(CancellationToken cancellationToken)
    {
        if (_bosun is null) return new SideReading("windows", null, Reading<InstancesDoc>.Fail(ReadingProblem.NotInstalled, _notInstalled));
        return new SideReading("windows", null, await CallAsync(_bosun, ["instances", "--json"], "bosun instances --json", cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Each distro's report, by its file; none, with the notice a page shows beside the Windows rows.</summary>
    private async Task<(IReadOnlyList<SideReading> Guests, string? Notice)> GuestsAsync(CancellationToken cancellationToken)
    {
        string[] files;
        try
        {
            files = Directory.Exists(_logs) ? Directory.GetFiles(_logs, "instances-wsl-*.json") : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ([], $"The WSL reports in {_logs} can't be listed ({e.Message}), so only Windows is listed.");
        }
        if (files.Length == 0)
            return ([], $"No WSL distro has reported its instances to {_logs}, so only Windows is listed. Each distro's logger reports every 30 s once it runs (wsl/install.sh sets it up).");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        var guests = new List<SideReading>();
        foreach (var file in files) guests.Add(await GuestAsync(file, cancellationToken).ConfigureAwait(false));
        return (guests, null);
    }

    private async Task<SideReading> GuestAsync(string file, CancellationToken cancellationToken)
    {
        var named = Path.GetFileNameWithoutExtension(file)["instances-wsl-".Length..];
        var (text, problem, why) = await MemoryClient.ReadSharedTextAsync(file, MaxDocumentBytes, cancellationToken).ConfigureAwait(false);
        if (text is null) return new SideReading("wsl", named, Reading<InstancesDoc>.Fail(problem, why));
        InstancesDoc? doc;
        try
        {
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            // The kind before the number: TryGetInt64 throws on a string, a bool or null (round 4).
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema_version", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var found))
                return new SideReading("wsl", named, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file} has no schema_version."));
            if (found != 1)
                return new SideReading("wsl", named, Reading<InstancesDoc>.Fail(ReadingProblem.Unsupported,
                    $"{file} is of schema version {found}, and this panel reads 1. Update the panel or the WSL tools, so they match."));
            doc = root.Deserialize(InstanceJson.Default.InstancesDoc);
        }
        catch (JsonException e)
        {
            return new SideReading("wsl", named, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file} is not an instances report: {e.Message}"));
        }
        if (doc is null) return new SideReading("wsl", named, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file} reads as nothing."));
        var distro = doc.Distro ?? named;
        if (doc.Side != "wsl") return new SideReading("wsl", distro, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file} is the {doc.Side} document, not a WSL one."));
        if (doc.Fault() is { } fault) return new SideReading("wsl", distro, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file} is not an instances report: {fault}."));
        if (!DateTimeOffset.TryParseExact(doc.At, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var at))
            return new SideReading("wsl", distro, Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{file}'s time '{doc.At}' is not a UTC time."));
        var now = _clock.GetUtcNow();
        var age = now > at ? now - at : TimeSpan.Zero;
        if (age > StaleAfter)
            return new SideReading("wsl", distro, Reading<InstancesDoc>.Stale(doc, now, age,
                $"WSL ({distro}) last reported {MemoryClient.Words(age)} ago: it is stopped, or its logger is."));
        return new SideReading("wsl", distro, Reading<InstancesDoc>.Of(doc, now, age));
    }

    private async Task<Reading<InstancesDoc>> CallAsync(string program, string[] arguments, string command, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(program, arguments) { MaxOutputBytes = MaxDocumentBytes, Timeout = Timeout, Environment = _environment };
        var result = await _runner.RunJsonAsync(request, InstanceJson.Default.InstancesDoc, 1, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case JsonResult<InstancesDoc>.Ok ok:
                // The side first: a document from the wrong side is wrong as a whole, whatever its rows say.
                if (ok.Value.Side != "windows") return Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{command} wrote the {ok.Value.Side} document, not the windows one");
                if (ok.Value.Fault() is { } fault) return Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{command} wrote something that is not its document: {fault}");
                return Reading<InstancesDoc>.Of(ok.Value, _clock.GetUtcNow());
            case JsonResult<InstancesDoc>.Unsupported unsupported:
                return Reading<InstancesDoc>.Fail(ReadingProblem.Unsupported,
                    $"{command} wrote schema version {unsupported.Found}, and this panel reads {unsupported.Supported}. Update the panel or the tool, so they match.");
            case JsonResult<InstancesDoc>.Invalid invalid:
                return Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{command} wrote something that is not its document: {invalid.Reason}");
            default:
                var process = result.Process;
                if (process.Outcome == ProcessOutcome.Cancelled) cancellationToken.ThrowIfCancellationRequested();
                return Reading<InstancesDoc>.Fail(ReadingProblem.Failed, process.Outcome switch
                {
                    ProcessOutcome.NotStarted => $"{command} could not be started: {process.Reason}",
                    ProcessOutcome.TimedOut => $"{command} did not answer within {Timeout.TotalSeconds:0} s and was stopped.",
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
    /// The rows of every side that answered, grouped by repo. A WSL repo under <c>/mnt/&lt;drive&gt;/</c> is that drive's
    /// Windows folder, so it joins the Windows repo of the same path (letter case aside, as Windows reads paths); any
    /// other WSL path stays its distro's own, so two distros' same path are two repos. A WSL job's two halves join: the
    /// Windows holder (slot, state, command) and the WSL job whose pid is its <c>guest_pid</c> (memory, folder, start).
    /// </summary>
    /// <param name="keepStale">Keep a stale distro's last rows too, for a page that says they are old (D03 T02 §2).</param>
    public static IReadOnlyList<RepoGroup> Merge(IReadOnlyList<SideReading> sides, bool keepStale = false)
    {
        // Each distro's drive mounts, from its own fresh report: a stale one speaks for its own rows only, never for a
        // live Windows editor's window or holder in that distro (plan review of D03 T02 §6).
        var mountsOf = new Dictionary<string, DriveMounts>(StringComparer.OrdinalIgnoreCase);
        foreach (var side in sides)
            if (side is { Side: "wsl" } && side.Reading is { Value: { Drives: { } drives } report, IsStale: false } && (side.Distro ?? report.Distro) is { } named)
                mountsOf.TryAdd(named, new DriveMounts(drives));
        DriveMounts? MountsOf(string? distro) => distro is not null && mountsOf.TryGetValue(distro, out var m) ? m : null;

        var rows = new List<InstanceRow>();
        foreach (var side in sides)
            if (side.Reading is { Value: { } doc } reading && (keepStale || !reading.IsStale))
                rows.AddRange(doc.Instances.Select(i =>
                {
                    var distro = side.Side == "wsl" ? side.Distro ?? doc.Distro : null;
                    // A WSL row its own report's mounts; a Windows editor's WSL window, or a WSL holder Windows saw, its named
                    // distro's fresh report's; none read, none (the guest path kept).
                    var mounts = side.Side == "wsl" ? doc.Drives is { } own ? new DriveMounts(own) : null
                        : i.Location == "wsl" ? MountsOf(i.Distro)
                        : i.Side == "wsl" ? MountsOf(i.GuestDistro)
                        : null;
                    return new InstanceRow(i, distro, mounts);
                }));

        // Pair each WSL holder in the Windows document with the job of its own distro whose pid is its guest_pid: a
        // Linux pid is unique only within one distro (round 1), so a holder that names no distro is never joined.
        var halves = rows.Where(r => r.Instance is { Side: "wsl", Kind: "job" } && r.Distro is not null).ToList();
        var holderOf = new Dictionary<InstanceRow, InstanceRow>(ReferenceEqualityComparer.Instance);
        foreach (var holder in rows.Where(r => r is { Distro: null, Instance: { Side: "wsl", Kind: "job", GuestPid: not null } }))
            if (holder.Instance.GuestDistro is { } its &&
                halves.FirstOrDefault(h => h.Instance.Pid == holder.Instance.GuestPid && h.Distro == its && !holderOf.ContainsKey(h)) is { } half)
                holderOf[half] = holder;
        var paired = new HashSet<InstanceRow>(holderOf.Values, ReferenceEqualityComparer.Instance);
        var merged = new List<InstanceRow>();
        foreach (var row in rows)
        {
            if (paired.Contains(row)) continue; // shown as its WSL half, joined
            if (!holderOf.TryGetValue(row, out var holder))
            {
                // A WSL job no holder names is a shim still waiting for a slot (the Queue page's), or one whose holder
                // couldn't be read: not a running job (round 5).
                if (row.Instance is { Side: "wsl", Kind: "job" } && row.Distro is not null) continue;
                merged.Add(row);
                continue;
            }
            merged.Add(row with
            {
                Instance = row.Instance with
                {
                    Slot = holder.Instance.Slot,
                    State = holder.Instance.State,
                    Evidence = holder.Instance.Evidence,
                    Command = holder.Instance.Command ?? row.Instance.Command,
                    GuestPid = holder.Instance.GuestPid,
                    StartedAt = holder.Instance.StartedAt, // its command's start, null until it has started (round 5)
                    Repo = row.Instance.Repo ?? holder.Instance.Repo,
                },
            });
        }

        return merged
            .GroupBy(r => Key(r), StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                var (repo, distro) = Place(first);
                return new RepoGroup(repo, distro, g.OrderBy(r => r.Instance.Kind, StringComparer.Ordinal).ThenBy(r => r.Instance.Pid).ToList());
            })
            .OrderBy(g => g.Repo is null)
            .ThenBy(g => g.Repo, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Distro, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The repo a row belongs to, as a page names it, and the distro whose file system holds it.</summary>
    private static (string? Repo, string? Distro) Place(InstanceRow row)
    {
        var repo = row.Instance.Repo;
        if (repo is null) return (null, null);
        // A WSL window of a Windows editor is in its distro's repo, though its process is on Windows (D03 T02 §4).
        if (!row.Instance.InWsl) return (repo, null);
        // A drive's folder only under its distro's own mount root (D03 T02 §6): with the mounts moved or off, /mnt/c/app
        // is a guest folder like any other.
        if (row.Mounts?.WindowsPath(repo) is { } windows)
            return (windows.Replace('\\', '/').TrimEnd('/'), null);
        // A Windows editor's WSL window is in the distro it names; an unjoined WSL holder Windows saw, the one it names.
        return (repo, row.Instance.Side == "windows" ? row.Instance.Distro : row.Distro ?? row.Instance.GuestDistro);
    }

    /// <summary>The grouping key: a Windows folder compared as Windows compares them, a guest folder per distro, exactly.</summary>
    private static string Key(InstanceRow row)
    {
        var (repo, distro) = Place(row);
        if (repo is null) return "none";
        return distro is null ? "win:" + repo.Replace('\\', '/').TrimEnd('/').ToUpperInvariant() : $"wsl:{distro}:{repo}";
    }

}
