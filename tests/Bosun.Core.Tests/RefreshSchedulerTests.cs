using Xunit;

namespace Bosun.Core.Tests;

/// <summary>
/// The refresh scheduler (D02 T02 §8, design/SPEC.md §1): what is started runs at once and at its interval, what is
/// stopped runs nothing, nothing runs twice at once, a failing refresh backs off, and a stopped run is stale. Every
/// test moves its own clock and ticks by hand: no test waits for time to pass.
/// </summary>
public class RefreshSchedulerTests
{
    /// <summary>A driver that only writes down whether it was asked to tick.</summary>
    private sealed class Driver : IRefreshDriver
    {
        public Action? Tick { get; private set; }

        public List<string> Calls { get; } = [];

        public void Start(Action tick)
        {
            Calls.Add("start");
            Tick = tick;
        }

        public void Stop()
        {
            Calls.Add("stop");
            Tick = null;
        }
    }

    /// <summary>Work a test ends when it chooses: each call waits until it is told how to end.</summary>
    private sealed class Work
    {
        private readonly List<(TaskCompletionSource Ends, CancellationToken Token)> _calls = [];

        public int Calls => _calls.Count;

        /// <summary>Whether a call's token has been cancelled.</summary>
        public bool Cancelled(int call) => _calls[call].Token.IsCancellationRequested;

        public Task Run(CancellationToken token)
        {
            var ends = new TaskCompletionSource();
            _calls.Add((ends, token));
            return ends.Task;
        }

        public void End(int call) => _calls[call].Ends.SetResult();

        public void Fail(int call, string message) => _calls[call].Ends.SetException(new InvalidOperationException(message));

        /// <summary>The work notices its cancellation and gives up, as a well-behaved tool reader does.</summary>
        public void GiveUp(int call) => _calls[call].Ends.SetException(new OperationCanceledException(_calls[call].Token));
    }

    private sealed class Bench
    {
        public DateTime Now = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);
        public Driver Driver { get; } = new();
        public RefreshScheduler Scheduler { get; }

        public Bench() => Scheduler = new RefreshScheduler(() => Now, Driver);

