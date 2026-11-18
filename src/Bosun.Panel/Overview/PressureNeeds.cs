using System.Globalization;
using Bosun.Core.Memory;
using Bosun.Panel.Tray;

namespace Bosun.Panel.Overview;

/// <summary>
/// The Memory page's part of Needs you (D03 T01 §5), fed by the tray's own pressure check and its transitions, never by
/// a read of its own: the check runs every 30 s whatever page is shown, in the tray too (design/SPEC.md §1).
///
/// An episode is a row for as long as <see cref="PressureWatch"/> keeps it: from the check that found free memory under
/// the floor until one finds it more than <see cref="PressureWatch.Hysteresis"/> over it, so its words say what stays
/// true through the recovery band ("fell under ... and has not recovered past ..."), never that memory is under the
/// floor now. A check that could not decide (no snapshot, a stale one, one that did not measure free memory) keeps the
/// episode as it was, and is a row of its own with the check's reason, as the tray already treats it.
/// </summary>
public sealed class PressureNeeds : INeedsYouProvider
{
    public const string Section = "memory", Where = "Memory", PressureWord = "Pressure", UnknownWord = "Unknown";

    private PressureCheck? _check;
    private IReadOnlyList<NeedsYouRow> _rows = [];
    private DateTimeOffset? _problemSince;
    private readonly TimeProvider _clock;

    public PressureNeeds(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public IReadOnlyList<NeedsYouRow> Rows => _rows;

    public bool HasLooked => _check?.HasChecked == true;

    public event Action? Changed;

    /// <summary>Follows the check: each of its runs gives the rows again. Once, before its first run.</summary>
    public void Watch(PressureCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (_check is not null) throw new InvalidOperationException("one pressure check feeds Needs you");
        _check = check;
        check.Checked += Take;
        Take();
    }

    /// <summary>The row an episode is: when it began, the floor it fell under and the level it must come back over.</summary>
    public static string EpisodeWords(DateTimeOffset since, long floorMb) => string.Create(CultureInfo.InvariantCulture,
        $"Memory pressure since {since.ToLocalTime():HH:mm}: free memory fell under the {floorMb:N0} MB alert threshold and has not recovered past {floorMb + PressureWatch.Hysteresis:N0} MB.");

    /// <summary>The row a check that can't decide is, with its reason.</summary>
    public static string ProblemWords(string why) => $"The memory check can't read free memory: {why}";

    private void Take()
    {
        if (_check is not { } check) return;
        var rows = new List<NeedsYouRow>();
        if (check.InEpisode)
        {
            var since = check.Since ?? _clock.GetUtcNow();
            rows.Add(new NeedsYouRow("pressure", PressureWord, EpisodeWords(since, check.FloorMb), Where, Section, NeedsSeverity.Urgent, since) { SaidByTray = true });
        }
        if (check.Problem is { } why)
        {
            // Since the check first failed: a reason that changes (a stale snapshot's age) is the same row.
            _problemSince ??= _clock.GetUtcNow();
            rows.Add(new NeedsYouRow("check", UnknownWord, ProblemWords(why), Where, Section, NeedsSeverity.Warning, _problemSince.Value) { SaidByTray = true });
        }
        else _problemSince = null;
        _rows = rows;
        Changed?.Invoke();
    }
}
