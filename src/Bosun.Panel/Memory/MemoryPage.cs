using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using Bosun.Panel.Theme;
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
/// Its private memory and processes now are null when its side's snapshot could not be read: never measured, never 0.
public sealed record RepoRow(string Repo, string Path, string Side, double? PrivateGb, double? PeakGb, string Peak, long? Processes)
{
    public string Private => PrivateGb is { } gb ? MemoryPage.GbText(gb) : "Not available";

    public string ProcessesNow => Processes?.ToString(CultureInfo.InvariantCulture) ?? "Not available";

    // Where a number goes and a word is shown, why (D02 T04 §4, design/SPEC.md §1): the table's tooltip and help text.
    public bool PrivateIsWord => PrivateGb is null;

    // A value's hover says its exact size, a word's says why (D02 T04 §4 completion review).
    public string? PrivateWhy => PrivateGb is { } gb ? MemoryPage.MbText(gb, "private now") : $"Not available: the {Side} snapshot could not be read";

    public bool PeakIsWord => PeakGb is null;

    public string? PeakWhy => PeakGb is { } gb ? MemoryPage.MbText(gb, "at its peak in the last 24 hours")
        : Peak == MemoryPage.NotInHistory ? "No sample of this repo is in the last 24 hours' history yet"
        : "Not available: the last 24 hours' history could not be read";

    public bool ProcessesIsWord => Processes is null;

    public string? ProcessesWhy => Processes is { } n ? string.Create(CultureInfo.InvariantCulture, $"{n} {(n == 1 ? "process" : "processes")} now") : $"Not available: the {Side} snapshot could not be read";
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
    private Reading<ReportDoc>? _report, _fortnight;
    // The last report each read gave, kept to draw from with its age when a later read fails (D03 T01 §3).
    private Reading<ReportDoc>? _dayGood, _fortnightGood;
    private HistoryRange _range = HistoryRange.Day;
    private IReadOnlyList<HistoryPoint> _points = [];
    private string _historyState = "Reading the history.", _lowestSummary = "";
    private bool _showTable;
    private Bosun.Core.Refresh? _fortnightRead;
    private Decision? _decision;
    private string _decisionState = ReadingDecision;
    private long _floorMb = Bosun.Core.PanelSettings.DefaultPressureFloorMb;
    private object? _windowsUsed, _wslVm, _overlap;
    private string _windowsNote = "", _wslNote = "", _overlapNote = "", _historyNote = "", _wslAge = "", _tableNote = "", _peaksNote = "";

