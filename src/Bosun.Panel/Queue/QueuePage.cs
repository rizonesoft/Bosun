using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Bosun.Panel.Controls;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Shell;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Queue;

/// <summary>
/// One finished job as the history table shows it (D03 T02 §3): every cell in words, and the numbers Slot, Waited,
/// Ran, Exit and Ended sort by. The exit code and how the job was admitted are apart: a job whose slot wait timed out
/// ran without a slot and still has its own exit code. Slots are numbered from one, as the tiles number them.
/// </summary>
public sealed class HistoryLine
{
    public const string NotKnown = "Not known";

    /// <summary>
    /// An exit code in words: a small one as its number; a Windows status (a negative 32-bit code, such as -1073741510)
    /// in hex as Windows writes it, with its name where it is a common one, so "stopped by Ctrl+C" is read, not a number.
    /// </summary>
    public static string ExitWords(long code)
    {
        if (code >= 0 || code < int.MinValue) return code.ToString(CultureInfo.InvariantCulture);
        var status = unchecked((uint)(int)code);
        var hex = string.Create(CultureInfo.InvariantCulture, $"0x{status:X8}");
        return status switch
        {
            0xC000013A => hex + " (stopped by Ctrl+C)",
            0xC0000005 => hex + " (access violation)",
            0xC00000FD => hex + " (stack overflow)",
            0xC0000409 => hex + " (stack buffer overrun)",
            0xC0000017 => hex + " (out of memory)",
            0xC0000142 => hex + " (could not start)",
            _ => hex,
        };
    }

    public const string TimedOut = "Ran without a slot: the wait timed out";

    public const string Cancelled = "Cancelled while waiting";

    public HistoryLine(JobLineDoc job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Job = job;
        Command = job.Command;
        Repo = QueueWords.Repo(job.Repo);
        Side = QueueWords.Side(job.Side);
        SlotSort = job.Slot is { } slot ? slot + 1 : null;
        Slot = SlotSort is { } n ? n.ToString(CultureInfo.InvariantCulture) : "None";
        WaitedSeconds = job.WaitedS;
        Waited = Seconds(job.WaitedS);
        RanSeconds = job.RanS;
        Ran = Seconds(job.RanS);
        ExitSort = job.ExitCode;
        // The cell holds the code (a Windows status in hex fits it); its name is in the cell's tooltip and spoken words.
        Exit = job.ExitCode is { } code ? ExitWords(code).Split(' ')[0] : NotKnown;
        ExitWhy = job.ExitCode is { } said ? $"Exit code {ExitWords(said)}" : "Not known: the job's exit code was not recorded (a WSL job's may not be)";
        Admission = job.TimedOutIntoQueue ? TimedOut : SlotSort is { } given ? string.Create(CultureInfo.InvariantCulture, $"Ran in slot {given}") : Cancelled;
        EndedSort = QueueClient.EndOf(job);
        Ended = DateTimeOffset.TryParse(job.EndedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ended)
            ? ended.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : job.EndedAt;
    }

    public JobLineDoc Job { get; }

    public string Command { get; }

    public string Repo { get; }

    public string Side { get; }

    public string Slot { get; }

    public int? SlotSort { get; }

    public string Waited { get; }

    public double WaitedSeconds { get; }

    public string Ran { get; }

    public double RanSeconds { get; }

    public string Exit { get; }

    public long? ExitSort { get; }

    /// <summary>Whether Exit shows a word, not a number: drawn dim, with <see cref="ExitWhy"/>.</summary>
    public bool ExitIsWord => ExitSort is null;

    public string ExitWhy { get; }

    public string Admission { get; }

    public string Ended { get; }

    public long EndedSort { get; }

    /// <summary>A length in seconds as the table says it: "0.5 s", "12 s", "4 min".</summary>
    public static string Seconds(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) return NotKnown;
        if (seconds < 60) return seconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";
        return QueueWords.Elapsed((long)Math.Round(seconds)) ?? NotKnown;
    }
}

/// <summary>What a reading of the limits may do to a field's text.</summary>
internal enum TextRule
{
    /// <summary>Keep the person's edit and what was said about it; a field with neither shows the value read.</summary>
    KeepEdit,

    /// <summary>Show the value read: a save or reset whose value the field still holds as submitted.</summary>
    Replace,

    /// <summary>Touch no text: a reading that did not read the file confirms nothing a field holds.</summary>
    KeepText,
}

