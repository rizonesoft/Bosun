using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Core.Instances;
using Bosun.Panel.Controls;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Shell;
using Bosun.Panel.Theme;
using Scan = Bosun.Core.Instances.InstancesView;

namespace Bosun.Panel.Instances;

/// <summary>
/// One instance as its row shows it (D03 T02 §2, design/SPEC.md §4): every cell in words, the numbers it sorts by, and
/// its two actions. A value that could not be read says "Not available" with its reason, never 0 (SPEC §1).
/// </summary>
public sealed class InstanceLine : Observable
{
    internal InstanceLine(InstanceRow row, string group, int groupOrder, int order, string? staleAs, string staleWhy, DateTimeOffset now)
    {
        Row = row;
        var i = row.Instance;
        Key = $"{i.Side}|{row.Distro ?? i.GuestDistro}|{i.Pid}";
        Group = group;
        GroupOrder = groupOrder;
        Order = order;
        Instance = i.Kind == "job" ? $"Build job: {i.Command ?? i.Name}" : KindWord(i.Kind);
        InstanceDetail = $"{i.Name}, pid {i.Pid}" + (i.Cwd is { } cwd ? $", in {cwd}" : "");
        Side = i.Side == "windows" ? "Windows" : row.Distro is { } d ? $"WSL ({d})" : i.GuestDistro is { } g ? $"WSL ({g})" : "WSL";
        IsStale = staleAs is not null;
        StaleWhy = staleWhy;
        State = staleAs ?? StateWords(i);
        StateDetail = IsStale ? row.Instance.Evidence is { } e ? $"{e}. {StaleWhy}" : StaleWhy : i.Evidence ?? StateWord(i.State);
        MemoryMb = i.PrivateMb;
        // "Not available" in the cell, its reason on hover and to a screen reader (design/SPEC.md §1): the reason
        // in the cell itself is cut off in a column sized for a number.
        Memory = i.PrivateMb is { } mb ? Bosun.Panel.Memory.MemoryPage.GbText(mb / 1024.0) + " GB" : NotAvailable;
        MemoryWhy = i.PrivateMb is null ? "Not available: its memory could not be read" : $"{i.PrivateMb} MB private";
        UptimeSeconds = Started(i.StartedAt) is { } started ? Math.Max(0, (long)(now - started).TotalSeconds) : null;
        Uptime = UptimeSeconds is { } seconds ? Duration(seconds) : NotAvailable;
        UptimeWhy = UptimeSeconds is not null ? $"Started {i.StartedAt}"
            : i.Kind == "job" && i.Slot is not null ? "Not available: its command has not started"
            : "Not available: its start could not be read";
        (Target, _folderProblem) = OpenCommands.Resolve(row);
    }

    public InstanceRow Row { get; }

    /// <summary>The same instance across refreshes: its side, its distro and its pid.</summary>
    public string Key { get; }

    /// <summary>The repo group's header: "D:/Bosun", "/home/me/app in WSL (Ubuntu)" or "Not in a repo".</summary>
    public string Group { get; }

    public int GroupOrder { get; }

    public int Order { get; }

    public string Instance { get; }

    public string InstanceDetail { get; }

    public string Side { get; }

    public string State { get; }

    public string StateDetail { get; }

    /// <summary>Whether this row is from a distro's last report, shown as old.</summary>
    public bool IsStale { get; }

    /// <summary>For an old row, why: its distro's own words.</summary>
    public string StaleWhy { get; }

    public const string NotAvailable = "Not available";

    public string Memory { get; }

    /// <summary>Whether GB shows a word, not a number: drawn dim by the theme (D02 T04 §4).</summary>
    public bool MemoryIsWord => MemoryMb is null;

    /// <summary>Whether Uptime shows a word, not a number.</summary>
    public bool UptimeIsWord => UptimeSeconds is null;

    /// <summary>The memory's reason or source: why it is not available, or the megabytes behind it.</summary>
    public string MemoryWhy { get; }

    /// <summary>The uptime's reason or source: why it is not available, or when it started.</summary>
    public string UptimeWhy { get; private set; } = "";

    public long? MemoryMb { get; }

    public string Uptime { get; }

    public long? UptimeSeconds { get; }

    public OpenTarget? Target { get; }

    /// <summary>Why its actions can't run, or null when they can: set too when a click found its folder gone.</summary>
    public string? FolderProblem
    {
        get => _folderProblem;
        internal set
        {
            if (!Set(ref _folderProblem, value)) return;
            Raise(nameof(OpenFolderTip));
            Raise(nameof(OpenTerminalTip));
            (OpenFolder as AsyncCommand)?.Refresh();
            (OpenTerminal as AsyncCommand)?.Refresh();
        }
    }

