using System.Globalization;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Tray;

/// <summary>
/// The memory pressure check (D03 T01 §4, design/SPEC.md §1 and §17): every 30 s, window shown or in the tray (in the
/// tray, it is the only check that runs), it reads Windows' available memory from the logger's snapshot and the stored
/// threshold, and says what changed: the tray's Pressure state, one notification as an episode starts (the notifier
/// honours the notification setting and the hour's pause, and writes the line either way), and a line as it ends. A
/// reading that decides nothing (no snapshot, one that can't be read or isn't one, a stale one, or one that didn't
/// measure available memory) keeps the episode as it was and says why in the tray, never a healthy state.
/// </summary>
public sealed class PressureCheck(Func<CancellationToken, Task<Reading<SnapshotDoc>>> read, PanelSettingsStore settings, TrayViewModel tray,
    Notifier notifier, ActivityLog log)
{
    /// <summary>How often it runs: the logger's own interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public const string Title = "Memory is running low";

    private readonly PressureWatch _watch = new();

    /// <summary>Whether an episode is under way.</summary>
    public bool InEpisode => _watch.InEpisode;

    private string? _said;     // the full reason last written to the Activity pane: written once per change
    private bool _owed;        // an episode whose notification Windows refused: tried again at each check while it lasts

    /// <summary>One check. On the UI thread: what it changes is the tray's.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reading = await read(cancellationToken);
        long? available = null;
        string? problem = null, full = null;
        if (!reading.HasValue) (problem, full) = ($"memory check unavailable: {Short(reading.Problem!.Value)}", reading.Why);
        else if (reading.IsStale) (problem, full) = ($"memory check stale: {reading.Why}", reading.Why);
        else if (reading.Value!.Counter("available_mb") is not { } measured)
            (problem, full) = ("memory check unavailable: available memory not measured", "The logger's snapshot did not measure available memory.");
        else available = measured;

        // The tooltip holds 127 characters: it has the short reason first, and the Activity pane the whole of it, once.
        tray.CheckProblem = problem;
        if (full != _said)
        {
            if (full is not null) log.Warn($"The memory pressure check can't read memory: {full}");
            else if (_said is not null) log.Info("The memory pressure check reads memory again.");
            _said = full;
        }

        var floor = settings.Current.PressureFloorMb; // read at every check: a saved change applies with no restart
        switch (_watch.Observe(available, floor))
        {
            case PressureChange.Started:
                tray.Pressure = true;
                _owed = Notify(available!.Value, floor, quietly: false) == Delivery.Refused;
                break;
            case PressureChange.Ended:
                tray.Pressure = false;
                _owed = false;
                log.Info(string.Create(CultureInfo.InvariantCulture, $"Memory pressure ended: {available:N0} MB available, over the {floor:N0} MB alert threshold again."));
                break;
            default:
                // Windows refused the episode's notification: tried again while the pressure lasts, quietly, until it is
                // shown. Turned off or paused is the person's choice, and is not tried again.
                if (_owed && _watch.InEpisode && available is { } now) _owed = Notify(now, floor, quietly: true) == Delivery.Refused;
                break;
        }
    }

    private Delivery Notify(long available, long floor, bool quietly) => notifier.Deliver(NotificationType.MemoryPressure, Title,
        string.Create(CultureInfo.InvariantCulture, $"{available:N0} MB available, under the {floor:N0} MB alert threshold. Close what you can spare."), quietly);

    /// <summary>What a problem is, short enough for the tray's tooltip; the reader's own words go to the Activity pane.</summary>
    private static string Short(ReadingProblem problem) => problem switch
    {
        ReadingProblem.Missing => "the logger has not written its snapshot",
        ReadingProblem.Unreadable => "the logger's snapshot can't be read",
        ReadingProblem.Invalid => "the logger's snapshot is damaged",
        ReadingProblem.Unsupported => "the logger's snapshot is of another version",
        ReadingProblem.NotInstalled => "bosun is not installed",
        _ => "the logger could not be read",
    };
}
