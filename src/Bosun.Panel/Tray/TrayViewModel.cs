using System.Globalization;

namespace Bosun.Panel.Tray;

/// <summary>What the tray icon says (design/SPEC.md §17): each state has its icon, and the tooltip says it in words.</summary>
public enum TrayState
{
    Normal,

    /// <summary>Memory is under pressure.</summary>
    Pressure,

    /// <summary>Something needs the operator.</summary>
    NeedsYou,
}

/// <summary>One line of the tray's menu: its words, and what choosing it does.</summary>
public sealed record TrayMenuItem(string Id, string Text, Func<Task> Run);

/// <summary>
/// The tray's state and its menu (D02 T02 §7, design/SPEC.md §17), with nothing of Windows in it: which of the
/// three states the icon shows, the tooltip that says it in words, and the four lines of the menu.
/// </summary>
public sealed class TrayViewModel
{
    public const string Open = "open", Queue = "queue", Pause = "pause", Exit = "exit";

    private readonly Notifier _notifier;
    private readonly INeedsYouSource? _needs;
    private readonly IQueueSummary? _queue;
    private readonly Func<Task> _open, _openQueue, _exit;
    private bool _pressure;

    public TrayViewModel(Notifier notifier, Func<Task> open, Func<Task> openQueue, Func<Task> exit, INeedsYouSource? needs = null, IQueueSummary? queue = null)
    {
        _notifier = notifier;
        _open = open;
        _openQueue = openQueue;
        _exit = exit;
        _needs = needs;
        _queue = queue;
        if (needs is not null) needs.Changed += () => Changed?.Invoke();
    }

    /// <summary>Raised when <see cref="State"/> or <see cref="Tooltip"/> may have changed.</summary>
    public event Action? Changed;

    /// <summary>Whether memory is under pressure. Said by the memory check (D03 T01 §4).</summary>
    public bool Pressure
    {
        get => _pressure;
        set
        {
            if (_pressure == value) return;
            _pressure = value;
            Changed?.Invoke();
        }
    }

    private string? _checkProblem;

    /// <summary>
    /// Why the memory check could not decide, when it couldn't (D03 T01 §4): "memory check unavailable: ..." or
    /// "memory check stale: ...". Said in the tooltip beside the state, so a check that failed never looks like
    /// healthy memory; null when the check read memory.
    /// </summary>
    public string? CheckProblem
    {
        get => _checkProblem;
        set
        {
            if (_checkProblem == value) return;
            _checkProblem = value;
            Changed?.Invoke();
        }
    }

    /// <summary>The most a tooltip holds: Windows' NOTIFYICONDATA keeps 127 characters and the terminating null.</summary>
    public const int MaxTooltip = 127;

    private int Needs => Math.Max(0, _needs?.Count ?? 0);

    /// <summary>Pressure when there is pressure, the more urgent of the two; else Needs you when something does; else Normal.</summary>
    public TrayState State => _pressure ? TrayState.Pressure : Needs > 0 ? TrayState.NeedsYou : TrayState.Normal;

    /// <summary>The icon file for a state, as resources/ names it (D02 T01 §3).</summary>
    public static string IconFile(TrayState state) => state switch
    {
        TrayState.Pressure => "tray-pressure.ico",
        TrayState.NeedsYou => "tray-needs.ico",
        _ => "tray-normal.ico",
    };

    /// <summary>The Normal state in words: the tooltip says the icon's state whichever it is (design/SPEC.md §17).</summary>
    public const string NormalTooltip = "Bosun Panel: normal";

    /// <summary>The state in words: what the icon's overlay means, never left to its colour.</summary>
    public string Tooltip
    {
        get
        {
            var needs = Needs == 0 ? null : string.Create(CultureInfo.InvariantCulture, $"{Needs} {(Needs == 1 ? "thing needs" : "things need")} you");
            var state = (_pressure, needs) switch
            {
                (true, null) => "Bosun Panel: memory pressure",
                (true, not null) => $"Bosun Panel: memory pressure, and {needs}",
                (false, not null) => $"Bosun Panel: {needs}",
                _ => NormalTooltip,
            };
            if (_checkProblem is null) return state;
            var said = $"{state}; {_checkProblem}";
            return said.Length <= MaxTooltip ? said : said[..(MaxTooltip - 3)] + "...";
        }
    }

    /// <summary>
    /// The menu, as it reads now (design/SPEC.md §17): Open, the queue in words, Pause notifications for 1 hour (or
    /// Resume, with the time the pause ends), and Exit. Made each time the menu is opened, so its words are current.
    /// </summary>
    public IReadOnlyList<TrayMenuItem> Menu()
    {
        // A value that can't be read is Not available, never an empty queue (design/SPEC.md §1).
        var queue = _queue?.Words is { Length: > 0 } words ? $"Queue: {words}" : "Queue: Not available";
        var paused = _notifier.PausedUntil;
        var pause = paused is { } until
            ? new TrayMenuItem(Pause, string.Create(CultureInfo.InvariantCulture, $"Resume notifications (paused until {until:HH:mm})"), () => Done(_notifier.Resume))
            : new TrayMenuItem(Pause, "Pause notifications for 1 hour", () => Done(_notifier.PauseForAnHour));
        return [new(Open, "Open", _open), new(Queue, queue, _openQueue), pause, new(Exit, "Exit", _exit)];
    }

    private static Task Done(Action act)
    {
        act();
        return Task.CompletedTask;
    }
}
