namespace Bosun.Core;

/// <summary>How one run of a refresh ended: done, or failed with the tool's own message, and how long until the next.</summary>
public sealed record RefreshOutcome(bool Succeeded, string? Message, TimeSpan NextIn)
{
    /// <summary>The refresh is not started (its page is hidden, or the panel is on its way out): nothing was run.</summary>
    public static RefreshOutcome NotStarted { get; } = new(false, "It is not refreshing now.", TimeSpan.Zero);

    /// <summary>The run was stopped before it ended: whatever it read is old, and is given to nobody.</summary>
    public static RefreshOutcome Stale { get; } = new(false, "It was stopped before it ended.", TimeSpan.Zero);
}

/// <summary>
/// What ticks the scheduler (D02 T02 §8). The scheduler keeps no timer and no thread of its own: it starts its
/// driver when the first refresh becomes active and stops it with the last, so with nothing to refresh nothing ticks.
/// </summary>
public interface IRefreshDriver
{
    /// <summary>Begin calling <paramref name="tick"/> regularly, about once a second.</summary>
    void Start(Action tick);

    void Stop();
}

/// <summary>
/// The one owner of every refresh in the panel (D02 T02 §8, design/SPEC.md §1: pages refresh while visible, memory
/// and the queue every 5 s, the others every 30 s; a hidden page polls nothing). A refresh is registered once and
/// then started and stopped; the scheduler runs what is due, by the clock it is given, each time it is ticked.
///
/// Used from one thread, the UI's: its driver ticks there, and a run's end comes back there.
/// </summary>
public sealed class RefreshScheduler(Func<DateTime>? clock = null, IRefreshDriver? driver = null) : IDisposable
{
    /// <summary>Memory and the queue (design/SPEC.md §1).</summary>
    public static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);

    /// <summary>Every other page.</summary>
    public static readonly TimeSpan Slow = TimeSpan.FromSeconds(30);

    /// <summary>The longest a refresh that keeps failing waits between tries.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private readonly List<Refresh> _all = [];
    private bool _driving;

    /// <summary>True once the panel is on its way out: every refresh is stopped, and none can start.</summary>
    public bool IsStopped { get; private set; }

    public IReadOnlyList<Refresh> Refreshes => _all;

    internal DateTime Now => _clock();

    /// <summary>
    /// Registers a refresh: its name (for what is said about it), how often it runs, and its work, which is given a
    /// token that is cancelled when the refresh is stopped. It does nothing until it is started.
    /// </summary>
    public Refresh Register(string name, TimeSpan interval, Func<CancellationToken, Task> work)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a refresh has a name", nameof(name));
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval), "a refresh has an interval above zero");
        ArgumentNullException.ThrowIfNull(work);
        var refresh = new Refresh(this, name, interval, work);
        _all.Add(refresh);
        return refresh;
    }

    /// <summary>Runs every refresh that is active, not running, and due.</summary>
    public void Tick()
    {
        if (IsStopped) return;
        foreach (var refresh in _all.ToArray()) refresh.RunIfDue();
    }

    /// <summary>The panel is exiting: every refresh is stopped, work under way is cancelled, and nothing starts after.</summary>
    public void Stop()
    {
        if (IsStopped) return;
        IsStopped = true;
        foreach (var refresh in _all.ToArray()) refresh.Stop();
        ActivityChanged();
    }

    public void Dispose() => Stop();

    /// <summary>The driver runs exactly while something is active.</summary>
    internal void ActivityChanged()
    {
        var any = !IsStopped && _all.Any(refresh => refresh.IsActive);
        if (any == _driving) return;
        _driving = any;
        if (any) driver?.Start(Tick);
        else driver?.Stop();
    }
}

/// <summary>
/// One refresh (D02 T02 §8). Started, it runs at once and then one interval after each run ends, so work slower than
/// its interval never runs back to back, and it never runs twice at once. A run that fails makes the next wait
/// twice as long, up to <see cref="RefreshScheduler.MaxBackoff"/>, until one works. Stopped, its work under way is
/// cancelled, and that run is stale: its outcome is given to nobody and changes nothing.
/// </summary>
public sealed class Refresh
{
    private readonly RefreshScheduler _owner;
    private readonly Func<CancellationToken, Task> _work;
    private CancellationTokenSource? _cancel;
    private Task<RefreshOutcome>? _current;
    private TaskCompletionSource<RefreshOutcome>? _fresh;
    private bool _busy;
    private int _generation;
    private DateTime _due;