/// <summary>
/// One limit of the form (D03 T02 §3): what the person has typed, what is in force and where it comes from, its range,
/// and why it can't be saved as typed. A field set by a valid variable is read-only; one whose variable is ignored as
/// invalid says so and stays editable. The tool owns the range: a value out of it is refused by <c>bosun config set</c>
/// with its own words, shown beside the field; here only what is not a whole number is refused before a save.
/// </summary>
public sealed class LimitField : Observable
{
    private readonly Action _edited;
    private string _text = "", _baseline = "", _source = "Not available: the limits have not been read", _range = "";
    private string? _error, _variableNote;
    private bool _readOnly, _known;

    internal LimitField(string key, string label, string unit, string? hint, Action edited)
    {
        Key = key;
        Label = label;
        Unit = unit;
        Hint = hint;
        _edited = edited;
    }

    /// <summary>Its key in <c>config.json</c>: slots, min_avail_mb, max_wait_s or min_guest_mb.</summary>
    public string Key { get; }

    public string Label { get; }

    /// <summary>"MB", "seconds" or empty.</summary>
    public string Unit { get; }

    /// <summary>A fixed word about the limit ("0 means no limit"), or null.</summary>
    public string? Hint { get; }

    /// <summary>What a screen reader calls the field: its label with its unit.</summary>
    public string SpokenName => Unit.Length == 0 ? Label : $"{Label}, in {Unit}";

    /// <summary>The value as the person has it.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value ?? "")) return;
            // A new edit replaces what was said about the last one: the tool's refusal, or that it did not read back.
            Error = Fault(out _);
            Raise(nameof(IsDirty));
            _edited();
        }
    }

    /// <summary>Why it can't be saved as it is: not a whole number, or the tool's own refusal; null when it may be.</summary>
    public string? Error
    {
        get => _error;
        internal set
        {
            if (Set(ref _error, value)) Raise(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>Where the value in force comes from: saved, the default, or a variable.</summary>
    public string Source
    {
        get => _source;
        private set
        {
            if (Set(ref _source, value)) Raise(nameof(Detail));
        }
    }

    /// <summary>"From 1 to 64".</summary>
    public string Range
    {
        get => _range;
        private set
        {
            if (Set(ref _range, value)) Raise(nameof(Detail));
        }
    }

    /// <summary>The range and the source, as the line under the field says them: "From 1 to 64. Saved: 3."</summary>
    public string Detail => (_range.Length == 0 ? "" : _range + ". ") + _source.TrimEnd('.') + ".";

    /// <summary>An ignored invalid variable, with the tool's words; null when there is none.</summary>
    public string? VariableNote { get => _variableNote; private set => Set(ref _variableNote, value); }

    /// <summary>Set by a valid variable: the variable wins, so the field is not edited here.</summary>
    public bool IsReadOnly
    {
        get => _readOnly;
        private set
        {
            if (Set(ref _readOnly, value)) RaiseEditable();
        }
    }

    private void RaiseEditable()
    {
        Raise(nameof(IsEditable));
        Raise(nameof(IsLocked));
    }

    /// <summary>The text box's read-only: not read yet, missing, or set by a variable.</summary>
    public bool IsLocked => !IsEditable;

    /// <summary>Whether the person can type in it: read, and not set by a variable.</summary>
    public bool IsEditable => _known && !_readOnly;

    /// <summary>The value saved in the file; null when none is (the default or a variable is in force).</summary>
    public long? Saved { get; private set; }

    public long Min { get; private set; }

    public long Max { get; private set; }

    /// <summary>What the last reading said is in force, as text: what the field goes back to on Discard.</summary>
    public string Baseline => _baseline;

    /// <summary>Whether the person has changed it from what was read.</summary>
    public bool IsDirty => _known && !_readOnly && _text.Trim() != _baseline;

    /// <summary>Per field: back to the variable or the default (<c>bosun config unset</c>).</summary>
    public ICommand? UseDefault { get; internal set; }

    public string UseDefaultName => $"Use the default for {Label}";

    /// <summary>The variable that can set it (BOSUN_SLOTS); empty until the limits are read.</summary>
    public string Variable { get; private set; } = "";

    public const string UseDefaultDescription = "Removes the saved value, so the variable or the default applies";

    /// <summary>Why Use the default can't run now, from the page; null when it can.</summary>
    internal Func<string?>? WhyNoDefault { get; set; }

    /// <summary>Why Use the default can't run now, or null when it can: the palette's reason and the button's tooltip.</summary>
    public string? UseDefaultReason => WhyNoDefault?.Invoke();

    /// <summary>The button's tooltip: why it can't run while it can't, else what it does.</summary>
    public string UseDefaultTip => UseDefaultReason ?? UseDefaultDescription;

    internal void RefreshTip() => Raise(nameof(UseDefaultTip));

    /// <summary>Why the text is not a value to send, or null with the value.</summary>
    internal string? Fault(out long value)
    {
        value = 0;
        var trimmed = _text.Trim();
        var range = string.Create(CultureInfo.InvariantCulture, $"from {Min} to {Max}");
        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit)) return $"Enter a whole number, {range}.";
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value))
            return string.Create(CultureInfo.InvariantCulture, $"That is more than the most it may be, {Max}.");
        return null;
    }

    /// <summary>
    /// A reading of the limit: its range, source and value in force. The text follows <paramref name="rule"/>: an edit
    /// kept, the value read shown, or no text touched; a field set by a variable always shows the variable's value.
    /// </summary>
    internal void Take(LimitDoc limit, bool fileTrusted, TextRule rule)
    {
        var edited = IsDirty || _error is not null;
        _known = true;
        Min = limit.Min;
        Max = limit.Max;
        Saved = fileTrusted ? limit.Saved : null;
        Range = string.Create(CultureInfo.InvariantCulture, $"From {limit.Min} to {limit.Max}");
        IsReadOnly = limit.Source == "environment";
        Source = limit.Source switch
        {
            "environment" => $"Set by {limit.Variable}, so it can't be changed here",
            "file" => string.Create(CultureInfo.InvariantCulture, $"Saved: {limit.Value}"),
            "default" when fileTrusted => string.Create(CultureInfo.InvariantCulture, $"The default, {limit.Default}"),
            "default" => string.Create(CultureInfo.InvariantCulture, $"The default in force, {limit.Default}, not a saved value"),
            _ => $"From the {limit.Source}",
        };
        VariableNote = limit.VariableError is { } bad ? $"{limit.Variable} is ignored: {bad}" : null;
        Variable = limit.Variable;
        _baseline = limit.Value.ToString(CultureInfo.InvariantCulture);
        Raise(nameof(Baseline));
        RaiseEditable();
        if (!_readOnly && (rule == TextRule.KeepText || (rule == TextRule.KeepEdit && edited)))
        {
            Raise(nameof(IsDirty));
            return;
        }
        Reset();
    }

    /// <summary>A limit this bosun.exe does not have: not shown as a value, and not edited.</summary>
    internal void Missing()
    {
        _known = false;
        Saved = null;
        Source = "Not available: this bosun.exe does not report this limit";
        Range = "";
        VariableNote = null;
        RaiseEditable();
        Raise(nameof(IsDirty));
    }

    /// <summary>Back to the value in force, with nothing said about an edit.</summary>
    internal void Reset()
    {
        if (Set(ref _text, _baseline, nameof(Text))) Raise(nameof(IsDirty));
        Error = null;
        Raise(nameof(IsDirty));
    }
}

