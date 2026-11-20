using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Core.Instances;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Bosun.Panel.Controls;
using Bosun.Panel.Instances;
using Bosun.Panel.Memory;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;
using Scan = Bosun.Core.Instances.InstancesView;

namespace Bosun.Panel.Overview;

/// <summary>
/// One of the Overview's four metric cards (D03 T01 §5, design/SPEC.md §2 and §19): its title and glyph, its value (a
/// <see cref="Metric"/>, or a <see cref="NotAvailable"/> with its reason, never 0), the gauge where it has a fraction
/// (an empty track with "Not available" when the value can't be read), a note under it and a warning when what it
/// shows is not current or not whole. The whole card is one named, keyboard-operable button that opens its page.
/// </summary>
public sealed class OverviewCard : Observable
{
    private object? _value = new Metric(OverviewPage.Checking);
    private string _note = OverviewPage.BeingRead, _warning = "", _word = "", _unit = "GB free";
    private double? _gaugeValue, _gaugeMaximum;
    private StatusKind _state = StatusKind.Neutral;
    private bool _showGauge, _wordsWithGauge, _usable;
    private string _gaugeWord = "";

    internal OverviewCard(string title, string section, string page, PackIconLucideKind glyph, ICommand open)
    {
        Title = title;
        Section = section;
        Page = page;
        Glyph = glyph;
        Open = open;
    }

    public string Title { get; }

    /// <summary>The rail key of the page it opens.</summary>
    public string Section { get; }

    /// <summary>That page's title, as its Open says it.</summary>
    public string Page { get; }

    public PackIconLucideKind Glyph { get; }

    /// <summary>Goes to its page, through the shell, which asks about unsaved edits first.</summary>
    public ICommand Open { get; }

    /// <summary>The card's value: a <see cref="Metric"/> or a <see cref="NotAvailable"/>.</summary>
    public object? Value { get => _value; private set => Set(ref _value, value); }

    /// <summary>The line under the value: what it is of, or why it is not there.</summary>
    public string Note { get => _note; private set => Set(ref _note, value); }

    /// <summary>What is not current or not whole about it, in the warning colour; empty when it is both.</summary>
    public string Warning { get => _warning; private set => Set(ref _warning, value); }

    /// <summary>Whether it draws its gauge: a value with a total, or one that can't be read (the empty track).</summary>
    public bool ShowGauge { get => _showGauge; private set => Set(ref _showGauge, value); }

    public double? GaugeValue { get => _gaugeValue; private set => Set(ref _gaugeValue, value); }

    public double? GaugeMaximum { get => _gaugeMaximum; private set => Set(ref _gaugeMaximum, value); }

    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    public string Word { get => _word; private set => Set(ref _word, value); }

    /// <summary>The state word the gauge draws under its value; a card's <see cref="Word"/> is the one its value is said with.</summary>
    public string GaugeWord { get => _gaugeWord; private set => Set(ref _gaugeWord, value); }

    /// <summary>Whether the card holds a value that was read (a count, a figure, "WSL not running"), not "Not available" and not the first look.</summary>
    public bool Usable => _usable;

    /// <summary>Whether the value's text is left out of the card because its gauge says it: a card whose value is words keeps them beside its gauge.</summary>
    public bool ValueHidden => _showGauge && !_wordsWithGauge;

    public StatusKind State { get => _state; private set => Set(ref _state, value); }

    /// <summary>The gauge's fraction, as the gauge works it out: null for an empty track.</summary>
    public double? Fraction => _showGauge ? RadialGauge.FractionOf(_gaugeValue, _gaugeMaximum) : null;

    /// <summary>The value in words: "19.5 GB, Safe", "1 running, 2 waiting", or "Not available: why".</summary>
    public string Said => _value switch
    {
        NotAvailable na => na.ToString(),
        Metric m when _word.Length > 0 => $"{m.Text}, {_word}",
        Metric m => m.Text,
        _ => "Not available",
    };

    /// <summary>What a screen reader calls the card: its title, its value, and where it goes.</summary>
    public string OpenName => $"{Title}: {Said}. Open {Page}";

    /// <summary>What it says beside its name: its note and its warning.</summary>
    public string Help => string.Join(" ", new[] { _note, _warning }.Where(said => said.Length > 0));

    /// <summary>What the palette lists it as.</summary>
    public string PaletteName => $"Open {Page}: {Title}";

