using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tray;
using Xunit;

namespace Bosun.Panel.Tests.Tray;

/// <summary>
/// The memory pressure check (D03 T01 §4): the tray goes to Pressure with its words and one notification per episode,
/// each start and end in the Activity pane, the threshold read at every check; a check that read nothing says so in
/// the tray and changes nothing else; and, started as the application starts it, a pressure already present at launch
/// is notified, the check runs while the window is hidden, and stops at exit.
/// </summary>
public class PressureCheckTests
{
    private static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
        return 0;
    });

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh snapshot with this much available memory (null: not measured).</summary>
    private static Reading<SnapshotDoc> Fresh(long? availableMb) => Reading<SnapshotDoc>.Of(Snapshot(availableMb), Now, TimeSpan.FromSeconds(10));

    private static SnapshotDoc Snapshot(long? availableMb) => new()
    {
        SchemaVersion = 1, Side = "windows", At = "2026-11-16T08:59:50Z", IntervalS = 30,
        Memory = new Dictionary<string, long?> { ["available_mb"] = availableMb, ["total_mb"] = 32601 },
        WslVm = null, Repos = [], Processes = [], HistoryWritten = true,
    };

    /// <summary>A check over the fixture's tray, notifier and settings, reading what the test puts in <paramref name="next"/>.</summary>
    private static (PressureCheck Check, Func<Task> Run) Bench(TrayFixture f, Func<Reading<SnapshotDoc>> next)
    {
        var check = new PressureCheck(_ => Task.FromResult(next()), f.Store, f.Controller.Tray, f.Notifier, f.Log);
        return (check, async () => await check.RunAsync(CancellationToken.None));
    }

    [Fact]
    public void An_episode_turns_the_tray_to_Pressure_says_it_once_and_writes_its_start_and_end_and_a_second_is_said_again()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Reading<SnapshotDoc> reading = Fresh(9000);
            var (_, run) = Bench(f, () => reading);
            void At(long mb)
            {
                reading = Fresh(mb);
                run().GetAwaiter().GetResult();
                Drain();
            }

            foreach (var mb in new long[] { 9000, 2100, 1900, 2300, 1700, 2600 }) At(mb); // a noisy dip
            Assert.Equal((TrayState.Pressure, "Bosun Panel: memory pressure"), (f.Icon.State, f.Icon.Tooltip));
            Assert.Single(f.Icon.Notifications);
            Assert.Equal(PressureCheck.Title, f.Icon.Notifications[0].Title);
            Assert.Contains("1,900 MB available, under the 2,048 MB alert threshold", f.Icon.Notifications[0].Text);
            At(3100); // more than 1 GB over: the end
            Assert.Equal(TrayState.Normal, f.Icon.State);
            Assert.Contains(f.Lines(LogSeverity.Info), line => line.StartsWith("Memory pressure ended: 3,100 MB available", StringComparison.Ordinal));

            At(1500); // a second episode
            Assert.Equal(2, f.Icon.Notifications.Count);
            Assert.Equal(2, f.Lines(LogSeverity.Warn).Count(line => line.StartsWith(PressureCheck.Title, StringComparison.Ordinal)));
        });
    }

    [Fact]
    public void A_threshold_saved_meanwhile_is_used_at_the_next_check_with_no_restart()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Reading<SnapshotDoc> reading = Fresh(3000);
            var (_, run) = Bench(f, () => reading);
            run().GetAwaiter().GetResult();
            Assert.Equal(TrayState.Normal, f.Icon.State); // 3,000 MB is over the default 2,048
            Assert.True(f.Store.Update(current => current with { PressureFloorMb = 4096 }).Succeeded);
            run().GetAwaiter().GetResult();
            Drain();
            Assert.Equal(TrayState.Pressure, f.Icon.State); // and under the 4,096 saved since
        });
    }

    public static TheoryData<string, string> Undecided => new()
    {
        { "missing", "memory check unavailable: " },
        { "unreadable", "memory check unavailable: " },
        { "invalid", "memory check unavailable: " },
        { "unsupported", "memory check unavailable: " },
        { "unmeasured", "memory check unavailable: available memory not measured" },
        { "stale", "memory check stale: " },
    };

    private static Reading<SnapshotDoc> Bad(string kind) => kind switch
    {
        "missing" => Reading<SnapshotDoc>.Fail(ReadingProblem.Missing, "current-windows.json is not there: its logger has not written it."),
        "unreadable" => Reading<SnapshotDoc>.Fail(ReadingProblem.Unreadable, "current-windows.json can't be read: denied."),
        "invalid" => Reading<SnapshotDoc>.Fail(ReadingProblem.Invalid, "current-windows.json is not a snapshot."),
        "unsupported" => Reading<SnapshotDoc>.Fail(ReadingProblem.Unsupported, "current-windows.json is of schema version 2."),
        "unmeasured" => Fresh(null),
        _ => Reading<SnapshotDoc>.Stale(Snapshot(9000), Now, TimeSpan.FromMinutes(12), "The Windows logger last wrote 12 minutes ago."),
    };

    [Theory]
    [MemberData(nameof(Undecided))]
    public void A_check_that_read_nothing_says_why_in_the_tray_and_keeps_the_episode_as_it_was(string kind, string said)
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Reading<SnapshotDoc> reading = Bad(kind);
            var (check, run) = Bench(f, () => reading);

            run().GetAwaiter().GetResult(); // before an episode: never a healthy state (completion review)
            Drain();
            Assert.Equal(TrayState.NeedsYou, f.Icon.State);
            Assert.StartsWith($"Bosun Panel: {said}", f.Icon.Tooltip);
            Assert.DoesNotContain("normal", f.Icon.Tooltip);
            Assert.Empty(f.Icon.Notifications);

            reading = Fresh(1000);
            run().GetAwaiter().GetResult(); // an episode starts
            reading = Bad(kind);
            run().GetAwaiter().GetResult(); // during it: kept, not said again
            Drain();
            Assert.True(check.InEpisode);
            Assert.Equal(TrayState.Pressure, f.Icon.State);
            Assert.StartsWith($"Bosun Panel: memory pressure; {said}", f.Icon.Tooltip);
            Assert.Single(f.Icon.Notifications);

            reading = Fresh(1000); // read again: the plain state, still the same episode
            run().GetAwaiter().GetResult();
            Drain();
            Assert.Equal("Bosun Panel: memory pressure", f.Icon.Tooltip);
            Assert.Single(f.Icon.Notifications);
        });
    }

    [Fact]
    public void The_real_reader_s_reason_reaches_the_tray_whole_and_the_Activity_pane_once_with_its_path()
    {
        // Completion review: the reader's words begin with the snapshot's full path, which a 127-character tooltip cut
        // before the reason. The tray has the short reason first; the Activity pane has every word, once.
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            var logs = Path.Combine(Path.GetTempPath(), "bosun-a-folder-with-a-long-name-" + new string('x', 60), "logs");
            var client = new MemoryClient(null, "", logs);
            var readable = false;
            var check = new PressureCheck(token => readable ? Task.FromResult(Fresh(9000)) : client.SnapshotAsync("windows", token), f.Store, f.Controller.Tray, f.Notifier, f.Log);
            check.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            check.RunAsync(CancellationToken.None).GetAwaiter().GetResult(); // the same again: not written again
            Drain();
            Assert.Equal("Bosun Panel: memory check unavailable: the logger has not written its snapshot", f.Icon.Tooltip);
            var said = Assert.Single(f.Lines(LogSeverity.Warn), line => line.StartsWith("The memory pressure check can't read memory:", StringComparison.Ordinal));
            Assert.Contains(Path.Combine(logs, "current-windows.json"), said);

            readable = true; // the logger writes again: the plain state, and that said once
            check.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            check.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
            Drain();
            Assert.Equal((TrayState.Normal, TrayViewModel.NormalTooltip), (f.Icon.State, f.Icon.Tooltip));
            Assert.Single(f.Lines(LogSeverity.Info), line => line == "The memory pressure check reads memory again.");
        });
    }

    [Fact]
    public void A_notification_Windows_refused_is_tried_again_while_the_pressure_lasts_and_shown_once()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Reading<SnapshotDoc> reading = Fresh(1500);
            var (_, run) = Bench(f, () => reading);
            f.Icon.FailNotify = true; // Windows refuses it as the episode starts
            run().GetAwaiter().GetResult();
            Drain();
            Assert.Empty(f.Icon.Notifications);
            run().GetAwaiter().GetResult(); // refused again: tried, written once only
            Drain();
            Assert.Single(f.Lines(LogSeverity.Warn), line => line.Contains("(not notified: Windows did not show it)", StringComparison.Ordinal));
            f.Icon.FailNotify = false;
            run().GetAwaiter().GetResult(); // shown now
            run().GetAwaiter().GetResult(); // and not again
            Drain();
            Assert.Single(f.Icon.Notifications);
        });
    }

    [Fact]
    public void A_notification_turned_off_or_paused_is_not_tried_again_when_that_ends()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            Reading<SnapshotDoc> reading = Fresh(1500);
            var (_, run) = Bench(f, () => reading);
            f.Notifier.PauseForAnHour();
            run().GetAwaiter().GetResult(); // paused as the episode starts: the person's choice
            f.Notifier.Resume();
            run().GetAwaiter().GetResult();
            Drain();
            Assert.Empty(f.Icon.Notifications);
            Assert.Equal(TrayState.Pressure, f.Icon.State); // the tray still says it

            reading = Fresh(9000);
            run().GetAwaiter().GetResult(); // the episode ends
            Assert.True(f.Store.Update(current => current with { NotifyMemoryPressure = false }).Succeeded);
            reading = Fresh(1500);
            run().GetAwaiter().GetResult(); // turned off as the next one starts
            Assert.True(f.Store.Update(current => current with { NotifyMemoryPressure = true }).Succeeded);
            run().GetAwaiter().GetResult();
            Drain();
            Assert.Empty(f.Icon.Notifications);
        });
    }

    [Fact]
    public void A_long_reason_is_cut_to_what_a_tray_tooltip_holds()
    {
        OnUi(() =>
        {
            var f = new TrayFixture().Start();
            f.Controller.Tray.CheckProblem = "memory check unavailable: " + new string('x', 300);
            Assert.Equal(TrayViewModel.MaxTooltip, f.Controller.Tray.Tooltip.Length);
            Assert.EndsWith("...", f.Controller.Tray.Tooltip);
        });
    }

    /// <summary>A driver that only says whether it ticks: the test moves time itself.</summary>
    private sealed class Driver : IRefreshDriver
    {
        public bool Ticking { get; private set; }

        public void Start(Action tick) => Ticking = true;

        public void Stop() => Ticking = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Started_as_the_application_starts_it_a_pressure_at_launch_is_said_the_check_runs_while_hidden_and_stops_at_exit(bool inTray)
    {
        OnUi(() =>
        {
            var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
            var driver = new Driver();
            var scheduler = new RefreshScheduler(() => now, driver);
            var f = new TrayFixture(refreshes: scheduler);
            var reads = 0;
            Func<CancellationToken, Task<Reading<SnapshotDoc>>> read = _ =>
            {
                reads++;
                return Task.FromResult(Fresh(1200)); // already low at launch
            };

            Assert.Throws<InvalidOperationException>(() => f.Controller.StartPressureCheck(read)); // never before the tray
            f.Start(inTray); // App.MakeWindow: the tray first, then the check
            f.Controller.StartPressureCheck(read);
            Drain();
            Assert.Equal(1, reads);
            Assert.Single(f.Icon.Notifications); // the sink was there: said at once
            Assert.Equal(TrayState.Pressure, f.Icon.State);

            f.Shell.WindowHidden(); // to the tray: the check goes on
            now = now.AddSeconds(30);
            scheduler.Tick();
            Drain();
            Assert.Equal(2, reads);
            Assert.True(driver.Ticking);

            f.Controller.ExitAsync().GetAwaiter().GetResult(); // Exit stops every refresh, this one with them
            Drain();
            now = now.AddSeconds(90);
            scheduler.Tick();
            Drain();
            Assert.Equal(2, reads);
            Assert.False(driver.Ticking);
            Assert.Equal([0], f.Exits);
        });
    }
}