    public MemoryPage(IMemorySource client, RefreshScheduler scheduler, ActivityLog log, Bosun.Core.PanelSettingsStore? settings = null) : base("Memory", Purpose)
    {
        // The pressure line is the stored threshold, and moves when Settings saves another (D03 T01 §3).
        if (settings is not null)
        {
            _floorMb = settings.Current.PressureFloorMb;
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            settings.Changed += saved => dispatcher.BeginInvoke(() => FloorMb = saved.PressureFloorMb);
            // The decision's minutes are counted against the floor: a new one reads the fortnight again at once (D03 T01 §6).
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(FloorMb)) return;
                // The verdict shown was counted at the old floor: it goes, shown or not, until one at the new floor arrives.
                if (_fortnightGood?.Value is { } shown && shown.LowMb != FloorMb)
                {
                    _fortnightGood = null;
                    Decision = null;
                    DecisionState = RereadingDecision(FloorMb);
                }
                if (IsShown) _ = _fortnightRead?.RunNowAsync();
            };
        }
        ShowAsTable = new Relay(() => ShowTable = true);
        ShowAsChart = new Relay(() => ShowTable = false);
        _client = client;
        LoadingShape = SkeletonShape.Gauge;
        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.SortDescriptions.Add(new SortDescription(nameof(RepoRow.PrivateGb), ListSortDirection.Descending));
        RefreshEvery(scheduler, log, RefreshScheduler.Fast, ReadSnapshotsAsync);
        RefreshAlso(scheduler, "history", RefreshScheduler.Slow, ReadReportAsync);
        // The 7 and 14 days come from a fortnight's report, read less often: it reads every log of those days.
        _fortnightRead = RefreshAlso(scheduler, "fortnight", FortnightEvery, ReadFortnightAsync);
    }

    /// <summary>How often the fortnight's report is read while the page is shown.</summary>
    public static readonly TimeSpan FortnightEvery = TimeSpan.FromMinutes(5);

    public const string ReadingHistory = "Reading the history.";

    public const string NoHistory = "No history in this range yet: leave the logger running and it fills.";

    /// <summary>The chart's ranges, for its segmented control.</summary>
    public IReadOnlyList<HistoryRange> Ranges => HistoryRange.All;

    /// <summary>The range shown; changing it draws that range's points, or its own state, never the last range's.</summary>
    public HistoryRange Range
    {
        get => _range;
        set
        {
            if (value is null || !Set(ref _range, value)) return;
            DrawHistory();
        }
    }

    /// <summary>The chart's points and the table's rows: the same buckets.</summary>
    public IReadOnlyList<HistoryPoint> Points { get => _points; private set => Set(ref _points, value); }

    /// <summary>What the history card says about its data: reading, not read, incomplete, none in the range; empty when whole.</summary>
    public string HistoryState { get => _historyState; private set => Set(ref _historyState, value); }

    /// <summary>The chart card's header line: the lowest point in the range and when ("Lowest 1.5 GB at Mon 16 Nov 14:20").</summary>
    public string LowestSummary { get => _lowestSummary; private set => Set(ref _lowestSummary, value); }

    /// <summary>The pressure line, in MB: the stored threshold.</summary>
    public long FloorMb
    {
        get => _floorMb;
        private set
        {
            if (Set(ref _floorMb, value)) Raise(nameof(FloorNote));
        }
    }

    public string FloorNote => $"Dashed line: the {(FloorMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} GB pressure alert.";

    /// <summary>True while the card shows its table in place of the chart.</summary>
    public bool ShowTable { get => _showTable; private set => Set(ref _showTable, value); }

    public ICommand ShowAsTable { get; }

    public ICommand ShowAsChart { get; }

    /// <summary>The page's own commands for Ctrl+K: whichever of Show as table and Show as chart the card offers now.</summary>
    protected override IEnumerable<PageCommand> Commands
    {
        get
        {
            // A history that failed while the live cards are fine leaves the page in its data state, so its Retry is
            // the history card's own (completion review): the page's refresh, which reads all three again.
            if (HistoryFailed && RefreshNow is { } retry) yield return new PageCommand("Retry reading the history", GlyphAction.Refresh, retry);
            // The decision's own Retry, whatever range the chart shows (D03 T01 §6).
            if (DecisionFailed && RefreshNow is { } again) yield return new PageCommand("Retry reading the decision's history", GlyphAction.Refresh, again);
            yield return ShowTable ? new PageCommand("Show the history as a chart", GlyphAction.Chart, ShowAsChart) : new PageCommand("Show the history as a table", GlyphAction.Table, ShowAsTable);
        }
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

    /// <summary>
    /// What an empty table can say: only what the reads established (completion review). With every source read it is
    /// <see cref="NoRepos"/>; a WSL that runs but could not be read, or a history not yet or not read, is said instead
    /// of claimed.
    /// </summary>
    public static string Empty(bool historyRead, bool historyTried, bool vmRunning, bool wslRead)
    {
        if (historyRead && (!vmRunning || wslRead)) return NoRepos;
        var now = vmRunning && !wslRead ? "No repo has processes running on Windows now; WSL's could not be read." : "No repo has processes running now.";
        var history = !historyRead ? historyTried ? " The last 24 hours' history could not be read, so earlier repos can't be listed."
            : " The last 24 hours' history is still being read." : "";
        return now + history;
    }

    public static string Gb(long mb) => (mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";

    private static double GbOf(long mb) => mb / 1024.0;

    /// <summary>
    /// Gigabytes to one decimal, and "&lt; 0.1" for a positive amount that would round to 0.0: 40 MB is not nothing
    /// (D02 T04 §4 completion review). The exact size is the cell's hover (<see cref="MbText"/>).
    /// </summary>
    public static string GbText(double gb) => gb > 0 && gb < 0.05 ? "< 0.1" : gb.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>The exact size behind a cell, in megabytes: "40 MB private now".</summary>
    public static string MbText(double gb, string what) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(gb * 1024):0} MB {what}");

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

    private async Task<Action> ReadFortnightAsync(CancellationToken token)
    {
        // The floor goes with the read, and a read taken at a floor since changed is dropped: the minutes, the line and
        // the verdict always agree (D03 T01 §6).
        var floor = FloorMb;
        var report = await _client.ReportAsync(14, floor, token);
        return () =>
        {
            // Read at a floor since changed: dropped, and read again at the new one, since a change saved while this
            // read was under way joined it rather than starting another.
            if (floor != FloorMb)
            {
                _ = _fortnightRead?.RunNowAsync();
                return;
            }
            _fortnight = report;
            if (report.Value is not null) _fortnightGood = report;
            DrawHistory();
            DrawDecision();
        };
    }

    public const string ReadingDecision = "Reading the last 14 days for the decision.";

    /// <summary>What the card says while the fortnight is read again at a new pressure line.</summary>
    public static string RereadingDecision(long floorMb) =>
        $"Reading the last 14 days again at the new {(floorMb / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} GB pressure line.";

    /// <summary>The Decision card's answer from the fortnight's report; null until one has been read.</summary>
    public Decision? Decision { get => _decision; private set => Set(ref _decision, value); }

    /// <summary>What the Decision card says about its data: reading, not read, or kept from earlier; empty when current.</summary>
    public string DecisionState { get => _decisionState; private set => Set(ref _decisionState, value); }

    /// <summary>Whether the fortnight's last read failed: the card's Retry shows, whatever range the chart shows.</summary>
    public bool DecisionFailed => _fortnight is { Value: null };

    /// <summary>The Decision card from the last fortnight read, with its own states, apart from the chart's range.</summary>
    private void DrawDecision()
    {
        Raise(nameof(DecisionFailed));
        if (_fortnightGood?.Value is not { } doc)
        {
            Decision = null;
            DecisionState = _fortnight is null ? ReadingDecision : $"The last 14 days could not be read for the decision: {_fortnight.Why}";
            return;
        }
        Decision = DecisionRule.Decide(doc);
        DecisionState = _fortnight is { Value: null }
            ? $"The last 14 days could not be read again: {_fortnight.Why} Showing the verdict from {ReadWhen(_fortnightGood.ReadAt)}."
            : "";
    }

    private DateTime? _chartFrom, _chartTo;
    private string? _chartEnd;

    /// <summary>Where the chart's time axis starts, in UTC: the report's time less the range.</summary>
    public DateTime? ChartFrom { get => _chartFrom; private set => Set(ref _chartFrom, value); }

    /// <summary>Where it ends: the report's time.</summary>
    public DateTime? ChartTo { get => _chartTo; private set => Set(ref _chartTo, value); }

    /// <summary>What its end says: "Now", or a kept reading's time and age.</summary>
    public string? ChartEnd { get => _chartEnd; private set => Set(ref _chartEnd, value); }

    /// <summary>The chart's points for the range shown, from its own report, and what it says about them.</summary>
    private void DrawHistory()
    {
        var (latest, good) = _range == HistoryRange.Day ? (_report, _dayGood) : (_fortnight, _fortnightGood);
        Raise(nameof(HistoryFailed));
        if (good?.Value is not { } doc)
        {
            Points = [];
            LowestSummary = "";
            // No reading for this range: no axis either, never another range's span or two made-up times (completion review).
            ChartFrom = null;
            ChartTo = null;
            ChartEnd = null;
            HistoryState = latest is null ? ReadingHistory : $"The history could not be read: {latest.Why}";
            return;
        }
        var end = DateTime.TryParse(doc.GeneratedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : DateTime.UtcNow;
        Points = History.Buckets(doc.Series, _range, end);
        // The chart's span is the report's own period, its end "Now" while this is the latest read (D02 T04 §5).
        ChartFrom = end - _range.Length;
        ChartTo = end;
        ChartEnd = latest is { Value: null }
            ? $"{end.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}, {Age(Clock.GetUtcNow() - new DateTimeOffset(end, TimeSpan.Zero))} ago"
            : "Now";
        var low = History.Lowest(Points);
        LowestSummary = low is null ? "" : $"Lowest {low.Lowest} at {low.LowestAt!.Value.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}";
        var notes = new List<string>();
        if (latest is { Value: null })
            notes.Add($"The history could not be read again: {latest.Why} Showing the read from {ReadWhen(good.ReadAt)}.");
        if (low is null) notes.Add(NoHistory);
        if (!doc.Read.Complete) notes.Add($"The history is incomplete: {ReadLoss.Words(doc.Read)}.");
        HistoryState = string.Join(" ", notes);
    }

    /// <summary>
    /// When a kept read was taken, with its day and age ("Sun 16 Nov 21:40, 3 hours ago"), so yesterday's is never
    /// taken for today's (completion review).
    /// </summary>
    private string ReadWhen(DateTimeOffset? at) => at is { } read
        ? $"{read.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}, {Age(Clock.GetUtcNow() - read)} ago"
        : "earlier";

    /// <summary>Whether the range shown failed its last read: its Retry shows, on the card and for Ctrl+K.</summary>
    public bool HistoryFailed => (_range == HistoryRange.Day ? _report : _fortnight) is { Value: null };

    private sealed class Relay(Action run) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => run();
    }

    private async Task<Action> ReadReportAsync(CancellationToken token)
    {
        var report = await _client.ReportAsync(1, null, token);
        return () =>
        {
            _report = report;
            if (report.Value is not null) _dayGood = report;
            Draw();
            DrawHistory();
        };
    }

    /// <summary>Everything on the page from the last readings.</summary>
    private void Draw()
    {
        if (_windows?.Value is not { } windows) return;
        var total = windows.Counter("total_mb");
        var available = windows.Counter("available_mb");
        WindowsUsed = total is { } t && available is { } a && t >= a && a >= 0 ? new Metric(Gb(t - a)) : new NotAvailable("The Windows logger did not measure total and available memory.");
        // The room left, the page's own question (completion review): available of total under what is used.
        var room = total is { } tt && available is { } aa && aa >= 0 ? $"{Gb(aa)} available of {Gb(tt)}" : "Available: not measured";
        WindowsNote = room + "; " + (windows.Counter("commit_mb") is { } commit ? $"committed {Gb(commit)}" : "committed: not measured");
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
                : $"The last 24 hours' history is incomplete: {ReadLoss.Words(report.Read)}, so peaks may be higher.";
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
        // One peak per side and repo, the highest: a history that holds a repo under both "win" and "windows" is one
        // repo on one side (round 1: two such rows made ToDictionary throw, and the page stopped updating).
        var peaks = (report?.Repos ?? []).GroupBy(r => (SideOf(r.Side), r.Repo)).ToDictionary(g => g.Key, g => g.Max(r => r.PeakPrivateMb));
        var rows = new List<RepoRow>();
        void Add(string side, IEnumerable<GroupDoc> groups)
        {
            foreach (var g in groups.Where(g => !string.IsNullOrWhiteSpace(g.Repo)))
            {
                var (peakGb, peak) = report is null ? ((double?)null, "Not available")
                    : peaks.TryGetValue((side, g.Repo!), out var mb) ? (GbOf(mb), GbText(GbOf(mb))) : (null, NotInHistory);
                rows.Add(new RepoRow(Name(g.Repo!), g.Repo!, side == "wsl" ? "WSL" : "Windows", GbOf(g.PrivateMb), peakGb, peak, g.Processes));
            }
        }
        Add("windows", windows.Repos);
        if (vmRunning && wsl is not null) Add("wsl", wsl.Repos);
        // A repo with a peak in the last 24 hours that its side's snapshot does not list: nothing running now, a row at
        // 0.0 with its peak. When that side's snapshot could not be read, what runs now was not measured: its current
        // figures are Not available, never 0 (round 1).
        foreach (var ((sideKey, path), mb) in peaks)
        {
            var side = sideKey == "wsl" ? "WSL" : "Windows";
            if ((side == "Windows" || vmRunning) && !rows.Any(x => x.Path == path && x.Side == side))
            {
                var measured = side == "Windows" || wsl is not null;
                rows.Add(new RepoRow(Name(path), path, side, measured ? 0 : null, GbOf(mb), GbText(GbOf(mb)), measured ? 0 : null));
            }
        }
        Rows.Clear();
        foreach (var row in rows) Rows.Add(row);
        TableNote = Rows.Count == 0 ? Empty(report is not null, _report is not null, vmRunning, wsl is not null) : "";
    }
}