    internal void ShowValue(string text, string note, string warning = "")
    {
        ShowGauge = false;
        GaugeValue = GaugeMaximum = null;
        Word = GaugeWord = "";
        _wordsWithGauge = false;
        _usable = true;
        Draw(new Metric(text), note, warning);
    }

    internal void ShowGaugeOf(double valueGb, double totalGb, string word, StatusKind state, string note, string warning = "")
    {
        ShowGauge = true;
        _wordsWithGauge = false;
        _usable = true;
        GaugeValue = Math.Round(valueGb, 1);
        GaugeMaximum = Math.Round(totalGb, 1);
        Unit = "GB free";
        Word = GaugeWord = word;
        State = state;
        Draw(new Metric(MemoryPage.GbText(valueGb) + " GB"), note, warning);
    }

    /// <summary>A value in words with a gauge of a count beside it: the queue's words, and how many of its slots are in use.</summary>
    internal void ShowWordsWithGauge(string text, double value, double maximum, string unit, string gaugeWord, StatusKind state, string note, string warning = "")
    {
        ShowGauge = true;
        _wordsWithGauge = true;
        _usable = true;
        GaugeValue = value;
        GaugeMaximum = maximum;
        Unit = unit;
        Word = "";
        GaugeWord = gaugeWord;
        State = state;
        Draw(new Metric(text), note, warning);
    }

    /// <summary>The value can't be read: "Not available" with why; a gauge card keeps its empty track.</summary>
    internal void ShowNotAvailable(string why, bool gauge, string warning = "")
    {
        ShowGauge = gauge;
        _wordsWithGauge = false;
        _usable = false;
        GaugeValue = GaugeMaximum = null;
        Word = GaugeWord = "";
        State = StatusKind.Neutral;
        // The theme draws a value's "Not available" with its reason; a gauge's empty track has only the words, so its
        // note says why. Never both, so the reason is said once.
        Draw(new NotAvailable(why), gauge ? why : "", warning);
    }

    private void Draw(object value, string note, string warning)
    {
        Value = value;
        Note = note;
        Warning = warning;
        Raise(nameof(Fraction));
        Raise(nameof(ValueHidden));
        Raise(nameof(Usable));
        Raise(nameof(Said));
        Raise(nameof(OpenName));
        Raise(nameof(Help));
    }
}

/// <summary>One row of the Needs you table: the provider's row in words, and its Open.</summary>
public sealed class NeedsLine
{
    internal NeedsLine(NeedsYouRow row, ICommand open)
    {
        Row = row;
        Open = open;
    }

    public NeedsYouRow Row { get; }

    public string State => Row.State;

    public string What => Row.What;

    public string Where => Row.Where;

    /// <summary>The state pill's colour: its word says it too.</summary>
    public StatusKind Kind => Row.Severity switch
    {
        NeedsSeverity.Urgent => StatusKind.Danger,
        NeedsSeverity.Warning => StatusKind.Warn,
        _ => StatusKind.Info,
    };

    public ICommand Open { get; }

    /// <summary>The Open button's name, and the palette's: which page, for what.</summary>
    public string OpenName => $"Open {Where}: {What}";
}

/// <summary>
/// The Needs you table (D03 T01 §5, design/SPEC.md §2): its rows in <see cref="NeedsYou"/>'s order, each with Open, and
/// what it says with none: "Nothing needs you." only once every provider has looked, the pressure check among them.
/// The Overview draws it under its cards, and keeps it above the failure when every card fails.
/// </summary>
public sealed class NeedsYouTable : Observable
{
    public const string Nothing = "Nothing needs you.";

    public const string Looking = "Checking what needs you.";

    private string? _empty = Looking;

    public ObservableCollection<NeedsLine> Lines { get; } = [];

    /// <summary>What it says in place of rows; null while it has some.</summary>
    public string? Empty { get => _empty; private set => Set(ref _empty, value); }

    internal void Show(IReadOnlyList<NeedsLine> lines, bool looked)
    {
        Lines.Clear();
        foreach (var line in lines) Lines.Add(line);
        Empty = lines.Count > 0 ? null : looked ? Nothing : Looking;
    }
}

