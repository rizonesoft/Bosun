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
public sealed record InstanceRow(InstanceDoc Instance, string? Distro);

/// <summary>
/// The instances of one repo, from every side: <see cref="Repo"/> is the repo's path as its side writes it (a Windows
/// path for a WSL repo under <c>/mnt/&lt;drive&gt;/</c>, since that is the same folder), null for instances outside any
/// checkout; <see cref="Distro"/> names the distro whose own file system holds it.
/// </summary>
public sealed record RepoGroup(string? Repo, string? Distro, IReadOnlyList<InstanceRow> Rows);

/// <summary>
/// Everything running, merged by repo (D03 T02 §1): the groups, what each side answered (a side that failed keeps the
/// others' rows and says why), and a notice when WSL was not asked.
/// </summary>
public sealed record InstancesView(IReadOnlyList<RepoGroup> Groups, IReadOnlyList<SideReading> Sides, string? WslNotice);

/// <summary>
/// Reads every agent, editor and job (D03 T02 §1): <c>bosun instances --json</c> on Windows, and in each WSL distro that
/// is running, <c>wsl.exe -d &lt;distro&gt; -- bosun instances --json</c>. A distro that isn't running is never started
/// to be asked: <c>wsl.exe --list --running --quiet</c> says which run, and starts none.
/// </summary>
public sealed partial class InstanceClient
{
    /// <summary>The most of a document kept: a few hundred bytes an instance.</summary>
    public const int MaxDocumentBytes = 4 * 1024 * 1024;

    private readonly string? _bosun;
    private readonly string _notInstalled;
    private readonly string? _wsl;
    private readonly ProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string?>? _environment;

    /// <param name="bosun">The tool, or null when it is not there (<paramref name="notInstalled"/> says why).</param>
    /// <param name="wsl">wsl.exe, or null when WSL is not installed.</param>
    /// <param name="environment">Variables added for every call.</param>
    public InstanceClient(string? bosun, string notInstalled, string? wsl, ProcessRunner? runner = null, TimeProvider? clock = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        _bosun = bosun;
        _notInstalled = notInstalled;
        _wsl = wsl;
        _runner = runner ?? new ProcessRunner();
        _clock = clock ?? TimeProvider.System;
        _environment = environment;
    }

    /// <summary>The client for this user: the tool <see cref="BosunTool.Locate"/> finds, and Windows' own wsl.exe.</summary>
    public static InstanceClient ForThisUser()
    {
        var bosun = BosunTool.Locate(out var why);
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        return new InstanceClient(bosun, why, File.Exists(wsl) ? wsl : null);
    }

    /// <summary>How long one side may take: it lists processes and reads a few small files.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<InstancesView> ReadAsync(CancellationToken cancellationToken = default)
    {
        var windows = WindowsAsync(cancellationToken);
        var (distros, notice) = await RunningDistrosAsync(cancellationToken).ConfigureAwait(false);
        var guests = await Task.WhenAll(distros.Select(d => GuestAsync(d, cancellationToken))).ConfigureAwait(false);
        var sides = new List<SideReading> { await windows.ConfigureAwait(false) };
        sides.AddRange(guests);
        return new InstancesView(Merge(sides), sides, notice);
    }

    private async Task<SideReading> WindowsAsync(CancellationToken cancellationToken)
    {
        if (_bosun is null) return new SideReading("windows", null, Reading<InstancesDoc>.Fail(ReadingProblem.NotInstalled, _notInstalled));
        var reading = await CallAsync(_bosun, ["instances", "--json"], "bosun instances --json", "windows", cancellationToken).ConfigureAwait(false);
        return new SideReading("windows", null, reading);
    }

    private async Task<SideReading> GuestAsync(string distro, CancellationToken cancellationToken)
    {
        var reading = await CallAsync(_wsl!, ["-d", distro, "--", "bosun", "instances", "--json"], $"bosun instances --json in WSL ({distro})", "wsl", cancellationToken)
            .ConfigureAwait(false);
        return new SideReading("wsl", distro, reading);
    }

