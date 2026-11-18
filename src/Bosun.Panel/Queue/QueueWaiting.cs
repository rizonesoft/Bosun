using Bosun.Core.Memory;

namespace Bosun.Panel.Queue;

/// <summary>
/// How many jobs wait for a slot (D03 T01 §5): the one owner of the waiter count, beside <see cref="QueueSummary"/>.
/// Every reading of the queue's status publishes to it, the Queue page's and the Overview's alike, so the Queue rail
/// badge is right from the first reading either page takes, before Queue has been visited. Like the summary, a reading
/// is taken only if no reading begun after it has been taken already, so a slow read can't put back an older count.
/// A status that could not be read, or whose records could not be read, counts nothing waiting: nothing is known to.
/// </summary>
public sealed class QueueWaiting
{
    private long _asked, _applied;

    /// <summary>The jobs waiting in the last reading taken; 0 before the first.</summary>
    public int Count { get; private set; }

    /// <summary>Raised when <see cref="Count"/> changes. On the thread that took the reading: the UI thread.</summary>
    public event Action? Changed;

    /// <summary>A ticket for a reading about to start.</summary>
    public long Begin() => ++_asked;

    /// <summary>The waiters a status says, when it could tell: its readable records' waiters.</summary>
    public static int Of(StatusDoc status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.Records.Readable ? status.Waiters.Count : 0;
    }

    /// <summary>A reading becomes the count, unless one begun after it has been taken.</summary>
    public void Take(Reading<StatusDoc> reading, long ticket)
    {
        ArgumentNullException.ThrowIfNull(reading);
        if (ticket <= _applied) return;
        _applied = ticket;
        var count = reading.Value is { } status ? Of(status) : 0;
        if (count == Count) return;
        Count = count;
        Changed?.Invoke();
    }
}