/// <summary>
/// The Overview (D03 T01 §5, design/SPEC.md §2): what opens first, to decide in five seconds whether to start more work
/// and what needs attention. Four metric cards (Windows available, WSL guest available, Queue, Dev instances), then the
/// Needs you table. It owns no data: while it is shown it reads the same sources as the Memory, Queue and Instances
/// pages, at their pace (the snapshots and the queue's status every 5 s, the instances every 30 s), each on its own, so
/// a slow or failing source never holds back or replaces another, and words them with those pages' own functions, so a
/// card can't disagree with its page. Hidden, it reads nothing. A card whose source failed says "Not available" with
/// why; the page is Failed only once every source has answered with nothing usable, and even then the table stays.
/// </summary>
public sealed class OverviewPage : PageBase
{
    public const string Purpose = "Memory, the queue and dev instances at a glance, and what needs you.";

    public const string Checking = "Checking";

    public const string BeingRead = "Being read.";

    private readonly IMemorySource _memory;
    private readonly IQueueSource _queue;
    private readonly IInstanceSource _instances;
    private readonly ActivityLog _log;
    private readonly QueueWaiting _waiting;
    private readonly QueueSummary? _words;
    private readonly Bosun.Core.PanelSettingsStore? _settings;
    private long _windowsAsked, _windowsApplied, _guestAsked, _guestApplied, _queueAsked, _queueApplied, _scanAsked, _scanApplied;
    private Reading<SnapshotDoc>? _windows, _guest;
    private Reading<StatusDoc>? _status;
    private Scan? _scan;
    private string? _scanFailure;
    private List<string> _scanWhys = [];
    private bool _scanAnswered;

