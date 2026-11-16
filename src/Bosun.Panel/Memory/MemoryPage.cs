using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Memory;

/// <summary>
/// A card's value as the Memory view draws it, large (its template is in MemoryView.xaml), beside a
/// <see cref="NotAvailable"/>, which the theme draws as "Not available" with why.
/// </summary>
public sealed record Metric(string Text)
{
    public override string ToString() => Text;
}

/// <summary>One repo on one side, as the By repo table shows it: its folder's name, its whole path for the join and the tooltip.</summary>
public sealed record RepoRow(string Repo, string Path, string Side, double PrivateGb, double? PeakGb, string Peak, long Processes)
{
    public string Private => PrivateGb.ToString("0.0", CultureInfo.InvariantCulture);
}

/// <summary>
/// The Memory page (D03 T01 §2, design/SPEC.md §3): how much room is left and who took the rest. Three cards (Windows
/// used, WSL VM, Overlapping builds) above a table by repo. The two snapshots are read every 5 s, and the last 24
/// hours' report every 30 s, each only while the page is shown (<see cref="PageBase.RefreshEvery"/>,
/// <see cref="PageBase.RefreshAlso"/>). A Windows snapshot that can't be read is the page's failed state; one gone
/// stale shows its last data with its age. A WSL snapshot or a report that can't be read makes only its own values
/// "Not available", with why; nothing unread is ever shown as 0.
/// </summary>
public sealed class MemoryPage : PageBase
{
    public const string Purpose = "Live headroom, what is using it, and the peaks behind the 64 GB decision.";

    public const string NotInHistory = "Not in the history yet";

    public const string NoRepos = "No repo has processes running now, and none ran in the last 24 hours.";

    private readonly IMemorySource _client;
    private Reading<SnapshotDoc>? _windows, _wsl;
    private Reading<ReportDoc>? _report;
    private object? _windowsUsed, _wslVm, _overlap;
    private string _windowsNote = "", _wslNote = "", _overlapNote = "", _historyNote = "", _wslAge = "", _tableNote = "", _peaksNote = "";

    public MemoryPage(IMemorySource client, RefreshScheduler scheduler, ActivityLog log) : base("Memory", Purpose)
    {
        _client = client;
        LoadingShape = SkeletonShape.Gauge;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.SortDescriptions.Add(new SortDescription(nameof(RepoRow.PrivateGb), ListSortDirection.Descending));
        RefreshEvery(scheduler, log, RefreshScheduler.Fast, ReadSnapshotsAsync);
        RefreshAlso(scheduler, "history", RefreshScheduler.Slow, ReadReportAsync);
    }

    /// <summary>Windows used: a <see cref="Metric"/> ("24.6 GB") or a <see cref="NotAvailable"/>.</summary>
    public object? WindowsUsed { get => _windowsUsed; private set => Set(ref _windowsUsed, value); }

    public string WindowsNote { get => _windowsNote; private set => Set(ref _windowsNote, value); }

    /// <summary>WSL VM: what the VM costs Windows, "Not running", or a <see cref="NotAvailable"/>.</summary>
    public object? WslVm { get => _wslVm; private set => Set(ref _wslVm, value); }

    public string WslNote { get => _wslNote; private set => Set(ref _wslNote, value); }

    /// <summary>Overlapping builds in the last 24 hours, or a <see cref="NotAvailable"/>.</summary>
    public object? Overlap { get => _overlap; private set => Set(ref _overlap, value); }

    public string OverlapNote { get => _overlapNote; private set => Set(ref _overlapNote, value); }

    /// <summary>What is wrong with the history, said beside its figures; empty when it is whole.</summary>
    public string HistoryNote { get => _historyNote; private set => Set(ref _historyNote, value); }

    /// <summary>The WSL figures' age when the WSL snapshot has gone stale; empty while it is current.</summary>
    public string WslAge { get => _wslAge; private set => Set(ref _wslAge, value); }

    /// <summary>Why the peaks can't be shown, when the report can't be read; empty otherwise.</summary>
    public string PeaksNote { get => _peaksNote; private set => Set(ref _peaksNote, value); }

    /// <summary>The table's own line when it has no rows; empty otherwise.</summary>
    public string TableNote { get => _tableNote; private set => Set(ref _tableNote, value); }

    /// <summary>The table's rows, changed in place so a chosen sort survives a refresh.</summary>
    public ObservableCollection<RepoRow> Rows { get; } = [];

    /// <summary>The rows as the table shows them: highest private memory first until another sort is chosen.</summary>
    public ICollectionView RowsView { get; }