    /// <summary>The distros running now, or none with the notice a page shows beside the Windows rows.</summary>
    private async Task<(IReadOnlyList<string> Distros, string? Notice)> RunningDistrosAsync(CancellationToken cancellationToken)
    {
        if (_wsl is null) return ([], "WSL is not installed, so only Windows is listed.");
        var environment = new Dictionary<string, string?>(_environment ?? new Dictionary<string, string?>()) { ["WSL_UTF8"] = "1" };
        var request = new ProcessRequest(_wsl, ["--list", "--running", "--quiet"]) { Timeout = Timeout, MaxOutputBytes = 64 * 1024, Environment = environment };
        var process = await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (process.Outcome == ProcessOutcome.Cancelled) cancellationToken.ThrowIfCancellationRequested();
        // wsl.exe writes UTF-16 unless WSL_UTF8 is set, and an older one ignores it: the NULs that leaves are dropped.
        var names = process.StandardOutput.Replace("\0", "", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => DistroName().IsMatch(name)).ToList();
        if (process.Outcome != ProcessOutcome.Exited)
            return ([], $"WSL could not be asked which distros run ({process.Reason ?? process.Outcome.ToString()}), so only Windows is listed.");
        if (process.ExitCode != 0 || names.Count == 0)
        {
            var said = (process.StandardOutput + " " + process.StandardError).Replace("\0", "", StringComparison.Ordinal).Trim();
            return ([], said.Length == 0 || process.ExitCode == 0
                ? "No WSL distro is running, so only Windows is listed. None is started just to be asked."
                : $"No WSL distro is listed as running ({FirstWords(said)}), so only Windows is listed.");
        }
        return (names, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex DistroName();

    private async Task<Reading<InstancesDoc>> CallAsync(string program, string[] arguments, string command, string side, CancellationToken cancellationToken)
    {
        var request = new ProcessRequest(program, arguments) { MaxOutputBytes = MaxDocumentBytes, Timeout = Timeout, Environment = _environment };
        var result = await _runner.RunJsonAsync(request, InstanceJson.Default.InstancesDoc, 1, cancellationToken).ConfigureAwait(false);
        switch (result)
        {
            case JsonResult<InstancesDoc>.Ok ok:
                // The side first: a document from the wrong side is wrong as a whole, whatever its rows say.
                if (ok.Value.Side != side) return Reading<InstancesDoc>.Fail(ReadingProblem.Invalid, $"{command} wrote the {ok.Value.Side} document, not the {side} one");
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
    public static IReadOnlyList<RepoGroup> Merge(IReadOnlyList<SideReading> sides)
    {
        var rows = new List<InstanceRow>();
        foreach (var side in sides)
            if (side.Reading.Value is { } doc)
                rows.AddRange(doc.Instances.Select(i => new InstanceRow(i, side.Side == "wsl" ? side.Distro ?? doc.Distro : null)));

        // Pair each WSL holder in the Windows document with the distro's job whose pid is its guest_pid. With several
        // distros running, the first that carries it is taken (a pid is unique within a distro only).
        var halves = rows.Where(r => r.Instance is { Side: "wsl", Kind: "job" } && r.Distro is not null).ToList();
        var holderOf = new Dictionary<InstanceRow, InstanceRow>(ReferenceEqualityComparer.Instance);
        foreach (var holder in rows.Where(r => r is { Distro: null, Instance: { Side: "wsl", Kind: "job", GuestPid: not null } }))
            if (halves.FirstOrDefault(h => h.Instance.Pid == holder.Instance.GuestPid && !holderOf.ContainsKey(h)) is { } half)
                holderOf[half] = holder;
        var paired = new HashSet<InstanceRow>(holderOf.Values, ReferenceEqualityComparer.Instance);
        var merged = new List<InstanceRow>();
        foreach (var row in rows)
        {
            if (paired.Contains(row)) continue; // shown as its WSL half, joined
            if (!holderOf.TryGetValue(row, out var holder))
            {
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
                    StartedAt = holder.Instance.StartedAt ?? row.Instance.StartedAt,
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
        if (row.Instance.Side == "windows") return (repo, null);
        if (MountedDrive().Match(repo) is { Success: true } mounted)
            return ($"{char.ToUpperInvariant(mounted.Groups[1].Value[0])}:/{mounted.Groups[2].Value}".TrimEnd('/'), null);
        return (repo, row.Distro);
    }

    /// <summary>The grouping key: a Windows folder compared as Windows compares them, a guest folder per distro, exactly.</summary>
    private static string Key(InstanceRow row)
    {
        var (repo, distro) = Place(row);
        if (repo is null) return "none";
        return distro is null ? "win:" + repo.Replace('\\', '/').TrimEnd('/').ToUpperInvariant() : $"wsl:{distro}:{repo}";
    }

    [GeneratedRegex(@"^/mnt/([A-Za-z])(?:/(.*))?$")]
    private static partial Regex MountedDrive();
}