    public OverviewPage(IMemorySource memory, IQueueSource queue, IInstanceSource instances, RefreshScheduler scheduler, ActivityLog log,
        NeedsYou needs, QueueWaiting waiting, QueueSummary? words = null, Bosun.Core.PanelSettingsStore? settings = null) : base("Overview", Purpose)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _instances = instances ?? throw new ArgumentNullException(nameof(instances));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Needs = needs ?? throw new ArgumentNullException(nameof(needs));
        _waiting = waiting ?? throw new ArgumentNullException(nameof(waiting));
        _words = words;
        _settings = settings;
        Windows = Card("Windows available", "memory", "Memory", Glyphs.For(GlyphAction.WindowsMemory));
        Guest = Card("WSL guest available", "memory", "Memory", Glyphs.For(GlyphAction.WslMemory));
        Queue = Card("Queue", "queue", "Queue", Glyphs.ForPage("queue"));
        Instances = Card("Dev instances", "instances", "Instances", Glyphs.ForPage("instances"));
        Cards = [Windows, Guest, Queue, Instances];
        LoadingShape = Bosun.Panel.Controls.SkeletonShape.Overview;
        // The table follows Needs you whatever page is shown: its providers' sources already run (the pressure check).
        KeptWhenFailed = Table;
        Needs.Changed += ShowNeeds;
        ShowNeeds();
        RefreshEvery(scheduler, log, RefreshScheduler.Fast, ReadMemoryAsync);
        RefreshAlso(scheduler, "queue", RefreshScheduler.Fast, ReadQueueAsync);
        RefreshAlso(scheduler, "instances", RefreshScheduler.Slow, ReadInstancesAsync);
    }

    /// <summary>Goes to a page by its rail key: the shell's <see cref="ShellViewModel.GoToAsync"/>, given once the shell is made.</summary>
    public Func<string, Task<bool>>? Navigate { get; set; }

    /// <summary>What needs the person: the table's rows, the badges' and the tray's.</summary>
    public NeedsYou Needs { get; }

    public NeedsYouTable Table { get; } = new();

    public OverviewCard Windows { get; }

    public OverviewCard Guest { get; }

    public OverviewCard Queue { get; }

    public OverviewCard Instances { get; }

    /// <summary>The four cards in the order they are drawn.</summary>
    public IReadOnlyList<OverviewCard> Cards { get; }

    /// <summary>Every card's Open and every row's, for Ctrl+K: the very commands their buttons run.</summary>
    protected override IEnumerable<PageCommand> Commands =>
        Cards.Select(card => new PageCommand(card.PaletteName, GlyphAction.Page, card.Open))
            .Concat(Table.Lines.Select(line => new PageCommand(line.OpenName, GlyphAction.Page, line.Open)));

    private OverviewCard Card(string title, string section, string page, PackIconLucideKind glyph) =>
        new(title, section, page, glyph, Opener(section, page));

    /// <summary>A named Open: navigation is not an action, so it writes no line unless it fails.</summary>
    private AsyncCommand Opener(string section, string page) => new(async () =>
    {
        if (Navigate is { } go) await go(section);
    }, e => _log.Fail($"Open {page}: {e.Message}"));

    private void ShowNeeds()
    {
        Table.Show([.. Needs.Rows.Select(row => new NeedsLine(row, Opener(row.Section, row.Where)))], Needs.HasLooked);
    }

    // ---- reading, each source on its own ----

    /// <summary>A read that throws is a read that failed, said like any other; a cancelled one is the page leaving.</summary>
    private static async Task<Reading<T>> Caught<T>(Task<Reading<T>> read) where T : class
    {
        try
        {
            return await read;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Reading<T>.Fail(ReadingProblem.Failed, string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message);
        }
    }

    /// <summary>
    /// Both snapshots are read together and each card shows its own the moment it is read: a guest file held in its
    /// logger's replace never holds back the Windows card, nor the reverse. Each is applied here, on the UI thread, and
    /// only while this run was not stopped (the page leaving the screen cancels its token); a newer read of a side that
    /// is on its card already is never replaced by an older one.
    /// </summary>
    private async Task<Action> ReadMemoryAsync(CancellationToken token)
    {
        var windowsSeq = ++_windowsAsked;
        var guestSeq = ++_guestAsked;
        var windows = Caught(_memory.SnapshotAsync("windows", token));
        var guest = Caught(_memory.SnapshotAsync("wsl", token));
        async Task ShowWindows()
        {
            var reading = await windows;
            if (token.IsCancellationRequested || windowsSeq <= _windowsApplied) return;
            _windowsApplied = windowsSeq;
            _windows = reading;
            DrawWindows();
            DrawGuest(); // whether the VM runs depends on the Windows sample too
            Settle();
        }
        async Task ShowGuest()
        {
            var reading = await guest;
            if (token.IsCancellationRequested || guestSeq <= _guestApplied) return;
            _guestApplied = guestSeq;
            _guest = reading;
            DrawGuest();
            Settle();
        }
        await Task.WhenAll(ShowWindows(), ShowGuest());
        return () => { };
    }

    private async Task<Action> ReadQueueAsync(CancellationToken token)
    {
        var seq = ++_queueAsked;
        // The waiter count's and the tray's tickets as the read starts: a slower read begun earlier can't replace this.
        var waiting = _waiting.Begin();
        var words = _words?.Begin();
        var status = await Caught(_queue.StatusAsync(token));
        return () =>
        {
            _waiting.Take(status, waiting);
            if (words is { } ticket) _words!.Take(status, ticket);
            if (seq <= _queueApplied) return;
            _queueApplied = seq;
            _status = status;
            DrawQueue();
            Settle();
        };
    }

    private async Task<Action> ReadInstancesAsync(CancellationToken token)
    {
        var seq = ++_scanAsked;
        Scan? scan = null;
        string? failure = null;
        try
        {
            scan = await _instances.ReadAsync(token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            failure = string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message;
        }
        return () =>
        {
            if (seq <= _scanApplied) return;
            _scanApplied = seq;
            (_scan, _scanFailure, _scanAnswered) = (scan, failure, true);
            _scanWhys = scan is null ? [failure ?? "The instances could not be read."] : InstanceReasons(scan);
            DrawInstances();
            Settle();
        };
    }

    /// <summary>
    /// The page's state from what has answered: Data once any card holds a value that was read (a card that says "Not
    /// available" is not one, whether its source failed or answered with nothing usable); Failed, quoting each card's
    /// reason, once every source has answered and no card has a value; until then, Loading.
    /// </summary>
    private void Settle()
    {
        if (Cards.Any(card => card.Usable))
        {
            ShowData();
            return;
        }
        if (_windows is null || _status is null || !_scanAnswered) return;
        var whys = new List<string?>();
        foreach (var card in new[] { Windows, Guest, Queue })
            if (card.Value is NotAvailable na) whys.Add(na.Reason);
        whys.AddRange(_scanWhys);
        var said = whys.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).Distinct().ToList();
        ShowFailed(string.Join(Environment.NewLine, said.Count > 0 ? said : ["Nothing could be read."]));
    }

    // ---- the cards ----

    /// <summary>The Windows alert threshold the word is judged against: the stored one when the page has the settings.</summary>
    private long FloorMb => _settings?.Current.PressureFloorMb ?? Bosun.Core.PanelSettings.DefaultPressureFloorMb;

    /// <summary>Windows' free memory in a word: under the alert threshold Low, within the pressure check's recovery band Tight, else Safe.</summary>
    public static (string Word, StatusKind State) WindowsWord(long availableMb, long floorMb) =>
        availableMb < floorMb ? ("Low", StatusKind.Danger)
        : availableMb <= floorMb + PressureWatch.Hysteresis ? ("Tight", StatusKind.Warn)
        : ("Safe", StatusKind.Ok);

    /// <summary>The note a snapshot's memory has: "19.5 GB available of 31.3 GB", as the Memory page says it.</summary>
    private static string Room(long available, long total) => $"{MemoryPage.Gb(available)} available of {MemoryPage.Gb(total)}";

    private static string NotCurrent(Reading<SnapshotDoc> reading, string side) =>
        reading.IsStale ? $"Not current: {reading.Why ?? $"the {side} logger has not written lately."}" : "";

    private void DrawWindows()
    {
        if (_windows is not { } reading) return;
        if (reading.Value is not { } doc)
        {
            Windows.ShowNotAvailable(reading.Why ?? "The Windows snapshot could not be read.", gauge: true);
            return;
        }
        switch (doc.Figures(out var available, out var total))
        {
            case MemoryFigure.NotMeasured:
                // The Memory page's own words for the same gap.
                Windows.ShowNotAvailable("The Windows logger did not measure total and available memory.", gauge: true, NotCurrent(reading, "Windows"));
                return;
            case MemoryFigure.Impossible:
                // Impossible counters are never a Safe verdict (round 1).
                Windows.ShowNotAvailable(Impossible("Windows", available, total), gauge: true, NotCurrent(reading, "Windows"));
                return;
        }
        var (word, state) = WindowsWord(available, FloorMb);
        Windows.ShowGaugeOf(available / 1024.0, total / 1024.0, word, state, Room(available, total), NotCurrent(reading, "Windows"));
    }

    public const string WslNotRunning = "WSL not running";

    public const string WslNeverReported = "WSL has not reported its memory: no sample of the guest has been written.";

    private void DrawGuest()
    {
        if (_guest is not { } guest) return;
        // Whether the VM runs, decided as the Memory page decides it: by the newer of the two fresh samples, and a stale
        // snapshot says nothing about now (round 1: its old "stopped" hid a running guest).
        if (WslVmState.Running(_windows, guest) == false)
        {
            var note = guest.Value is { } last ? LastSample(guest, last)
                : guest.Problem == ReadingProblem.Missing ? WslNeverReported
                : $"Its last sample can't be read: {guest.Why}";
            Guest.ShowValue(WslNotRunning, note);
            return;
        }
        if (guest.Value is not { } doc)
        {
            var why = guest.Problem == ReadingProblem.Missing ? WslNeverReported : guest.Why ?? "The WSL snapshot could not be read.";
            Guest.ShowNotAvailable(why, gauge: true);
            return;
        }
        // The guest's own figures: what the guest has free of what it has, never the VM's cost to Windows.
        switch (doc.Figures(out var available, out var total))
        {
            case MemoryFigure.NotMeasured:
                Guest.ShowNotAvailable("The WSL logger did not measure the guest's total and available memory.", gauge: true, NotCurrent(guest, "WSL"));
                return;
            case MemoryFigure.Impossible:
                Guest.ShowNotAvailable(Impossible("WSL", available, total), gauge: true, NotCurrent(guest, "WSL"));
                return;
        }
        Guest.ShowGaugeOf(available / 1024.0, total / 1024.0, "Free", StatusKind.Neutral, Room(available, total), NotCurrent(guest, "WSL"));
    }

    /// <summary>The words for a sample whose available memory is more than its total: a logger fault, never a reading.</summary>
    public static string Impossible(string side, long available, long total) => SnapshotDoc.ImpossibleWords(side, available, total);

    /// <summary>A stopped VM's last sample, with its age: "Last sample 12 minutes ago: 6.0 GB available of 14.0 GB".</summary>
    private static string LastSample(Reading<SnapshotDoc> reading, SnapshotDoc doc)
    {
        var age = Age(reading.Age ?? TimeSpan.Zero);
        // Only figures that can be: a sample with more available than total prints none (a logger fault, never a reading).
        return doc.Figures(out var available, out var total) == MemoryFigure.Valid
            ? $"Last sample {age} ago: {Room(available, total)}."
            : $"Last sample {age} ago.";
    }

    private void DrawQueue()
    {
        if (_status is not { } reading) return;
        if (reading.Value is not { } status)
        {
            Queue.ShowNotAvailable(reading.Why ?? "The queue could not be read.", gauge: false);
            return;
        }
        var states = status.Slots.Select(QueueSlotTile.StateOf).ToList();
        var inUse = states.Count(s => s is SlotState.Running or SlotState.Starting or SlotState.WaitingForGuest or SlotState.Held);
        var unknown = states.Count(s => s == SlotState.NotAvailable);
        var slots = states.Count == 1 ? "slot" : "slots";
        var words = QueueSummary.WordsOf(status);
        if (unknown > 0)
        {
            // A slot that could not be checked is not a free one: no definite count, and no gauge of one.
            var notKnown = string.Create(CultureInfo.InvariantCulture, $"{unknown} of {states.Count} {slots} not known");
            Queue.ShowValue(words, inUse > 0 ? string.Create(CultureInfo.InvariantCulture, $"At least {inUse} in use, {notKnown}") : notKnown);
            return;
        }
        var note = string.Create(CultureInfo.InvariantCulture, $"{inUse} of {states.Count} {slots} in use");
        if (states.Count == 0) Queue.ShowValue(words, note);
        else Queue.ShowWordsWithGauge(words, inUse, states.Count, "in use", inUse == 0 ? "Idle" : inUse == states.Count ? "Full" : "Busy",
            inUse == states.Count ? StatusKind.Warn : StatusKind.Neutral, note);
    }

    /// <summary>
    /// Why an instances scan has no card value, a reason each: every source's notice, and the reader's own words for WSL
    /// (no distro has reported, or its folder can't be listed) with their recovery words.
    /// </summary>
    private static List<string> InstanceReasons(Scan scan)
    {
        var reasons = InstancesPage.NoticesOf(scan);
        if (scan.WslNotice is { } wsl) reasons.Add(wsl);
        return reasons.Count > 0 ? reasons : ["Nothing could be read."];
    }

    private void DrawInstances()
    {
        if (!_scanAnswered) return;
        if (_scan is not { } scan)
        {
            Instances.ShowNotAvailable(_scanFailure ?? "The instances could not be read.", gauge: false);
            return;
        }
        var notices = InstancesPage.NoticesOf(scan);
        // A count needs a current side: stale reports and failed reads together establish nothing, so the card says
        // why instead of a measured 0 (round 1).
        if (!scan.Sides.Any(s => s.Reading is { HasValue: true, IsStale: false }))
        {
            Instances.ShowNotAvailable(string.Join(" ", InstanceReasons(scan)), gauge: false);
            return;
        }
        // The merged live instances, each once: a WSL job seen by both sides is one, an editor with several open windows
        // (a row each, D03 T02 §4) is one, and a stale distro's rows are not counted (Groups never lists them).
        var live = scan.Groups.SelectMany(g => g.Rows).DistinctBy(InstanceLine.ProcessKey).ToList();
        var bySide = new List<string>();
        void Side(string name)
        {
            var n = live.Count(row => InstanceLine.SideOf(row) == name);
            bySide.Add(string.Create(CultureInfo.InvariantCulture, $"{name} {n}"));
        }
        foreach (var side in scan.Sides.Where(s => s.Reading is { HasValue: true, IsStale: false })) Side(side.Name);
        // A row whose side is not a fresh side of its own (a WSL holder no distro report joined) is still counted, by its name.
        foreach (var other in live.Select(InstanceLine.SideOf).Distinct().Where(name => !scan.Sides.Any(s => s.Reading is { HasValue: true, IsStale: false } && s.Name == name)))
            Side(other);
        var note = live.Count == 0 && notices.Count == 0 && scan.WslNotice is null ? "Nothing is running on either side." : string.Join(", ", bySide);
        // No distro's report was read (none written, or the folder could not be listed): the reader's own words, which
        // say which and what to do.
        var warnings = scan.WslNotice is null ? notices : [.. notices, scan.WslNotice];
        Instances.ShowValue(live.Count.ToString(CultureInfo.InvariantCulture), note, string.Join(" ", warnings));
    }
}