/// <summary>
/// The Queue page (D03 T02 §3, design/SPEC.md §5): who holds the build slots and what they are doing, who waits and
/// why, the finished jobs, and the limits, read every 5 s while it is on screen through <see cref="IQueueSource"/>. A
/// part that can't be read is said where it would be, and the others stay: only nothing readable at all is the failed
/// state. An idle queue keeps its history, free slots and form on screen. The limits form saves through
/// <c>bosun config</c>, which every <c>bosun run</c> reads, so a saved limit governs the next job.
/// </summary>
public sealed class QueuePage : PageBase
{
    public const string Purpose = "Who holds the build slots, who is waiting, and the limits.";

    // What each limit does, as bosun's admission does it (src/app.cpp acquire_slot, dm::slot_admits; wsl/bosun's guest
    // wait): the first slot has no memory floor, and a positive maximum wait lets a job start without a slot.
    public const string SlotsHint = "How many build jobs run at once.";

    public const string FloorHint = "The first slot has no floor: a job takes any later slot only while this much memory is free.";

    public const string WaitHint = "A job waiting longer starts without a slot, past the slot and memory limits. 0 means it waits with no limit.";

    public const string GuestHint = "A WSL job in a slot after the first starts only while the WSL guest has this much free, or once the maximum wait runs out.";

    /// <summary>The idle queue's words, in the live area: the history and the form stay beside it.</summary>
    public const string IdleNote = "Nothing is running or waiting. Jobs started with bosun run show here.";

    /// <summary>How many finished jobs the history shows.</summary>
    public const int HistoryCount = 200;

