using System.Globalization;
using System.Windows.Input;
using Bosun.Core;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Shell;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Settings;

/// <summary>One choice of theme, with the words the page shows for it.</summary>
public sealed record ThemeOption(ThemeChoice Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Settings page (D02 T02 §5, design/SPEC.md §15, General). Each setting on it has the code that reads it: the
/// theme (the theme service, through the store), Start with Windows (the Run value itself) and memory pressure
/// notifications (the notifier, D02 T02 §7). The others of §15 are added by the sections that build their readers.
///
/// What the person has on the page and what is stored are kept apart: the difference is the page's unsaved edits.
/// Save writes to two places, the settings file and the registry, and one can take while the other does not: what
/// took is then stored and no longer an edit, what did not stays an edit with its reason, and the page may not be
/// left until it is saved or discarded.
/// </summary>
public sealed class SettingsPage : PageBase
{
    private readonly PanelSettingsStore _store;
    private readonly StartupEntry _startup;
    private readonly AsyncCommand _save, _discard, _repair, _readAgain;
    private ICommand? _startupAction;
    private string? _startupActionName;
    private ThemeChoice _storedTheme;
    private bool _storedStart, _storedNotify;
    private ThemeOption _selectedTheme;
    private bool _startWithWindows, _notifyMemoryPressure;
    private string? _notice, _startupNote, _status;
    private long _storedFloor = PanelSettings.DefaultPressureFloorMb;
    private string _floorText = "";
    private string? _floorError;

    public SettingsPage(PanelSettingsStore store, StartupEntry startup, ActivityLog log, Action openStartupSettings, Bosun.Panel.Theme.ThemeService? theme = null)
        : base("Settings", "How the panel starts, looks and notifies.")
    {
        // While Windows draws in a contrast theme, the choice here waits for it to be turned off (D02 T03 §10): said
        // under the theme box, and kept in step as contrast comes and goes.
        if (theme is not null)
        {
            void Say() => ThemeNote = theme.PreviewFile is { } file ? PreviewNote(file) : theme.IsHighContrast ? ContrastNote : null;
            theme.EffectiveChanged += Say;
            Say();
        }
        _store = store;
        _startup = startup;
        _selectedTheme = ThemeOptions[0];
        _save = log.Command("Save settings", SaveAsync, () => HasUnsavedEdits);
        _discard = log.Command("Discard settings changes", () =>
        {
            Discard();
            return Task.FromResult(ActionOutcome.Done);
        }, () => HasUnsavedEdits);
        OpenStartupSettings = log.Command("Open Windows start-up settings", () =>
        {
            openStartupSettings();
            return Task.FromResult(ActionOutcome.Done);
        });
        _repair = log.Command("Point Start with Windows at this copy", () =>
        {
            var outcome = _startup.Set(true);
            ReadStartupAgain();
            return Task.FromResult(outcome.Succeeded ? ActionOutcome.Done : ActionOutcome.Failed(outcome.Reason ?? "The start-up entry was not changed."));
        });
        _readAgain = log.Command("Read the Windows start-up entry again", () =>
        {
            var read = ReadStartupAgain();
            return Task.FromResult(read.State == StartupState.Unavailable ? ActionOutcome.Failed(read.Reason ?? "It could not be read.") : ActionOutcome.Done);
        });
        Retry = log.Command("Read settings again", () =>
        {
            _store.Reload();
            Load();
            return Task.FromResult(State == PageState.Failed ? ActionOutcome.Failed(FailureMessage!) : ActionOutcome.Done);
        });
        Load();
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(ThemeChoice.FollowWindows, "Follow Windows"),
        new(ThemeChoice.Dark, "Dark"),
        new(ThemeChoice.Light, "Light"),
    ];

    /// <summary>Said under the theme box while Windows draws in a contrast theme.</summary>
    public const string ContrastNote = "Windows' contrast theme is in use, so the panel draws in its colours. The theme chosen here applies when it is turned off.";

    /// <summary>What the page says under a contrast preview (D02 T03 §11): where it comes from and how to leave it.</summary>
    public static string PreviewNote(string file) =>
        $"A contrast preview from {file} is in force ({Bosun.Panel.Theme.PreviewContrast.Variable}). The theme chosen here applies when the panel is started without it: exit it from the tray and start it again.";

    private string? _themeNote;

    /// <summary><see cref="ContrastNote"/> while a contrast theme is in force; null otherwise.</summary>
    public string? ThemeNote
    {
        get => _themeNote;
        private set => Set(ref _themeNote, value);
    }

    /// <summary>The theme as the person has it on the page.</summary>
    public ThemeOption SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (value is not null && Set(ref _selectedTheme, value)) Edited();
        }
    }

    /// <summary>Start with Windows as the person has it on the page.</summary>
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (Set(ref _startWithWindows, value)) Edited();
        }
    }

    /// <summary>Memory pressure notifications, on or off, as the person has it on the page (design/SPEC.md §15, §17).</summary>
    public bool NotifyMemoryPressure
    {
        get => _notifyMemoryPressure;
        set
        {
            if (Set(ref _notifyMemoryPressure, value)) Edited();
        }
    }

    /// <summary>
    /// The pressure alert threshold as the person has typed it, in MB (D03 T01 §4, design/SPEC.md §15): read by the
    /// pressure check (src/Bosun.Panel/Tray/PressureCheck.cs) once saved. What can't be a threshold is refused beside
    /// the field (<see cref="PressureFloorError"/>), and no save goes through while it is.
    /// </summary>
    public string PressureFloorText
    {
        get => _floorText;
        set
        {
            if (!Set(ref _floorText, value ?? "")) return;
            PressureFloorError = FloorFault(_floorText, out _);
            Edited();
        }
    }

    /// <summary>Why the threshold as typed can't be saved; null when it can.</summary>
    public string? PressureFloorError
    {
        get => _floorError;
        private set => Set(ref _floorError, value);
    }

    /// <summary>Why <paramref name="text"/> is not a threshold, or null with its value.</summary>
    public static string? FloorFault(string text, out long floor)
    {
        floor = 0;
        var trimmed = text.Trim();
        var range = string.Create(CultureInfo.InvariantCulture, $"from {PanelSettings.MinPressureFloorMb:N0} to {PanelSettings.MaxPressureFloorMb:N0} MB");
        if (trimmed.Length == 0) return $"Enter the threshold in MB, {range}.";
        if (!trimmed.All(char.IsAsciiDigit)) return $"Enter a whole number of MB, {range}.";
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out floor) || floor > PanelSettings.MaxPressureFloorMb)
            return string.Create(CultureInfo.InvariantCulture, $"That is more than the most a threshold may be, {PanelSettings.MaxPressureFloorMb:N0} MB.");
        if (floor < PanelSettings.MinPressureFloorMb)
            return string.Create(CultureInfo.InvariantCulture, $"That is less than the least a threshold may be, {PanelSettings.MinPressureFloorMb:N0} MB.");
        return null;
    }

    /// <summary>Why the settings are not the file's own: it was moved aside, and the defaults are in use.</summary>
    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    /// <summary>
    /// Said when the start-up entry is not simply on or off: Windows has it turned off, it starts another copy of
    /// the panel, or it could not be read again. <see cref="StartupAction"/> is the way out of each.
    /// </summary>
    public string? StartupNote
    {
        get => _startupNote;
        private set => Set(ref _startupNote, value);
    }

    /// <summary>What to do about <see cref="StartupNote"/>, and the words on its button.</summary>
    public ICommand? StartupAction
    {
        get => _startupAction;
        private set => Set(ref _startupAction, value);
    }

    public string? StartupActionName
    {
        get => _startupActionName;
        private set => Set(ref _startupActionName, value);
    }

    /// <summary>What the last save did.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand Save => _save;

    /// <summary>
    /// The page's actions for the command palette (D02 T03 §8), each the command its button runs: Save and Discard,
    /// with why they can't run while there is nothing to save, and the start-up action while one is offered.
    /// </summary>
    protected override IEnumerable<PageCommand> Commands
    {
        get
        {
            if (State != PageState.Data) yield break;
            yield return new PageCommand("Save settings", GlyphAction.Save, _save)
            {
                Reason = () => PressureFloorError is { } error ? error : "There are no changes to save",
            };
            yield return new PageCommand("Discard settings changes", GlyphAction.Discard, _discard) { Reason = () => "There are no changes to discard" };
            if (StartupAction is { } startup && StartupActionName is { } name) yield return new PageCommand(name, GlyphAction.Startup, startup);
        }
    }

    public ICommand DiscardChanges => _discard;

    /// <summary>Opens Windows' own start-up settings, the supported way to turn an entry back on.</summary>
    public ICommand OpenStartupSettings { get; }

    public override bool HasUnsavedEdits => State == PageState.Data && (_selectedTheme.Value != _storedTheme || _startWithWindows != _storedStart || _notifyMemoryPressure != _storedNotify
        || FloorEdited);

    /// <summary>
    /// Whether the threshold differs from what is stored, by its value (round 1: "04096" saved as 4096 stayed an edit
    /// by its text). What can't be a threshold is always an edit: there is something on the page not stored.
    /// </summary>
    private bool FloorEdited => FloorFault(_floorText, out var typed) is not null || typed != _storedFloor;

    private void Edited()
    {
        Raise(nameof(HasUnsavedEdits));
        _save.Refresh();
        _discard.Refresh();
    }

    /// <summary>
    /// Reads what is stored and shows it. Settings or a Run key that could not be read are the page's failed state,
    /// with Retry: nothing is offered for editing that could not be read first.
    /// </summary>
    private void Load()
    {
        var start = _startup.Read();
        if (_store.State == SettingsState.Unavailable)
        {
            ShowFailed(_store.Notice ?? "The settings could not be read.");
            Edited();
            return;
        }
        if (start.State == StartupState.Unavailable)
        {
            ShowFailed(start.Reason ?? "The Windows start-up entry could not be read.");
            Edited();
            return;
        }
        Notice = _store.State == SettingsState.Restored ? _store.Notice : null;
        Take(_store.Current, start);
        ShowData();
        Edited();
    }

    /// <summary>What is stored becomes what the page shows.</summary>
    private void Take(PanelSettings settings, StartupRead start)
    {
        _storedTheme = settings.Theme;
        _storedNotify = settings.NotifyMemoryPressure;
        _storedFloor = settings.PressureFloorMb;
        TakeStart(start);
        SelectedTheme = ThemeOptions.First(option => option.Value == settings.Theme);
        NotifyMemoryPressure = _storedNotify;
        PressureFloorText = _storedFloor.ToString(CultureInfo.InvariantCulture);
        StartWithWindows = _storedStart;
    }

    /// <summary>
    /// What was read of the start-up entry becomes what is stored, and what the page says about it. An entry that
    /// could not be read changes nothing that is stored: the switch goes on showing what was last read, and says so.
    /// </summary>
    private void TakeStart(StartupRead start)
    {
        switch (start.State)
        {
            case StartupState.Unavailable:
                StartupNote = $"{start.Reason} The switch shows what was last read.";
                (StartupAction, StartupActionName) = (_readAgain, "Read again");
                return;
            case StartupState.DisabledByWindows:
                StartupNote = "Windows has Bosun Panel turned off in its start-up settings, so it will not start at sign-in until it is turned on there.";
                (StartupAction, StartupActionName) = (OpenStartupSettings, "Open Startup settings");
                break;
            case StartupState.OtherProgram:
                StartupNote = $"Start with Windows is on for another copy of Bosun Panel ({start.Reason}). Windows would start that one, or nothing if it is gone.";
                (StartupAction, StartupActionName) = (_repair, "Use this copy");
                break;
            default:
                StartupNote = null;
                (StartupAction, StartupActionName) = (null, null);
                break;
        }
        _storedStart = start.Chosen;
    }

    protected override void Shown() => ReadStartupAgain();

    protected override void CameBack() => ReadStartupAgain();

    /// <summary>
    /// The start-up entry can change behind the page's back: it is Windows' own setting, and the page sends the
    /// person to Windows to turn it on. So it is read again whenever the page comes on screen or the person comes
    /// back to the window. What the person has changed on the page and not saved is kept.
    /// </summary>
    private StartupRead ReadStartupAgain()
    {
        var start = _startup.Read();
        if (State != PageState.Data) return start;
        var edited = _startWithWindows != _storedStart;
        TakeStart(start); // an entry that can't be read now is said so, with Read again, and nothing stored changes
        if (!edited) StartWithWindows = _storedStart;
        Edited();
        return start;
    }

    public override Task<ActionOutcome> SaveAsync()
    {
        var saved = new List<string>();
        var failed = new List<string>();

        // A threshold that can't be one is refused before anything is written: nothing of the page is saved, here or
        // through the question asked on leaving it, and the field says why.
        if (_floorError is { } wrong)
        {
            Status = "Not saved: " + wrong;
            return Task.FromResult(ActionOutcome.Failed(wrong));
        }

        if (_selectedTheme.Value != _storedTheme)
        {
            var wanted = _selectedTheme.Value;
            // Only the theme of the latest stored settings: what another part of the panel saved since stays.
            var outcome = _store.Update(current => current with { Theme = wanted });
            if (outcome.Succeeded && _store.Current.Theme == wanted)
            {
                _storedTheme = wanted;
                Notice = null;
                saved.Add($"theme {_selectedTheme.Name}");
            }
            else failed.Add(outcome.Reason ?? "The theme did not read back as it was saved.");
        }

        if (_notifyMemoryPressure != _storedNotify)
        {
            var wanted = _notifyMemoryPressure;
            var outcome = _store.Update(current => current with { NotifyMemoryPressure = wanted });
            if (outcome.Succeeded && _store.Current.NotifyMemoryPressure == wanted)
            {
                _storedNotify = wanted;
                saved.Add($"memory pressure notifications {(wanted ? "on" : "off")}");
            }
            else failed.Add(outcome.Reason ?? "The notification setting did not read back as it was saved.");
        }

        if (FloorFault(_floorText, out var floor) is null && floor != _storedFloor)
        {
            var outcome = _store.Update(current => current with { PressureFloorMb = floor });
            if (outcome.Succeeded && _store.Current.PressureFloorMb == floor)
            {
                _storedFloor = floor;
                PressureFloorText = floor.ToString(CultureInfo.InvariantCulture); // as stored: "04096" reads back as "4096"
                saved.Add(string.Create(CultureInfo.InvariantCulture, $"pressure alert threshold {floor:N0} MB"));
            }
            else failed.Add(outcome.Reason ?? "The pressure alert threshold did not read back as it was saved.");
        }

        if (_startWithWindows != _storedStart)
        {
            var outcome = _startup.Set(_startWithWindows);
            var now = _startup.Read();
            if (outcome.Succeeded && now.State != StartupState.Unavailable && now.Chosen == _startWithWindows)
            {
                TakeStart(now);
                saved.Add($"Start with Windows {(_startWithWindows ? "on" : "off")}");
            }
            else failed.Add(outcome.Reason ?? now.Reason ?? "Start with Windows did not read back as it was saved.");
        }

        Edited();
        var done = saved.Count == 0 ? null : "Saved and read back: " + string.Join(", ", saved) + ".";
        if (failed.Count == 0)
        {
            // Whatever an earlier save, asked for on the way out of the page, could not do has now been done.
            SaveFailure = null;
            Status = done ?? "Nothing to save.";
            return Task.FromResult(saved.Count == 0 ? ActionOutcome.Done : ActionOutcome.DoneWith(string.Join(", ", saved)));
        }
        var reason = string.Join(" ", failed);
        Status = (done is null ? "" : done + " ") + "Not saved: " + reason;
        return Task.FromResult(ActionOutcome.Failed((done is null ? "" : done + " ") + reason));
    }

    /// <summary>Back to what the file and the Run key hold now: read again, not remembered.</summary>
    public override void Discard()
    {
        if (State != PageState.Data) return;
        // A Run key that can't be read now keeps what was last read, and says so: discarding invents no value and
        // hides no warning.
        Take(_store.Current, _startup.Read());
        Status = null;
        SaveFailure = null; // the edits a save could not keep are gone, and so is what there was to say about them
        Edited();
    }
}
