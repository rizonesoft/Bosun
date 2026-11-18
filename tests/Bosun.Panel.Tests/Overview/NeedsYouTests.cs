using Bosun.Core.Memory;
using Bosun.Panel.Overview;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Queue;
using Bosun.Panel.Tray;
using Bosun.Panel.Tests.Tray;
using Xunit;
using static Bosun.Panel.Tests.Overview.OverviewFixtures;

namespace Bosun.Panel.Tests.Overview;

/// <summary>
/// Needs you (D03 T01 §5): its order (most urgent, then oldest, kept across refreshes), a provider added with no change
/// to the page, the pressure rows fed by the real pressure check through its whole episode with the Overview hidden,
/// the rail badges and their spoken words with one owner of the waiter count, and the tray's Needs you state.
/// </summary>
public class NeedsYouTests
{
    static void On(Action test) => OverviewRig.On(test);

    static IReadOnlyList<string> Keys(NeedsYou needs) => [.. needs.Rows.Select(r => r.Key)];

    [Fact]
    public void Rows_are_most_urgent_first_then_oldest_and_keep_their_place_and_first_seen_time_across_refreshes()
    {
        On(() =>
        {
            var needs = new NeedsYou();
            var a = new FakeProvider();
            var b = new FakeProvider();
            needs.Add(a);
            needs.Add(b);
            // Supplied out of order, across two providers.
            a.Say(FakeProvider.Row("late-notice", minute: 5), FakeProvider.Row("urgent", severity: NeedsSeverity.Urgent, minute: 10));
            b.Say(FakeProvider.Row("warning", severity: NeedsSeverity.Warning, minute: 9), FakeProvider.Row("early-notice", minute: 2));
            Assert.Equal(["urgent", "warning", "early-notice", "late-notice"], Keys(needs));

            // Equal severity and time: the one gathered first stays first, whichever provider says it again.
            var c = new FakeProvider();
            needs.Add(c);
            c.Say(FakeProvider.Row("tie-c", minute: 2));
            b.Say(FakeProvider.Row("warning", severity: NeedsSeverity.Warning, minute: 9), FakeProvider.Row("early-notice", minute: 2));
            Assert.Equal(["urgent", "warning", "early-notice", "tie-c", "late-notice"], Keys(needs));

            // A refresh that stamps a row later keeps its first-seen time and its place.
            a.Say(FakeProvider.Row("late-notice", minute: 50), FakeProvider.Row("urgent", severity: NeedsSeverity.Urgent, minute: 10));
            Assert.Equal(Now.AddMinutes(5), needs.Rows.Single(r => r.Key == "late-notice").FirstSeen);
            Assert.Equal(["urgent", "warning", "early-notice", "tie-c", "late-notice"], Keys(needs));

            // Removed, it goes; back, it is new again, with its new time.
            b.Say(FakeProvider.Row("warning", severity: NeedsSeverity.Warning, minute: 9));
            Assert.Equal(["urgent", "warning", "tie-c", "late-notice"], Keys(needs));
            b.Say(FakeProvider.Row("warning", severity: NeedsSeverity.Warning, minute: 9), FakeProvider.Row("early-notice", minute: 30));
            Assert.Equal(["urgent", "warning", "tie-c", "late-notice", "early-notice"], Keys(needs));
        });
    }

    [Fact]
    public void A_provider_added_shows_its_rows_in_the_table_the_tray_and_on_the_rail_with_no_change_to_the_page()
    {
        On(() =>
        {
            var rig = new OverviewRig().WithTray(() => Fresh(WindowsSnapshot()));
            var provider = new FakeProvider();
            rig.Page.Needs.Add(provider);
            provider.Say(FakeProvider.Row("stuck", section: "queue", what: "A job has waited 2 hours"),
                FakeProvider.Row("late", section: "instances", what: "An agent has been idle a day"));
            OverviewRig.Settle();

            Assert.Equal(["A job has waited 2 hours", "An agent has been idle a day"], rig.Page.Table.Lines.Select(l => l.What));
            Assert.Null(rig.Page.Table.Empty);
            Assert.Equal((TrayState.NeedsYou, "Bosun Panel: 2 things need you"), (rig.Icon.State, rig.Icon.Tooltip));
            Assert.Equal((3, "Queue, 2 waiting, 1 needs you"), rig.Badge("queue"));
            Assert.Equal((1, "Instances, 1 needs you"), rig.Badge("instances"));

            provider.Say();
            OverviewRig.Settle();
            Assert.Equal(TrayState.Normal, rig.Icon.State);
            Assert.Equal(NeedsYouTable.Nothing, rig.Page.Table.Empty);
            Assert.Equal((0, "Instances"), rig.Badge("instances"));
        });
    }