    /// <summary>The form's fields in the order a save sends them.</summary>
    public static readonly IReadOnlyList<string> SaveOrder = ["slots", "min_avail_mb", "max_wait_s", "min_guest_mb"];

    private readonly IQueueSource _source;
    private readonly ActivityLog _log;
    private readonly AsyncCommand _save, _discard;
    private long _asked, _statusSeq, _historySeq, _limitsSeq;
    private bool _formReady, _saving;
    private StatusDoc? _status;
    private string? _statusNotice, _liveWords, _idle, _historyNotice, _limitsNotice, _fileNotice, _formNotice, _saveStatus;

    public QueuePage(IQueueSource source, RefreshScheduler scheduler, ActivityLog log) : base("Queue", Purpose)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        Words = new QueueSummary(source, log);
        Fields =
        [
            new LimitField("slots", "Slots", "", SlotsHint, Edited),
            new LimitField("min_avail_mb", "Memory floor for each slot after the first", "MB", FloorHint, Edited),
            new LimitField("max_wait_s", "Maximum wait", "seconds", WaitHint, Edited),
            new LimitField("min_guest_mb", "Guest memory floor", "MB", GuestHint, Edited),
        ];
        foreach (var field in Fields)
        {
            field.UseDefault = UseDefaultCommand(field);
            var each = field;
            field.WhyNoDefault = () => UseDefaultReason(each);
        }
        _save = log.Command("Save queue limits", SaveAsync, () => SaveReason() is null);
        _discard = log.Command("Discard queue limit changes", () =>
        {
            Discard();
            return Task.FromResult(ActionOutcome.Done);
        }, () => HasUnsavedEdits && !_saving);
        RefreshEvery(scheduler, log, RefreshScheduler.Fast, ReadAsync);
    }

    /// <summary>The tray's Queue line, sharing this page's readings while it is on screen.</summary>
    public QueueSummary Words { get; }

    /// <summary>The status last read, for the waiting list; null while it can't be read.</summary>
    public StatusDoc? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>A slot each, free ones included, in slot order.</summary>
    public ObservableCollection<SlotDoc> Slots { get; } = [];

    /// <summary>What the status could not tell, a line each, beside the jobs that were read.</summary>
    public ObservableCollection<string> Doubts { get; } = [];

    /// <summary>Why the status could not be read, above the live area; null when it was.</summary>
    public string? StatusNotice { get => _statusNotice; private set => Set(ref _statusNotice, value); }

    /// <summary>The queue in words ("1 running, 2 waiting"); null while it can't be read or is idle.</summary>
    public string? LiveWords { get => _liveWords; private set => Set(ref _liveWords, value); }

    /// <summary><see cref="IdleNote"/> when the queue is idle, else null.</summary>
    public string? Idle { get => _idle; private set => Set(ref _idle, value); }

    /// <summary>The finished jobs, newest first.</summary>
    public ObservableCollection<HistoryLine> History { get; } = [];

    /// <summary>What the history could not read, or that there is none yet; null when there is nothing to say.</summary>
    public string? HistoryNotice { get => _historyNotice; private set => Set(ref _historyNotice, value); }

    /// <summary>Why the limits could not be read, above the form; null when they were.</summary>
    public string? LimitsNotice { get => _limitsNotice; private set => Set(ref _limitsNotice, value); }

    /// <summary>The limits file's own problems (unreadable, invalid, refused values), above the form.</summary>
    public string? FileNotice { get => _fileNotice; private set => Set(ref _fileNotice, value); }

    /// <summary>What the last save could not do for the form as a whole: a busy or damaged file, a read-back that failed.</summary>
    public string? FormNotice { get => _formNotice; private set => Set(ref _formNotice, value); }

    /// <summary>What the last save did.</summary>
    public string? SaveStatus { get => _saveStatus; private set => Set(ref _saveStatus, value); }

    public IReadOnlyList<LimitField> Fields { get; }

    public LimitField Field(string key) => Fields.Single(f => f.Key == key);

    /// <summary>Whether the limits have been read, so the form can be edited.</summary>
    public bool FormReady { get => _formReady; private set => Set(ref _formReady, value); }

    public bool IsSaving => _saving;

    public ICommand Save => _save;

    public ICommand DiscardChanges => _discard;

    public override bool HasUnsavedEdits => _formReady && Fields.Any(f => f.IsDirty);

    protected override IEnumerable<PageCommand> Commands
    {
        get
        {
            if (State != PageState.Data) yield break;
            yield return new PageCommand("Save queue limits", GlyphAction.Save, _save) { Reason = SaveReason };
            yield return new PageCommand("Discard queue limit changes", GlyphAction.Discard, _discard) { Reason = () => _saving ? Saving : "There are no changes to discard" };
            foreach (var limit in Fields)
                if (limit.UseDefault is { } reset) yield return new PageCommand(limit.UseDefaultName, GlyphAction.Discard, reset) { Reason = () => limit.UseDefaultReason };
        }
    }

    public const string SaveDescription = "Saves the changed limits through bosun config and reads them back";

    /// <summary>The Save button's tooltip: why it can't run while it can't, else what it does.</summary>
    public string SaveTip => SaveReason() ?? SaveDescription;

    /// <summary>Why <paramref name="field"/>'s Use the default can't run now, or null when it can.</summary>
    public string? UseDefaultReason(LimitField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (_saving) return Saving;
        if (!_formReady) return NotRead;
        if (_limitsStale is { } stale) return stale;
        if (_fileRefusesSave is { } refuses) return refuses;
        if (!field.IsEditable) return field.IsReadOnly ? $"{field.Label} is set by {field.Variable}, so it can't be changed here" : $"{field.Label} is not available";
        if (field.Saved is null) return $"{field.Label} is already the default";
        return null;
    }

    public const string Saving = "A save of the limits is under way";

    public const string NothingEditable = "Every limit is set by a variable, so none can be changed here";

    public const string NotRead = "The limits could not be read, so there is nothing to save";

    public const string NoChanges = "There are no changes to save";

    /// <summary>Why bosun will not write the limits file now (it can't be read or is damaged); null when it will.</summary>
    private string? _fileRefusesSave;

    /// <summary>After a good read, why the limits can't be read now and when they last were; null while they are current.</summary>
    private string? _limitsStale;

    /// <summary>Why Save can't run now, or null when it can: the button, Ctrl+K and the question asked on leaving alike.</summary>
    public string? SaveReason()
    {
        if (_saving) return Saving;
        if (!_formReady) return NotRead;
        if (_limitsStale is { } stale) return stale;
        if (_fileRefusesSave is { } refuses) return refuses;
        if (Fields.All(f => !f.IsEditable)) return NothingEditable;
        if (!HasUnsavedEdits) return NoChanges;
        return null;
    }

    protected override void Shown() => Words.PageShowing = true;

    protected override void Hidden() => Words.PageShowing = false;

    private void Edited()
    {
        Raise(nameof(HasUnsavedEdits));
        Raise(nameof(SaveTip));
        _save.Refresh();
        _discard.Refresh();
        foreach (var field in Fields)
        {
            (field.UseDefault as AsyncCommand)?.Refresh();
            field.RefreshTip();
        }
    }

    // ---- reading ----

    private async Task<Action> ReadAsync(CancellationToken token)
    {
        // Numbered as it starts: a reading that ends after a newer one (a save's read-back) is not applied over it.
        var seq = ++_asked;
        var ticket = Words.Begin();     // the tray's words take this reading only if no newer one was applied
        var status = _source.StatusAsync(token);
        var history = _source.HistoryAsync(HistoryCount, token);
        var limits = _source.LimitsAsync(token);
        await Task.WhenAll(status, history, limits);
        var (s, h, l) = (status.Result, history.Result, limits.Result);
        return () => Apply(seq, ticket, s, h, l);
    }

    private void Apply(long seq, long ticket, Reading<StatusDoc> status, Reading<JobHistory> history, Reading<ConfigDoc> limits)
    {
        if (seq > _statusSeq)
        {
            _statusSeq = seq;
            TakeStatus(status, ticket);
        }
        if (seq > _historySeq)
        {
            _historySeq = seq;
            TakeHistory(history);
        }
        if (seq > _limitsSeq)
        {
            _limitsSeq = seq;
            TakeLimits(limits, _ => TextRule.KeepEdit);
        }
        if (!status.HasValue && !history.HasValue && !limits.HasValue)
        {
            var whys = new[] { status.Why, history.Why, limits.Why }.Where(w => w is not null).Select(w => w!).Distinct().ToList();
            ShowFailed(string.Join(Environment.NewLine, whys.Count > 0 ? whys : ["Nothing of the queue could be read."]));
            return;
        }
        ShowData();
    }

    private void TakeStatus(Reading<StatusDoc> reading, long ticket)
    {
        Words.Take(reading, ticket);
        var doc = reading.Value;
        Status = doc;
        Slots.Clear();
        Doubts.Clear();
        if (doc is null)
        {
            StatusNotice = $"The queue could not be read: {reading.Why}";
            LiveWords = null;
            Idle = null;
            Waiting = 0;
            return;
        }
        StatusNotice = null;
        foreach (var slot in doc.Slots.OrderBy(s => s.Index)) Slots.Add(slot);
        foreach (var doubt in QueueSummary.Doubts(doc)) Doubts.Add(doubt);
        var words = QueueSummary.WordsOf(doc);
        Idle = words == QueueSummary.Idle ? IdleNote : null;
        LiveWords = Idle is null ? words : null;
        Waiting = doc.Records.Readable ? doc.Waiters.Count : 0;
    }

    private void TakeHistory(Reading<JobHistory> reading)
    {
        if (reading.Value is not { } history)
        {
            // What was shown stays out: an old list would read as current.
            History.Clear();
            HistoryNotice = $"The finished jobs could not be read: {reading.Why}";
            return;
        }
        History.Clear();
        foreach (var job in history.Jobs) History.Add(new HistoryLine(job));
        var said = new List<string>();
        if (history.Unreadable.Count > 0) said.Add("These job logs could not be read, so jobs may be missing: " + string.Join("; ", history.Unreadable) + ".");
        if (history.PassedOver > 0)
            said.Add(string.Create(CultureInfo.InvariantCulture,
                $"{history.PassedOver} {(history.PassedOver == 1 ? "line of the job logs is" : "lines of the job logs are")} not a job record and {(history.PassedOver == 1 ? "was" : "were")} passed over."));
        if (history.Jobs.Count == 0 && said.Count == 0) said.Add("No job has finished yet: each one that does is listed here.");
        HistoryNotice = said.Count == 0 ? null : string.Join(" ", said);
    }

    /// <param name="rule">What the reading may do to each field's text, decided now, as it is applied.</param>
    private void TakeLimits(Reading<ConfigDoc> reading, Func<LimitField, TextRule> rule)
    {
        if (reading.Value is not { } doc)
        {
            if (_formReady && _lastRead is { } at)
            {
                // After a good read: the fields keep the values last read, said as such, and nothing is written over a file
                // that can't be read now until a fresh read says what it holds.
                _limitsStale = $"Last read at {at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)}; the limits can't be read now: {reading.Why}";
                LimitsNotice = _limitsStale;
            }
            else LimitsNotice = $"The limits could not be read: {reading.Why}";
            Edited();
            return;
        }
        _limitsStale = null;
        _lastRead = reading.ReadAt ?? Clock.GetUtcNow();
        LimitsNotice = null;
        var trusted = doc.Readable && doc.Valid;
        var problems = new List<string>();
        if (!doc.Readable) problems.Add($"The limits file {doc.Path} can't be read: {doc.Error ?? "no reason given"}.");
        else if (!doc.Valid) problems.Add($"The limits file {doc.Path} is not valid: {doc.Error ?? "no reason given"}.");
        foreach (var refused in doc.Refused) problems.Add($"Its {refused.Key} was refused: {refused.Error}.");
        // bosun will not overwrite a file it can't read or that is damaged (exit 74): saving waits on the person fixing
        // or removing it, and each field still says where its own value comes from.
        _fileRefusesSave = trusted ? null : $"bosun will not overwrite {doc.Path} while it {(doc.Readable ? "is damaged" : "can't be read")}: fix or remove it, then save.";
        if (_fileRefusesSave is { } refuses) problems.Add(refuses);
        FileNotice = problems.Count == 0 ? null : string.Join(" ", problems);
        foreach (var field in Fields)
        {
            // A reading that did not read the file replaces no text: its values are defaults, not what the file holds.
            if (doc.Limits.TryGetValue(field.Key, out var limit)) field.Take(limit, trusted, trusted ? rule(field) : TextRule.KeepText);
            else field.Missing();
        }
        FormReady = true;
        Edited();
    }

    private DateTimeOffset? _lastRead;

    /// <summary>
    /// A save's or reset's read-back, applied unless a newer reading was. <paramref name="replaces"/> is asked, as it is
    /// applied and not before it was awaited, which fields may show the value read: a field edited while it was pending
    /// keeps the person's text.
    /// </summary>
    private async Task<Reading<ConfigDoc>> ReadBackAsync(Func<LimitField, ConfigDoc, bool> replaces)
    {
        var seq = ++_asked;
        var back = await _source.LimitsAsync();
        if (seq > _limitsSeq)
        {
            _limitsSeq = seq;
            TakeLimits(back, field => back.Value is { } doc && replaces(field, doc) ? TextRule.Replace : TextRule.KeepEdit);
        }
        return back;
    }

    // ---- saving ----

    /// <summary>
    /// The form's save, for its button, Ctrl+K and the question asked on leaving: only the changed fields, in
    /// <see cref="SaveOrder"/>, each through <c>bosun config set</c>, then all four read back and each saved one checked
    /// against it. A field refused keeps the person's text with the tool's words beside it; the fields saved before or
    /// after it are said as saved. A busy or damaged file stops the save there, said above the form. The Activity pane
    /// gets one INFO line for what saved and one WARN line for what was refused or not confirmed.
    /// </summary>
    public override async Task<ActionOutcome> SaveAsync()
    {
        if (_saving) return ActionOutcome.Warning(Saving);
        var changed = Fields.Where(f => f.IsDirty).OrderBy(f => SaveOrder.ToList().IndexOf(f.Key)).ToList();
        if (changed.Count == 0) return ActionOutcome.Done;
        // The button's own eligibility, for every way in (the button, Ctrl+K and the question asked on leaving): a file
        // bosun won't overwrite, or limits that can't be read now, keep the draft and the person here, with why.
        if (SaveReason() is { } why)
        {
            SaveStatus = "Not saved: " + why;
            return ActionOutcome.Warning(why);
        }
        var faults = changed.Where(f => f.Fault(out _) is not null).ToList();
        if (faults.Count > 0)
        {
            foreach (var f in faults) f.Error = f.Fault(out _);
            var said = "Not saved: " + string.Join(" ", faults.Select(f => $"{f.Label}: {f.Error}"));
            SaveStatus = said;
            return ActionOutcome.Warning(said);
        }
        // The values as submitted, validated once and taken now: an edit made while the save runs is never sent.
        var submitted = changed.Select(f =>
        {
            f.Fault(out var value);
            return (Field: f, Value: value, Text: f.Text);
        }).ToList();
        _saving = true;
        Raise(nameof(IsSaving));
        Edited();
        try
        {
            return await SaveChangedAsync(submitted);
        }
        finally
        {
            _saving = false;
            Raise(nameof(IsSaving));
            Edited();
        }
    }

    private async Task<ActionOutcome> SaveChangedAsync(IReadOnlyList<(LimitField Field, long Value, string Text)> submitted)
    {
        var sent = new List<(LimitField Field, long Value)>();
        var problems = new List<string>();
        string? stopped = null;
        var toolFailed = false;
        FormNotice = null;
        foreach (var (field, value, _) in submitted)
        {
            if (stopped is not null)
            {
                problems.Add($"{field.Label} was not saved: {stopped}");
                continue;
            }
            var reading = await _source.SetLimitAsync(field.Key, value);
            if (reading.Value is not { } save)
            {
                toolFailed = true;
                stopped = reading.Why ?? "the tool gave no answer";
                problems.Add($"{field.Label} was not saved: {stopped}");
                continue;
            }
            switch (save.Outcome)
            {
                case LimitSaveOutcome.Saved:
                    sent.Add((field, value));
                    break;
                case LimitSaveOutcome.Refused:
                    field.Error = save.Message;
                    problems.Add($"{field.Label}: {save.Message}");
                    break;
                case LimitSaveOutcome.Busy:
                    stopped = $"another save held the limits file, so nothing more was changed ({save.Message})";
                    problems.Add($"{field.Label} was not saved: {stopped}");
                    break;
                default:
                    // Exit 74: the tool's own words name what failed (a damaged file it won't overwrite, a full disk, a
                    // temporary file refused, a replace blocked by a lock); only a read says the file is damaged.
                    stopped = $"bosun did not write the limits file: {save.Message.TrimEnd('.')}";
                    problems.Add($"{field.Label} was not saved: {stopped}");
                    break;
            }
        }
        if (stopped is not null) FormNotice = $"Not every limit was saved: {stopped}.";

        // Read back whatever happened: a refusal of one field leaves the others saved, and the file is what says so. Which
        // fields show the value read is decided as it is applied: a field confirmed and still holding what was submitted.
        var texts = submitted.ToDictionary(x => x.Field, x => x.Text);
        var values = sent.ToDictionary(x => x.Field, x => x.Value);
        var back = await ReadBackAsync((field, doc) => values.TryGetValue(field, out var value) && field.Text == texts[field]
            && doc.Limits.GetValueOrDefault(field.Key) is { Saved: { } read } && read == value);
        var confirmed = new List<string>();
        if (back.Value is { Readable: false } or { Valid: false })
        {
            // A get can answer and still not have read the file: nothing it says confirms a save.
            var why = back.Value.Error ?? "the limits file could not be read";
            if (sent.Count > 0)
            {
                FormNotice = $"The limits file could not be read back, so what was saved is not confirmed: {why}";
                problems.Add($"not confirmed, since the limits file could not be read back ({why}): {string.Join(", ", sent.Select(s => Named(s.Field, s.Value)))}");
            }
        }
        else if (back.Value is not { } doc)
        {
            if (sent.Count > 0)
            {
                FormNotice = $"The limits could not be read back, so what was saved is not confirmed: {back.Why}";
                problems.Add($"not confirmed, since the limits could not be read back ({back.Why}): {string.Join(", ", sent.Select(s => Named(s.Field, s.Value)))}");
            }
        }
        else
        {
            foreach (var (field, value) in sent)
            {
                var limit = doc.Limits.GetValueOrDefault(field.Key);
                if (limit is { Saved: { } saved } && saved == value) confirmed.Add(Named(field, value));
                else
                {
                    var read = limit?.Saved is { } other ? other.ToString(CultureInfo.InvariantCulture) : "nothing saved";
                    // The person's newest text stays (the read-back replaced it only where it was confirmed), with why.
                    field.Error = $"Saved, but read back as {read}, so it is not confirmed.";
                    problems.Add($"{field.Label} read back as {read}, not {value}");
                }
            }
        }

        var done = confirmed.Count == 0 ? null : "Saved and read back: " + string.Join(", ", confirmed) + ".";
        if (problems.Count == 0)
        {
            SaveFailure = null;
            SaveStatus = done;
            return ActionOutcome.DoneWith(string.Join(", ", confirmed));
        }
        // Both lines: what was saved is INFO, and what was not is the outcome's WARN (or FAIL when the tool failed).
        if (done is not null) _log.Info($"Queue limits saved and read back: {string.Join(", ", confirmed)}");
        var reason = string.Join("; ", problems) + ".";
        SaveStatus = (done is null ? "" : done + " ") + "Not saved: " + reason;
        return toolFailed ? ActionOutcome.Failed(reason) : ActionOutcome.Warning(reason);
    }

    private static string Named(LimitField field, long value) =>
        string.Create(CultureInfo.InvariantCulture, $"{field.Key} {value}");

    private AsyncCommand UseDefaultCommand(LimitField field) => _log.Command(field.UseDefaultName, async () =>
    {
        _saving = true;
        Raise(nameof(IsSaving));
        Edited();
        try
        {
            var before = field.Text;
            var reading = await _source.UnsetLimitAsync(field.Key);
            if (reading.Value is not { } unset) return ActionOutcome.Failed(reading.Why ?? "the tool gave no answer");
            if (unset.Outcome != LimitSaveOutcome.Saved)
            {
                FormNotice = $"{field.Label} was not put back to its default: {unset.Message}";
                return ActionOutcome.Warning(unset.Message);
            }
            var back = await ReadBackAsync((f, doc) => f == field && f.Text == before && doc.Limits.GetValueOrDefault(f.Key) is { Saved: null });
            if (back.Value is { Readable: false } or { Valid: false })
            {
                // A get can answer and still not have read the file: its null saved value confirms nothing.
                var why = back.Value.Error ?? "the limits file could not be read";
                FormNotice = $"The limits file could not be read back, so the default for {field.Label} is not confirmed: {why}";
                return ActionOutcome.Warning($"not confirmed, since the limits file could not be read back ({why})");
            }
            if (back.Value is not { } doc)
            {
                FormNotice = $"The limits could not be read back, so the default for {field.Label} is not confirmed: {back.Why}";
                return ActionOutcome.Warning($"not confirmed, since the limits could not be read back ({back.Why})");
            }
            if (doc.Limits.GetValueOrDefault(field.Key) is not { Saved: null } now)
                return ActionOutcome.Warning($"{field.Key} is still saved in the file after it was unset");
            FormNotice = null;
            return ActionOutcome.DoneWith(string.Create(CultureInfo.InvariantCulture, $"{field.Key} {now.Value} ({(now.Source == "environment" ? $"from {now.Variable}" : "the default")})"));
        }
        finally
        {
            _saving = false;
            Raise(nameof(IsSaving));
            Edited();
        }
    }, () => UseDefaultReason(field) is null);

    /// <summary>Back to the values last read: the person's edits and what was said about them go.</summary>
    public override void Discard()
    {
        foreach (var field in Fields) field.Reset();
        SaveStatus = null;
        FormNotice = null;
        SaveFailure = null;
        Edited();
    }
}
