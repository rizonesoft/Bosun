using System.Windows.Input;
using Bosun.Core;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Settings;

/// <summary>One choice of theme, with the words the page shows for it.</summary>
public sealed record ThemeOption(ThemeChoice Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The Settings page (D02 T02 §5, design/SPEC.md §15, General). Each setting on it has the code that reads it: the
/// theme (the theme service, through the store) and Start with Windows (the Run value itself). The others of §15 are
/// added by the sections that build their readers.
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
    private readonly AsyncCommand _save, _discard;
    private ThemeChoice _storedTheme;
    private bool _storedStart;
    private ThemeOption _selectedTheme;
    private bool _startWithWindows;
    private string? _notice, _startupNote, _status;

    public SettingsPage(PanelSettingsStore store, StartupEntry startup, ActivityLog log, Action openStartupSettings)
        : base("Settings", "How the panel starts, looks and notifies.")
    {
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

    /// <summary>Why the settings are not the file's own: it was moved aside, and the defaults are in use.</summary>
    public string? Notice
    {
        get => _notice;
        private set => Set(ref _notice, value);
    }

    /// <summary>Said when Windows has the start-up entry turned off: the Run value is there, and Windows is not acting on it.</summary>
    public string? StartupNote
    {
        get => _startupNote;
        private set => Set(ref _startupNote, value);
    }

    /// <summary>What the last save did.</summary>
    public string? Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public ICommand Save => _save;

    public ICommand DiscardChanges => _discard;

    /// <summary>Opens Windows' own start-up settings, the supported way to turn an entry back on.</summary>
    public ICommand OpenStartupSettings { get; }

    public override bool HasUnsavedEdits => State == PageState.Data && (_selectedTheme.Value != _storedTheme || _startWithWindows != _storedStart);

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
        Take(_store.Current.Theme, start);
        ShowData();
        Edited();
    }

    /// <summary>What is stored becomes what the page shows.</summary>
    private void Take(ThemeChoice theme, StartupRead start)
    {
        _storedTheme = theme;
        TakeStart(start);
        SelectedTheme = ThemeOptions.First(option => option.Value == theme);
        StartWithWindows = _storedStart;
    }

    private void TakeStart(StartupRead start)
    {
        _storedStart = start.Chosen;
        StartupNote = start.State == StartupState.DisabledByWindows
            ? "Windows has Bosun Panel turned off in its start-up settings, so it will not start at sign-in until it is turned on there."
            : null;
    }

    public override Task<ActionOutcome> SaveAsync()
    {
        var saved = new List<string>();
        var failed = new List<string>();

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
        var start = _startup.Read();
        // A Run key that can't be read now keeps what was last read: discarding must not invent a value.
        Take(_store.Current.Theme, start.State == StartupState.Unavailable ? new StartupRead(_storedStart ? StartupState.On : StartupState.Off) : start);
        Status = null;
        Edited();
    }
}
