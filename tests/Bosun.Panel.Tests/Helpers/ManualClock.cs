namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// A clock a test moves itself: its timers fire, on the test's thread, only when <see cref="Advance"/> passes their
/// time, so a test of a delay says exactly when it ends.
/// </summary>
sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly List<Timer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualClock() : this(new DateTimeOffset(2026, 11, 16, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Timers made and not yet disposed or fired.</summary>
    public int Pending => _timers.Count(t => t.Due is not null);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timer.Change(dueTime, period);
        _timers.Add(timer);
        return timer;
    }

    /// <summary>Moves the clock on, firing each timer whose time has come, in order.</summary>
    public void Advance(TimeSpan by)
    {
        var end = _now + by;
        while (_timers.Where(t => t.Due is { } due && due <= end).OrderBy(t => t.Due).FirstOrDefault() is { } next)
        {
            _now = next.Due!.Value;
            next.Due = null; // one shot: the panel's timers never repeat
            next.Callback(next.State);
        }
        _now = end;
    }

    public void Advance(double milliseconds) => Advance(TimeSpan.FromMilliseconds(milliseconds));

    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
            return true;
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