        /// <summary>Moves the clock on and ticks once, as the driver would.</summary>
        public void After(double seconds)
        {
            Now = Now.AddSeconds(seconds);
            Scheduler.Tick();
        }
    }

    [Fact]
    public void The_intervals_are_the_specs()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), RefreshScheduler.Fast);
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshScheduler.Slow);
        Assert.Equal(TimeSpan.FromMinutes(5), RefreshScheduler.MaxBackoff);
    }

    [Fact]
    public void A_refresh_does_nothing_until_it_is_started_then_runs_at_once_and_one_interval_after_each_run_ends()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, work.Run);

        bench.After(60);
        Assert.Equal((0, false, (DateTime?)null), (work.Calls, refresh.IsActive, refresh.NextDue)); // registered is not started

        refresh.Start();
        Assert.Equal((1, true, true), (work.Calls, refresh.IsActive, refresh.IsRunning)); // at once
        work.End(0);
        Assert.False(refresh.IsRunning);
        Assert.Equal(bench.Now.AddSeconds(5), refresh.NextDue);

        bench.After(4.9);
        Assert.Equal(1, work.Calls); // not before its time
        bench.After(0.1);
        Assert.Equal(2, work.Calls);
        work.End(1);
        bench.After(5);
        Assert.Equal(3, work.Calls);
        Assert.Equal(3, refresh.Runs);

        refresh.Start(); // started twice is started once
        Assert.Equal(3, work.Calls);
    }

    [Fact]
    public void A_stopped_refresh_makes_no_call_from_then_on_and_its_work_under_way_is_cancelled()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, work.Run);
        refresh.Start();
        work.End(0);
        bench.After(5);
        Assert.Equal(2, work.Calls); // under way
        Assert.False(work.Cancelled(1));

        refresh.Stop();
        Assert.True(work.Cancelled(1));
        Assert.Equal((false, (DateTime?)null), (refresh.IsActive, refresh.NextDue));
        work.GiveUp(1);

        for (var i = 0; i < 20; i++) bench.After(5); // well past any interval
        Assert.Equal(2, work.Calls);
        Assert.Equal(0, refresh.Failures); // a stop is not a failure of the tool
    }

    [Fact]
    public void Two_refreshes_each_keep_their_own_time()
    {
        var bench = new Bench();
        var fast = new Work();
        var slow = new Work();
        var memory = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, fast.Run);
        var instances = bench.Scheduler.Register("Instances", RefreshScheduler.Slow, slow.Run);
        memory.Start();
        instances.Start();
        fast.End(0);
        slow.End(0);

        for (var second = 1; second <= 30; second++)
        {
            bench.After(1);
            if (memory.IsRunning) fast.End(fast.Calls - 1);
            if (instances.IsRunning) slow.End(slow.Calls - 1);
        }

        Assert.Equal(7, fast.Calls); // at 0, 5, 10, 15, 20, 25 and 30 s
        Assert.Equal(2, slow.Calls); // at 0 and 30 s
        Assert.Equal([memory, instances], bench.Scheduler.Refreshes);
    }

    [Fact]
    public void The_driver_ticks_only_while_a_refresh_is_active()
    {
        var bench = new Bench();
        var first = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, _ => Task.CompletedTask);
        var second = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, _ => Task.CompletedTask);
        Assert.Empty(bench.Driver.Calls); // nothing to refresh, nothing ticking

        first.Start();
        second.Start();
        Assert.Equal(["start"], bench.Driver.Calls); // with the first
        Assert.NotNull(bench.Driver.Tick);

        first.Stop();
        Assert.Equal(["start"], bench.Driver.Calls);
        second.Stop();
        Assert.Equal(["start", "stop"], bench.Driver.Calls); // with the last

        second.Start();
        Assert.Equal(["start", "stop", "start"], bench.Driver.Calls);
        var before = second.Runs;
        bench.Now = bench.Now.AddSeconds(5);
        bench.Driver.Tick!(); // the driver's own tick is the scheduler's
        Assert.Equal(before + 1, second.Runs);
    }

    [Fact]
    public void Work_that_has_not_ended_is_never_run_a_second_time_and_the_next_run_is_an_interval_after_it_ends()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Instances", RefreshScheduler.Fast, work.Run);
        refresh.Start();

        for (var i = 0; i < 12; i++) bench.After(5); // a tool a minute slow
        Assert.Equal(1, work.Calls);

        work.End(0);
        bench.After(4);
        Assert.Equal(1, work.Calls); // not back to back: an interval from its end
        bench.After(1);
        Assert.Equal(2, work.Calls);
    }

    [Fact]
    public async Task Refresh_now_makes_one_extra_call_and_joins_a_run_under_way()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, work.Run);
        Assert.Same(RefreshOutcome.NotStarted, await refresh.RunNowAsync()); // not started: nothing runs
        Assert.Equal(0, work.Calls);

        refresh.Start();
        work.End(0);
        bench.After(1);

        var now = refresh.RunNowAsync(); // between two polls: one extra call
        Assert.Equal(2, work.Calls);
        var joined = refresh.RunNowAsync(); // pressed again while it runs: the same run
        Assert.Same(now, joined);
        Assert.Equal(2, work.Calls);
        work.End(1);
        Assert.True((await now).Succeeded);
        Assert.Equal(bench.Now.AddSeconds(5), refresh.NextDue); // and the next poll is an interval from it

        bench.After(5);
        Assert.Equal(3, work.Calls); // a poll under way
        var third = refresh.RunNowAsync();
        Assert.Equal(3, work.Calls); // no overlap with the poll
        work.End(2);
        Assert.True((await third).Succeeded);
    }

    [Fact]
    public void A_failing_refresh_waits_twice_as_long_each_time_up_to_five_minutes_and_is_back_at_its_interval_after_one_success()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, work.Run);
        var outcomes = new List<RefreshOutcome>();
        refresh.Finished += outcomes.Add;
        refresh.Start();

        double[] waits = [10, 20, 40, 80, 160, 300, 300, 300];
        for (var failure = 0; failure < waits.Length; failure++)
        {
            work.Fail(failure, "bosun: the status file is locked");
            Assert.Equal(failure + 1, refresh.Failures);
            Assert.Equal(TimeSpan.FromSeconds(waits[failure]), refresh.Wait);
            Assert.Equal(new RefreshOutcome(false, "bosun: the status file is locked", TimeSpan.FromSeconds(waits[failure])), outcomes[^1]);

            bench.After(waits[failure] - 1);
            Assert.Equal(failure + 1, work.Calls); // not a second early
            bench.After(1);
            Assert.Equal(failure + 2, work.Calls);
        }

        work.End(waits.Length); // one success
        Assert.Equal((0, RefreshScheduler.Fast), (refresh.Failures, refresh.Wait));
        Assert.Equal(new RefreshOutcome(true, null, RefreshScheduler.Fast), outcomes[^1]);
        bench.After(5);
        Assert.Equal(waits.Length + 2, work.Calls);
    }

    [Fact]
    public void Work_that_throws_at_once_is_a_failure_like_any_other_and_never_a_crash()
    {
        // The plan review: a throw before the first await must be counted and backed off, not swallowed or raised.
        var bench = new Bench();
        var calls = 0;
        var refresh = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, _ =>
        {
            calls++;
            throw new FileNotFoundException("bosun.exe was not found");
        });
        var outcomes = new List<RefreshOutcome>();
        refresh.Finished += outcomes.Add;

        refresh.Start();
        Assert.Equal((1, 1), (calls, refresh.Failures));
        Assert.Equal([new RefreshOutcome(false, "bosun.exe was not found", TimeSpan.FromSeconds(10))], outcomes);

        bench.After(9);
        Assert.Equal(1, calls);
        bench.After(1);
        Assert.Equal((2, 2), (calls, refresh.Failures));

        // Work that gives up on its own, not because it was stopped, is a failure too.
        var gaveUp = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, _ => throw new OperationCanceledException("the tool timed out"));
        gaveUp.Start();
        Assert.Equal(1, gaveUp.Failures);
    }

    [Fact]
    public async Task A_stopped_run_is_stale_and_a_refresh_started_again_while_it_ends_waits_for_it_and_then_runs_once()
    {
        // The plan review: a stop does not end the work at that instant. Coming back to the page meanwhile must
        // neither run beside the old call nor take the old call's result for fresh.
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, work.Run);
        var outcomes = new List<RefreshOutcome>();
        refresh.Finished += outcomes.Add;

        refresh.Start();          // call 0, under way
        refresh.Stop();           // the page is hidden: cancelled, still ending
        Assert.True(refresh.IsRunning);
        refresh.Start();          // and shown again before it has ended
        Assert.Equal(1, work.Calls); // no call beside it
        bench.After(30);
        Assert.Equal(1, work.Calls); // however long it takes
        var now = refresh.RunNowAsync(); // Refresh pressed meanwhile: it is the fresh run that is waited for
        Assert.False(now.IsCompleted);

        work.End(0); // the old call ignores its cancellation and ends well: stale all the same
        Assert.Empty(outcomes);
        Assert.Equal(2, work.Calls); // exactly one fresh run, at once
        Assert.True(refresh.IsRunning);

        work.End(1);
        Assert.Equal([new RefreshOutcome(true, null, RefreshScheduler.Fast)], outcomes);
        Assert.True((await now.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Succeeded);
        Assert.Equal(bench.Now.AddSeconds(5), refresh.NextDue); // the old run's end set no time
    }

    [Fact]
    public async Task A_stale_run_that_fails_counts_nothing_and_one_left_stopped_starts_nothing()
    {
        var bench = new Bench();
        var work = new Work();
        var refresh = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, work.Run);
        var outcomes = new List<RefreshOutcome>();
        refresh.Finished += outcomes.Add;

        refresh.Start();
        refresh.Stop();
        refresh.Start();
        var waiting = refresh.RunNowAsync();
        refresh.Stop(); // hidden again before the old call ended
        Assert.Same(RefreshOutcome.NotStarted, await waiting); // nothing is coming

        work.Fail(0, "bosun: interrupted");
        Assert.Equal((1, 0), (work.Calls, refresh.Failures));
        Assert.Empty(outcomes);
        bench.After(60);
        Assert.Equal(1, work.Calls);
    }

    [Fact]
    public async Task A_stopped_scheduler_stops_every_refresh_cancels_work_under_way_and_starts_nothing_after()
    {
        var bench = new Bench();
        var work = new Work();
        var idle = new Work();
        var running = bench.Scheduler.Register("Memory", RefreshScheduler.Fast, work.Run);
        var waiting = bench.Scheduler.Register("Queue", RefreshScheduler.Fast, idle.Run);
        running.Start();
        waiting.Start();
        idle.End(0);

        bench.Scheduler.Stop();

        Assert.True(bench.Scheduler.IsStopped);
        Assert.True(work.Cancelled(0));
        Assert.Equal((false, false), (running.IsActive, waiting.IsActive));
        Assert.Equal(["start", "stop"], bench.Driver.Calls);

        running.Start(); // nothing starts after
        waiting.Start();
        Assert.Same(RefreshOutcome.NotStarted, await waiting.RunNowAsync());
        work.GiveUp(0);
        for (var i = 0; i < 10; i++) bench.After(5);
        Assert.Equal((1, 1), (work.Calls, idle.Calls));
        Assert.Equal(["start", "stop"], bench.Driver.Calls);

        bench.Scheduler.Dispose(); // again: nothing more to do
        Assert.Equal(["start", "stop"], bench.Driver.Calls);
    }

    [Fact]
    public void A_refresh_needs_a_name_an_interval_and_work()
    {
        var scheduler = new RefreshScheduler();
        Assert.Throws<ArgumentException>(() => scheduler.Register(" ", RefreshScheduler.Fast, _ => Task.CompletedTask));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Register("Memory", TimeSpan.Zero, _ => Task.CompletedTask));
        Assert.Throws<ArgumentNullException>(() => scheduler.Register("Memory", RefreshScheduler.Fast, null!));

        // An interval longer than the limit is never shortened by backing off.
        var slow = scheduler.Register("About", TimeSpan.FromMinutes(10), _ => throw new InvalidOperationException("no"));
        slow.Start();
        Assert.Equal(TimeSpan.FromMinutes(10), slow.Wait);
    }
}
