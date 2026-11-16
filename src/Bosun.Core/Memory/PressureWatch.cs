namespace Bosun.Core.Memory;

/// <summary>What a pressure check changed.</summary>
public enum PressureChange
{
    /// <summary>Nothing: still under pressure, still not, or the check could not decide.</summary>
    None,

    /// <summary>Available memory fell under the floor: an episode starts, and is said once.</summary>
    Started,

    /// <summary>Available memory came back more than <see cref="PressureWatch.Hysteresis"/> over the floor.</summary>
    Ended,
}

/// <summary>
/// Memory pressure episodes (D03 T01 §4, design/SPEC.md §17): a warning is worth having only if it arrives once, early
/// enough, and doesn't nag. An episode starts when Windows' available memory is under the floor, and ends only when it
/// is more than <see cref="Hysteresis"/> over it, so a noisy dip around the floor is one episode. A check that could
/// not read memory decides nothing: the episode stands as it was.
/// </summary>
public sealed class PressureWatch
{
    /// <summary>How far over the floor memory must come back before an episode ends: 1 GB.</summary>
    public const long Hysteresis = 1024;

    /// <summary>Whether an episode is under way.</summary>
    public bool InEpisode { get; private set; }

    /// <param name="availableMb">Windows' available memory now, or null when it could not be read.</param>
    /// <param name="floorMb">The pressure alert threshold, read again at every check.</param>
    public PressureChange Observe(long? availableMb, long floorMb)
    {
        if (availableMb is not { } available) return PressureChange.None;
        if (!InEpisode && available < floorMb)
        {
            InEpisode = true;
            return PressureChange.Started;
        }
        if (InEpisode && available > floorMb + Hysteresis)
        {
            InEpisode = false;
            return PressureChange.Ended;
        }
        return PressureChange.None;
    }
}