    [Fact]
    public void The_pressure_rows_follow_the_real_check_through_an_episode_with_the_Overview_hidden_and_the_tray_agrees()
    {
        On(() =>
        {
            Reading<SnapshotDoc> reading = Fresh(WindowsSnapshot(available: 9000));
            var rig = new OverviewRig().WithTray(() => reading);
            var table = rig.Page.Table;
            Assert.Equal(NeedsYouTable.Nothing, table.Empty);                         // the check is healthy: nothing
            rig.GoTo("settings");                                                     // the Overview hidden from here on
            var reads = rig.Memory.WindowsReads;

            void Check(Reading<SnapshotDoc> next)
            {
                reading = next;
                rig.RunCheck();
            }

            // Entry: under the 2,048 MB default.
            Check(Fresh(WindowsSnapshot(available: 1500)));
            var row = Assert.Single(table.Lines);
            Assert.Equal(PressureNeeds.PressureWord, row.State);
            var since = rig.Check!.Since!.Value;                                      // when the check saw it begin
            Assert.Equal(since, row.Row.FirstSeen);
            Assert.Equal(PressureNeeds.EpisodeWords(since, 2048, 2048), row.What);
            Assert.Contains("fell under the 2,048 MB alert threshold and has not recovered past 3,072 MB", row.What);
            Assert.Equal("Memory", row.Where);
            Assert.Equal((1, "Memory, 1 needs you"), rig.Badge("memory"));
            Assert.Equal(TrayState.Pressure, rig.Icon.State);

            // The recovery band: over the floor, not yet 1 GB over it. Still an episode, and its words still true.
            Check(Fresh(WindowsSnapshot(available: 2900)));
            Assert.Equal(PressureNeeds.EpisodeWords(since, 2048, 2048), Assert.Single(table.Lines).What);
            Assert.Equal(TrayState.Pressure, rig.Icon.State);

            // A stale sample decides nothing: the episode stays, and the check's problem is a row of its own.
            Check(Stale(WindowsSnapshot(available: 9000), TimeSpan.FromMinutes(3), "Windows"));
            Assert.Equal([PressureNeeds.PressureWord, PressureNeeds.UnknownWord], table.Lines.Select(l => l.State));
            Assert.Equal(PressureNeeds.ProblemWords("The Windows logger last wrote 3 minutes ago."), table.Lines[1].What);
            Assert.Equal((2, "Memory, 2 need you"), rig.Badge("memory"));
            Assert.Equal(TrayState.Pressure, rig.Icon.State);
            Assert.Contains("memory check stale", rig.Icon.Tooltip);

            // A saved floor change: 2,900 MB is more than 1 GB over a 1,024 MB floor, so the episode ends.
            Check(Fresh(WindowsSnapshot(available: 2900)));
            Assert.Single(table.Lines);
            Assert.True(rig.Store.Update(s => s with { PressureFloorMb = 1024 }).Succeeded);
            Check(Fresh(WindowsSnapshot(available: 2900)));
            Assert.Empty(table.Lines);
            Assert.Equal(NeedsYouTable.Nothing, table.Empty);
            Assert.Equal((0, "Memory"), rig.Badge("memory"));
            Assert.Equal(TrayState.Normal, rig.Icon.State);

            // The logger stops: the check can't read, a row with its reason; it starts again: the row goes.
            Check(Missing("windows"));
            row = Assert.Single(table.Lines);
            Assert.Equal(PressureNeeds.UnknownWord, row.State);
            Assert.Contains("current-windows.json is not there: its logger has not written it.", row.What);
            Assert.Equal(TrayState.NeedsYou, rig.Icon.State);
            Assert.Null(table.Empty);                                                 // never "Nothing needs you" beside a failed check
            Check(Fresh(WindowsSnapshot(available: 9000)));
            Assert.Empty(table.Lines);
            Assert.Equal(TrayState.Normal, rig.Icon.State);

            Assert.Equal(reads, rig.Memory.WindowsReads);                             // the hidden Overview read nothing itself
        });
    }