    public static string Gb(long mb) => (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    private static double GbOf(long mb) => Math.Round(mb / 1024.0, 1);

    private async Task<Action> ReadSnapshotsAsync(CancellationToken token)
    {
        var windows = _client.SnapshotAsync("windows", token);
        var wsl = _client.SnapshotAsync("wsl", token);
        var (w, l) = (await windows, await wsl);
        // The page fails only on Windows' own: without it there is no headroom to show.
        if (w.Value is null) throw new InvalidOperationException(w.Why ?? "The Windows snapshot could not be read.");
        return () =>
        {
            (_windows, _wsl) = (w, l);
            Draw();
            if (w.IsStale) ShowLastKnown(w.Why ?? "The Windows logger has not written lately.", w.ReadAt ?? Clock.GetUtcNow());
            else ShowData();
        };
    }

    private async Task<Action> ReadReportAsync(CancellationToken token)
    {
        var report = await _client.ReportAsync(1, token);
        return () =>
        {
            _report = report;
            Draw();
        };
    }

    /// <summary>Everything on the page from the last readings.</summary>
    private void Draw()
    {
        if (_windows?.Value is not { } windows) return;
        var total = windows.Counter("total_mb");
        var available = windows.Counter("available_mb");
        WindowsUsed = total is { } t && available is { } a && t >= a && a >= 0 ? new Metric(Gb(t - a)) : new NotAvailable("The Windows logger did not measure total and available memory.");
        WindowsNote = windows.Counter("commit_mb") is { } commit ? $"Committed {Gb(commit)}" : "Committed: not measured";
        HistoryNote = windows.HistoryWritten ? "" : "History is not being written: the logger could not save its samples, so the history has a gap.";

        var vmRunning = windows.WslVm is { Running: true };
        var wsl = _wsl?.Value;
        if (!vmRunning)
        {
            WslVm = new Metric("Not running");
            WslNote = "The WSL VM is not running.";
            WslAge = "";
        }
        else
        {
            WslVm = new Metric(Gb(windows.WslVm!.PrivateMb));
            WslNote = wsl?.Counter("total_mb") is { } cap ? $"Cap {Gb(cap)}" : $"Cap: not available. {_wsl?.Why ?? "The WSL snapshot has not been read."}";
            WslAge = _wsl is { IsStale: true } ? $"The WSL figures are not current: {_wsl.Why}" : "";
        }

        var report = _report?.Value;
        if (report is not null)
        {
            Overlap = new Metric(report.Builds.MaxOverlap.ToString(CultureInfo.InvariantCulture) + " at once");
            OverlapNote = "Most in the last 24 hours" + (report.Windows.MinAvailableMb is { } low ? $"; lowest headroom {Gb(low)}" : "; lowest headroom not measured");
            PeaksNote = report.Read.Complete ? ""
                : $"The last 24 hours' history is incomplete: {report.Read.UnreadableFiles} files could not be read and {report.Read.RejectedRows} rows were rejected, so peaks may be higher.";
        }
        else if (_report is { } failed)
        {
            Overlap = new NotAvailable(failed.Why ?? "It could not be read.");
            OverlapNote = "Most in the last 24 hours";
            PeaksNote = $"Peaks not available: {failed.Why}";
        }
        else
        {
            Overlap = new NotAvailable("The last 24 hours' history is still being read.");
            PeaksNote = "";
        }

        // Rows join on side and repo: a repo on both sides is two rows with their own peaks. The report says the
        // Windows side "win" and the snapshot "windows" (src/core.hpp), so both are read as one side.
        static string SideOf(string side) => side is "win" or "windows" ? "windows" : side;
        static string Name(string path)
        {
            var trimmed = path.TrimEnd('/', '\\');
            var at = trimmed.LastIndexOfAny(['/', '\\']);
            return at >= 0 && at < trimmed.Length - 1 ? trimmed[(at + 1)..] : path;
        }
        var peaks = report?.Repos.ToDictionary(r => (SideOf(r.Side), r.Repo), r => r.PeakPrivateMb) ?? [];
        var rows = new List<RepoRow>();
        void Add(string side, IEnumerable<GroupDoc> groups)
        {
            foreach (var g in groups.Where(g => !string.IsNullOrWhiteSpace(g.Repo)))
            {
                var (peakGb, peak) = report is null ? ((double?)null, "Not available")
                    : peaks.TryGetValue((side, g.Repo!), out var mb) ? (GbOf(mb), GbOf(mb).ToString("0.0", CultureInfo.InvariantCulture)) : (null, NotInHistory);
                rows.Add(new RepoRow(Name(g.Repo!), g.Repo!, side == "wsl" ? "WSL" : "Windows", GbOf(g.PrivateMb), peakGb, peak, g.Processes));
            }
        }
        Add("windows", windows.Repos);
        if (vmRunning && wsl is not null) Add("wsl", wsl.Repos);
        // A repo with a peak in the last 24 hours but nothing running now is a row at 0.0, its peak said.
        foreach (var r in report?.Repos ?? [])
        {
            var side = SideOf(r.Side) == "wsl" ? "WSL" : "Windows";
            if ((side == "Windows" || vmRunning) && !rows.Any(x => x.Path == r.Repo && x.Side == side))
                rows.Add(new RepoRow(Name(r.Repo), r.Repo, side, 0, GbOf(r.PeakPrivateMb), GbOf(r.PeakPrivateMb).ToString("0.0", CultureInfo.InvariantCulture), 0));
        }
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        TableNote = Rows.Count == 0 ? NoRepos : "";
    }
}