    private string? _folderProblem;

    public ICommand? OpenFolder { get; internal set; }

    public ICommand? OpenTerminal { get; internal set; }

    public string OpenFolderName => $"Open folder of {Instance} ({InstanceDetail})";

    public string OpenTerminalName => $"Open terminal in the folder of {Instance} ({InstanceDetail})";

    public string OpenFolderTip => FolderProblem ?? (Target is { } t ? $"Open {t.WindowsPath} in Explorer" : "");

    public string OpenTerminalTip => FolderProblem ?? (Target is { } t ? $"Open a terminal in {t.Path}" + (t.Distro is { } d ? $" in WSL ({d})" : "") : "");

    public static string KindWord(string kind) => kind switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "agy" => "Antigravity CLI",
        "code" => "VS Code",
        "antigravity" => "Antigravity",
        "job" => "Build job",
        _ => kind,
    };

    public static string StateWord(string state) => state switch
    {
        "working" => "Working",
        "reviewing" => "Reviewing",
        "idle" => "Idle",
        _ => "Unknown",
    };

    /// <summary>The state in words, with a job's queue slot: "Working, slot 1", "Idle, slot 2: waiting for guest memory".</summary>
    public static string StateWords(InstanceDoc i)
    {
        ArgumentNullException.ThrowIfNull(i);
        var word = StateWord(i.State);
        if (i.Slot is not { } slot) return word;
        var waiting = i.Evidence?.Contains("waiting for guest memory", StringComparison.Ordinal) == true ? ": waiting for guest memory" : "";
        return string.Create(CultureInfo.InvariantCulture, $"{word}, slot {slot}{waiting}");
    }

    public static string Duration(long seconds) => seconds switch
    {
        < 60 => string.Create(CultureInfo.InvariantCulture, $"{seconds} s"),
        < 3600 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60} min"),
        < 86400 when seconds % 3600 / 60 == 0 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h"),
        < 86400 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600} h {seconds % 3600 / 60} min"),
        _ when seconds % 86400 / 3600 == 0 => string.Create(CultureInfo.InvariantCulture, $"{seconds / 86400} d"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{seconds / 86400} d {seconds % 86400 / 3600} h"),
    };

    private static DateTimeOffset? Started(string? at) =>
        at is not null && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var started)
            ? started : null;
}

/// <summary>
/// The Instances page (D03 T02 §2, design/SPEC.md §4): every agent, editor and build job on Windows and in each WSL
/// distro, grouped by repo, read every 30 s while it is on screen. It only reads and opens: it stops nothing, since a
/// repo's processes are that repo's to manage. A side that can't be read keeps the others' rows and is said by name; a
/// distro that stopped reporting keeps its last rows, each marked old.
/// </summary>
public sealed class InstancesPage : PageBase
{
    public const string Purpose = "Every agent, editor and build on Windows and in WSL, by repo.";

    public const string NothingRunning = "Nothing is running: no agent, editor or build job on either side.";

    public const string NothingRunningAction = "Start one in a repo; it appears here within 30 s.";

    public const string NotInRepo = "Not in a repo";

    private readonly IInstanceSource _source;
    private readonly ActivityLog _log;
    private readonly ILauncher _launcher;
    private readonly Func<bool> _windowsTerminal;
    private readonly Func<string, bool> _folderExists;
    private readonly TimeSpan _folderWait;
    private InstanceLine? _selected;
    private string? _wslNotice;
    private Comparison<InstanceLine>? _sort;