    [Fact]
    public void A_threshold_lowered_during_an_episode_keeps_what_the_episode_fell_under()
    {
        // Round 1: a 4,096 MB floor, 3,800 MB available; the floor lowered to 3,072 MB with 3,800 MB still available.
        // The episode lasts (recovery needs over 4,096 MB), and its row must not claim memory fell under 3,072 MB.
        On(() =>
        {
            Reading<SnapshotDoc> reading = Fresh(WindowsSnapshot(available: 9000));
            var rig = new OverviewRig().WithTray(() => reading);
            Assert.True(rig.Store.Update(s => s with { PressureFloorMb = 4096 }).Succeeded);
            reading = Fresh(WindowsSnapshot(available: 3800));
            rig.RunCheck();
            var since = rig.Check!.Since!.Value;
            Assert.Contains("fell under the 4,096 MB alert threshold and has not recovered past 5,120 MB", Assert.Single(rig.Page.Table.Lines).What);

            Assert.True(rig.Store.Update(s => s with { PressureFloorMb = 3072 }).Succeeded);
            rig.RunCheck();
            var what = Assert.Single(rig.Page.Table.Lines).What;
            Assert.Equal(PressureNeeds.EpisodeWords(since, 4096, 3072), what);
            Assert.Contains("fell under the 4,096 MB alert threshold and has not recovered past 4,096 MB", what);
            Assert.DoesNotContain("under the 3,072 MB", what);
        });
    }

    [Fact]
    public void Before_the_pressure_check_has_looked_the_table_does_not_say_nothing_needs_you()
    {
        On(() =>
        {
            var rig = new OverviewRig();                                              // no tray: no check has run
            Assert.Equal(NeedsYouTable.Looking, rig.Page.Table.Empty);
            rig.WithTray(() => Fresh(WindowsSnapshot()));
            Assert.Equal(NeedsYouTable.Nothing, rig.Page.Table.Empty);
        });
    }

    [Fact]
    public void The_Queue_badge_has_one_owner_right_before_Queue_is_visited_and_says_what_it_counts()
    {
        On(() =>
        {
            var rig = new OverviewRig();
            // At startup, Queue never visited: the Overview's reading already counts its two waiters.
            Assert.Equal((2, "Queue, 2 waiting"), rig.Badge("queue"));

            // A change before Queue is visited.
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.Status($"{QueueFixtures.Free(0)},{QueueFixtures.Free(1)}",
                QueueFixtures.Waiter(700, "\"slot\"")), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal((1, "Queue, 1 waiting"), rig.Badge("queue"));

            // On Queue, its own readings publish to the same count.
            rig.GoTo("queue");
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.BusyStatus(), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal((2, "Queue, 2 waiting"), rig.Badge("queue"));

            // A row of Needs you on Queue adds one, said as what it is.
            var provider = new FakeProvider();
            rig.Page.Needs.Add(provider);
            provider.Say(FakeProvider.Row("stuck"));
            OverviewRig.Settle();
            Assert.Equal((3, "Queue, 2 waiting, 1 needs you"), rig.Badge("queue"));

            // The last waiter and the last row go: no badge.
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.IdleStatus(), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal((1, "Queue, 1 needs you"), rig.Badge("queue"));
            provider.Say();
            OverviewRig.Settle();
            Assert.Equal((0, "Queue"), rig.Badge("queue"));
        });
    }

    [Fact]
    public void The_tray_shows_Needs_you_for_two_rows_and_Normal_for_none()
    {
        On(() =>
        {
            var rig = new OverviewRig().WithTray(() => Fresh(WindowsSnapshot()));
            Assert.Equal((TrayState.Normal, TrayViewModel.NormalTooltip), (rig.Icon.State, rig.Icon.Tooltip));
            var provider = new FakeProvider();
            rig.Page.Needs.Add(provider);
            provider.Say(FakeProvider.Row("one"), FakeProvider.Row("two", section: "instances"));
            OverviewRig.Settle();
            Assert.Equal((TrayState.NeedsYou, "Bosun Panel: 2 things need you"), (rig.Icon.State, rig.Icon.Tooltip));
            provider.Say();
            OverviewRig.Settle();
            Assert.Equal((TrayState.Normal, TrayViewModel.NormalTooltip), (rig.Icon.State, rig.Icon.Tooltip));
        });
    }

    [Fact]
    public void The_spoken_badge_says_each_kind_in_words()
    {
        Assert.Equal("Queue", RailRowName.Of("Queue", 0, 0));
        Assert.Equal("Queue, 2 waiting", RailRowName.Of("Queue", 2, 0));
        Assert.Equal("Memory, 1 needs you", RailRowName.Of("Memory", 0, 1));
        Assert.Equal("Memory, 2 need you", RailRowName.Of("Memory", 0, 2));
        Assert.Equal("Queue, 2 waiting, 1 needs you", RailRowName.Of("Queue", 2, 1));
    }
}