    internal Refresh(RefreshScheduler owner, string name, TimeSpan interval, Func<CancellationToken, Task> work)
    {
        _owner = owner;
        _work = work;
        Name = name;
        Interval = interval;
    }

    public string Name { get; }

    public TimeSpan Interval { get; }

    /// <summary>Started and not stopped: it runs when it is due.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Its work is under way, or a stopped run is still ending.</summary>
    public bool IsRunning => _busy;

    /// <summary>How many runs in a row have failed.</summary>
    public int Failures { get; private set; }

    /// <summary>How many times the work has been called.</summary>
    public int Runs { get; private set; }

    /// <summary>When the next run is due; null while it is not active.</summary>
    public DateTime? NextDue => IsActive ? _due : null;

    /// <summary>How long after a run ends the next is due: the interval, doubled for each failure in a row, up to the limit.</summary>
    public TimeSpan Wait
    {
        get
        {
            if (Failures == 0) return Interval;
            var backed = Interval * Math.Pow(2, Math.Min(Failures, 20));
            var limit = Interval > RefreshScheduler.MaxBackoff ? Interval : RefreshScheduler.MaxBackoff;
            return backed > limit ? limit : backed;
        }
    }

    /// <summary>A run that was not stopped has ended. Raised where the run ended: on the UI thread, in the panel.</summary>
    public event Action<RefreshOutcome>? Finished;

    /// <summary>
    /// Starts it, and runs it at once: what is shown is fresh, not up to an interval old. Started again while a
    /// stopped run is still ending, it waits for that run and then runs once: never beside it.
    /// </summary>
    public void Start()
    {
        if (_owner.IsStopped || IsActive) return;
        IsActive = true;
        _owner.ActivityChanged();
        if (_busy) _fresh ??= new TaskCompletionSource<RefreshOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        else _ = RunAsync();
    }

    /// <summary>Stops it: no run starts from now on, and work under way is cancelled and its result dropped.</summary>
    public void Stop()
    {
        if (!IsActive) return;
        IsActive = false;
        _generation++;
        _cancel?.Cancel();
        Interlocked.Exchange(ref _fresh, null)?.TrySetResult(RefreshOutcome.NotStarted);
        _owner.ActivityChanged();
    }

    /// <summary>
    /// Runs it now, for the Refresh button and Retry. While a run is under way this joins it: no second call. While
    /// a stopped run is still ending, it is the fresh run that follows. Not started, nothing runs.
    /// </summary>
    public Task<RefreshOutcome> RunNowAsync()
    {
        if (!IsActive) return Task.FromResult(RefreshOutcome.NotStarted);
        if (_fresh is { } fresh) return fresh.Task;
        return _busy && _current is { } current ? current : RunAsync();
    }

    internal void RunIfDue()
    {
        if (IsActive && !_busy && _owner.Now >= _due) _ = RunAsync();
    }

    private Task<RefreshOutcome> RunAsync()
    {
        var run = RunCore();
        _current = run;
        return run;
    }

    private async Task<RefreshOutcome> RunCore()
    {
        _busy = true;
        Runs++;
        var mine = _generation;
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        string? failure = null;
        try
        {
            await _work(cancel.Token);
        }
        // Cancelled because it was stopped: not a failure of the tool. The run is stale, below.
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
        }
        // Whatever the tool's reader throws, at once or later, is this run's failure, with its message: the page
        // shows it, and the next try waits longer.
        catch (Exception e)
        {
            failure = string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message;
        }
        _cancel = null;
        _busy = false;

        if (mine != _generation)
        {
            // Stopped while it ran: whatever it read is old. If it was started again meanwhile, the fresh run
            // begins now that this one is out of the way.
            if (IsActive && Interlocked.Exchange(ref _fresh, null) is { } fresh) fresh.TrySetResult(await RunAsync());
            return RefreshOutcome.Stale;
        }

        Failures = failure is null ? 0 : Failures + 1;
        _due = _owner.Now + Wait;
        var outcome = new RefreshOutcome(failure is null, failure, Wait);
        Finished?.Invoke(outcome);
        return outcome;
    }
}