    public InstancesPage(IInstanceSource source, RefreshScheduler scheduler, ActivityLog log, ILauncher? launcher = null,
        Func<bool>? windowsTerminal = null, Func<string, bool>? folderExists = null, TimeSpan? folderWait = null) : base("Instances", Purpose)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _launcher = launcher ?? new ProcessLauncher();
        _windowsTerminal = windowsTerminal ?? (() => OpenCommands.HasWindowsTerminal());
        _folderExists = folderExists ?? Directory.Exists;
        _folderWait = folderWait ?? TimeSpan.FromSeconds(3);
        LoadingShape = SkeletonShape.Table;
        var view = (ListCollectionView)CollectionViewSource.GetDefaultView(Lines);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InstanceLine.Group)));
        view.CustomSort = Comparer<InstanceLine>.Create(Compare);
        LinesView = view;
        RefreshEvery(scheduler, log, RefreshScheduler.Slow, ReadAsync);
    }

    /// <summary>Every row, in repo groups.</summary>
    public ObservableCollection<InstanceLine> Lines { get; } = [];

    public ListCollectionView LinesView { get; }

    /// <summary>Every source that could not be read, or is old, by name, above the table.</summary>
    public ObservableCollection<string> Notices { get; } = [];

    /// <summary>The row the palette's Open folder and Open terminal act on.</summary>
    public InstanceLine? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    /// <summary>The client's notice when no distro has reported: shown above the Windows rows.</summary>
    public string? WslNotice { get => _wslNotice; private set => Set(ref _wslNotice, value); }

    protected override IEnumerable<PageCommand> Commands
    {
        get
        {
            if (Selected is not { } line || line.OpenFolder is null || line.OpenTerminal is null) yield break;
            yield return new PageCommand($"Open folder of {line.Instance}", GlyphAction.OpenFolder, line.OpenFolder) { Reason = () => line.FolderProblem };
            yield return new PageCommand($"Open terminal for {line.Instance}", GlyphAction.OpenTerminal, line.OpenTerminal) { Reason = () => line.FolderProblem };
        }
    }

    /// <summary>
    /// Sorts the rows by a column within their groups, the groups kept in repo order: GB and Uptime by their numbers, a
    /// value not available last both ways (the plan review). Null puts the scanner's order back.
    /// </summary>
    public void SortBy(string? column, ListSortDirection direction)
    {
        var sign = direction == ListSortDirection.Ascending ? 1 : -1;
        _sort = column switch
        {
            null => null,
            nameof(InstanceLine.Memory) => (a, b) => Numbers(a.MemoryMb, b.MemoryMb, sign),
            nameof(InstanceLine.Uptime) => (a, b) => Numbers(a.UptimeSeconds, b.UptimeSeconds, sign),
            nameof(InstanceLine.Instance) => (a, b) => sign * string.Compare(a.Instance, b.Instance, StringComparison.CurrentCultureIgnoreCase),
            nameof(InstanceLine.Side) => (a, b) => sign * string.Compare(a.Side, b.Side, StringComparison.CurrentCultureIgnoreCase),
            nameof(InstanceLine.State) => (a, b) => sign * string.Compare(a.State, b.State, StringComparison.CurrentCultureIgnoreCase),
            _ => throw new ArgumentException($"no column '{column}'", nameof(column)),
        };
        LinesView.CustomSort = Comparer<InstanceLine>.Create(Compare);
    }

    private static int Numbers(long? a, long? b, int sign) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        _ => sign * a.Value.CompareTo(b.Value),
    };

    private int Compare(InstanceLine? a, InstanceLine? b)
    {
        if (a is null || b is null) return a is null ? b is null ? 0 : -1 : 1;
        var group = a.GroupOrder.CompareTo(b.GroupOrder);
        if (group != 0) return group;
        var by = _sort?.Invoke(a, b) ?? 0;
        return by != 0 ? by : a.Order.CompareTo(b.Order);
    }

    private async Task<Action> ReadAsync(CancellationToken token)
    {
        var view = await _source.ReadAsync(token);
        return () => Show(view);
    }

    private void Show(Scan view)
    {
        var now = Clock.GetUtcNow();
        // Each stale distro: its rows kept and marked old, said by name with its own age.
        var stale = new Dictionary<string, (string Why, TimeSpan Age, DateTimeOffset ReportedAt)>(StringComparer.Ordinal);
        foreach (var side in view.Sides)
            if (side.Reading is { IsStale: true, Value: not null } r && side.Distro is { } distro)
            {
                var age = r.Age ?? TimeSpan.Zero;
                stale[distro] = (r.Why ?? $"WSL ({distro}) has not reported lately.", age, (r.ReadAt ?? now) - age);
            }

        var notices = new List<string>();
        foreach (var side in view.Sides)
        {
            if (side.Reading.Value is not { } doc)
            {
                notices.Add($"{side.Name}: {side.Reading.Why}");
                continue;
            }
            if (side.Reading.IsStale) notices.Add(side.Reading.Why ?? $"{side.Name} has not reported lately.");
            foreach (var problem in doc.Problems) notices.Add($"{side.Name}: its {SourceWord(problem.Source)} could not be read: {problem.Why}");
            if (doc.Unreadable > 0)
                notices.Add(string.Create(CultureInfo.InvariantCulture, $"{side.Name}: {doc.Unreadable} {(doc.Unreadable == 1 ? "process" : "processes")} could not be read, so an instance may be missing."));
        }

        var lines = new List<InstanceLine>();
        var groups = view.Merged(keepStale: true);
        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];
            var header = group.Repo is null ? NotInRepo : group.Distro is { } d ? $"{group.Repo} in WSL ({d})" : group.Repo;
            for (var r = 0; r < group.Rows.Count; r++)
            {
                var row = group.Rows[r];
                string? staleAs = null;
                var why = "";
                if (row.Distro is { } distro && stale.TryGetValue(distro, out var old))
                {
                    staleAs = $"Last seen {Age(old.Age)} ago";
                    why = old.Why;
                }
                var line = new InstanceLine(row, header, g, r, staleAs, why, now);
                line.OpenFolder = Action(line, "Open folder", target => OpenCommands.Folder(target), checkFolder: true);
                line.OpenTerminal = Action(line, "Open terminal", target => OpenCommands.Terminal(target, _windowsTerminal()), checkFolder: true);
                lines.Add(line);
            }
        }

        // The selection follows its instance across a refresh, and goes when the instance does.
        var selectedKey = Selected?.Key;
        Lines.Clear();
        foreach (var line in lines) Lines.Add(line);
        Selected = selectedKey is null ? null : lines.FirstOrDefault(l => l.Key == selectedKey);

        Notices.Clear();
        foreach (var notice in notices) Notices.Add(notice);
        WslNotice = view.WslNotice;

        // The client's WSL notice (no distro reported, or the reports could not be listed) is as much a gap in what was
        // read as a failed side: the empty and failed states say it too, since they hide the view that shows it.
        var gaps = view.WslNotice is { } wsl ? [.. notices, wsl] : notices;
        var anyRead = view.Sides.Any(s => s.Reading.Value is not null);
        if (!anyRead)
        {
            ShowFailed(string.Join(Environment.NewLine, gaps.Count > 0 ? gaps : ["Nothing could be read."]));
            return;
        }
        if (lines.Count == 0)
        {
            // Nothing listed: said as nothing running only when every side was read whole.
            if (gaps.Count > 0) ShowEmpty("Nothing is listed, but the scan was incomplete: " + string.Join(" ", gaps.Select(n => n.TrimEnd('.') + ".")),
                "Retry with Refresh, or see the line for the source that could not be read.");
            else ShowEmpty(NothingRunning, NothingRunningAction);
            return;
        }
        // Every side read is old: the page as a whole is what was last seen.
        if (view.Sides.Where(s => s.Reading.Value is not null).All(s => s.Reading.IsStale) && stale.Count > 0)
        {
            var oldest = stale.Values.MinBy(s => s.ReportedAt);
            ShowLastKnown(oldest.Why, oldest.ReportedAt);
            return;
        }
        ShowData();
    }

    private static string SourceWord(string source) => source switch
    {
        "processes" => "process list",
        "sessions" => "Claude Code session records",
        "review-marks" => "review marks",
        "queue-records" => "queue records",
        _ => source,
    };

    /// <summary>An action on a row: its Activity line and toast, disabled with its reason when the row has no folder.</summary>
    private ICommand Action(InstanceLine line, string verb, Func<OpenTarget, System.Diagnostics.ProcessStartInfo> build, bool checkFolder)
    {
        return _log.Command($"{verb} of {line.Instance} ({line.InstanceDetail})", async () =>
        {
            if (line.Target is not { } target) return ActionOutcome.Failed(line.FolderProblem ?? OpenCommands.NoFolder);
            // A Windows folder is checked before anything is started; a distro's own folder is not, since reaching its
            // share would start a stopped distro just to look. The check and the start run off the UI thread, the
            // check bounded: a folder on a network share that has gone can block for the network's own timeout (round 1).
            var windowsSide = target.Distro is null || OpenCommands.MountedDrive(target.Path) is not null;
            if (checkFolder && windowsSide)
            {
                bool there;
                try
                {
                    there = await Task.Run(() => _folderExists(target.WindowsPath)).WaitAsync(_folderWait);
                }
                catch (TimeoutException)
                {
                    return ActionOutcome.Failed($"The folder could not be reached within {_folderWait.TotalSeconds:0} s: {target.WindowsPath}");
                }
                if (!there)
                {
                    // Its actions stay off with why until the next refresh reads the instance again.
                    line.FolderProblem = $"The folder is no longer there: {target.WindowsPath}";
                    return ActionOutcome.Failed(line.FolderProblem);
                }
            }
            var start = build(target);
            await Task.Run(() => _launcher.Start(start));
            return ActionOutcome.DoneWith(target.Distro is { } d ? $"{target.Path} in WSL ({d})" : target.Path);
        }, () => line.Target is not null && line.FolderProblem is null);
    }
}
