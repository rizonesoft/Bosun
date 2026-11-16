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

    /// <summary>One check. On the UI thread: what it changes is the tray's.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reading = await read(cancellationToken);
        long? available = null;
        if (!reading.HasValue) tray.CheckProblem = $"memory check unavailable: {reading.Why}";
        else if (reading.IsStale) tray.CheckProblem = $"memory check stale: {reading.Why}";
        else if (reading.Value!.Counter("available_mb") is not { } measured) tray.CheckProblem = "memory check unavailable: the logger did not measure available memory.";
        else
        {
            tray.CheckProblem = null;
            available = measured;
        }

        var floor = settings.Current.PressureFloorMb; // read at every check: a saved change applies with no restart
        switch (_watch.Observe(available, floor))
        {
            case PressureChange.Started:
                tray.Pressure = true;
                notifier.Raise(NotificationType.MemoryPressure, Title,
                    string.Create(CultureInfo.InvariantCulture, $"{available:N0} MB available, under the {floor:N0} MB alert threshold. Close what you can spare before Windows freezes."));
                break;
            case PressureChange.Ended:
                tray.Pressure = false;
                log.Info(string.Create(CultureInfo.InvariantCulture, $"Memory pressure ended: {available:N0} MB available, over the {floor:N0} MB alert threshold again."));
                break;
        }
    }
}
