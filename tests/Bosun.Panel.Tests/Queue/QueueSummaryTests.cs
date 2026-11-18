using System.Windows.Controls;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Tests.Tray;
using Bosun.Panel.Tray;
using Xunit;
using static Bosun.Panel.Tests.Queue.QueueFixtures;
using static Bosun.Panel.Tests.Queue.QueuePageTests;

namespace Bosun.Panel.Tests.Queue;

/// <summary>
/// The tray's Queue line (D03 T02 §3, D02 T02 §7): the queue's words from <see cref="QueueSummary"/>, read once each
/// time the menu opens while the Queue page is not on screen, the page's own readings while it is, and nothing read
/// on a timer: a hidden panel polls nothing (design/SPEC.md §1).
/// </summary>
public class QueueSummaryTests
{
    sealed record Tray(QueuePage Page, ShellViewModel Shell, ActivityLog Log, FakeQueueSource Source, FakeTrayIcon Icon, Action<TimeSpan> Advance)
    {
        /// <summary>The person opens the menu: its Queue line as it shows at once.</summary>
        public string Open()
        {
            var line = Icon.OpenMenu().Single(item => item.Id == TrayViewModel.Queue).Text;
            Settle();
            return line;
        }

        public int Warnings(string about) => Log.Entries.Count(e => e.Severity == LogSeverity.Warn && e.Message.Contains(about, StringComparison.Ordinal));
    }

    static void InTray(FakeQueueSource source, Action<Tray> test)
    {
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var page = new QueuePage(source, scheduler, log);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
            var store = new Bosun.Core.PanelSettingsStore(SettingsFixture.FilePath, new MemoryFiles());
            var icon = new FakeTrayIcon();
            var controller = new TrayController(shell, icon, new Notifier(store, log), store, _ => { }, queue: page.Words, refreshes: scheduler);
            controller.Start(new FakeWindow(), inTray: true);    // started for the tray: no page on screen
            Settle();
            test(new Tray(page, shell, log, source, icon, by =>
            {
                clock += by;
                scheduler.Tick();
                Settle();
            }));
            return 0;
        });
    }

    [Fact]
    public void Its_words_say_what_runs_and_waits_and_Idle_only_for_a_whole_reading_with_nothing()
    {
        Assert.Equal("1 running, 2 waiting", QueueSummary.WordsOf(BusyStatus()));
        Assert.Equal(QueueSummary.Idle, QueueSummary.WordsOf(IdleStatus()));
        Assert.Equal("1 record not readable", QueueSummary.WordsOf(Status($"{Free(0)},{Free(1)}", skipped: 1)));
        Assert.Equal("1 slot not known", QueueSummary.WordsOf(Status($"{Held(0, null, busy: null)},{Free(1)}")));
        Assert.Equal("records not readable", QueueSummary.WordsOf(Status($"{Free(0)},{Free(1)}", readable: false)));
    }

    [Fact]
    public void A_free_slot_above_the_limit_is_not_said_to_be_kept_by_a_job()
    {
        // Round 1: three jobs, the limit lowered to one, the middle one ended: its free slot is listed above the limit,
        // and only the still-held one is said to be kept until its job ends.
        var status = Status($"{Held(0, Holder(0))},{Free(1, above: true)},{Held(2, Holder(2), above: true)}");
        var doubts = QueueSummary.Doubts(status);
        Assert.DoesNotContain(doubts, d => d.StartsWith("Slot 2 ", StringComparison.Ordinal));
        Assert.Contains(doubts, d => d.StartsWith("Slot 3 is above the slot limit", StringComparison.Ordinal));
    }

    [Fact]
    public void The_menu_opened_before_Queue_was_ever_shown_reads_once_and_its_next_opening_has_the_words()
    {
        var source = new FakeQueueSource();
        InTray(source, t =>
        {
            Assert.Equal(0, source.StatusReads);                        // nothing read in the tray
            var slow = source.HoldStatus = new TaskCompletionSource();  // a read that takes its time
            Assert.Equal("Queue: Checking", t.Open());                  // the last words at once: none yet, so Checking, never Not available
            Assert.Equal(1, source.StatusReads);                        // one read for the opening
            Assert.Equal("Queue: Checking", t.Open());                  // still under way: no second read
            Assert.Equal(1, source.StatusReads);
            slow.SetResult();
            Settle();
            Assert.Equal("Queue: 1 running, 2 waiting", t.Open());
            Assert.Equal(2, source.StatusReads);

            // The queue changes between openings.
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            source.HoldStatus = slow = new TaskCompletionSource();
            Assert.Equal("Queue: 1 running, 2 waiting", t.Open());      // the last words, while the read runs
            slow.SetResult();
            Settle();
            Assert.Equal("Queue: Idle", t.Open());
            Assert.Equal(0, source.HistoryReads + source.LimitsReads);   // the menu reads the status only
        });
    }

    [Fact]
    public void Nothing_is_read_while_hidden_with_the_menu_closed()
    {
        var source = new FakeQueueSource();
        InTray(source, t =>
        {
            t.Advance(TimeSpan.FromMinutes(10));
            t.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(0, source.StatusReads);
            t.Open();
            t.Advance(TimeSpan.FromMinutes(10));
            Assert.Equal(1, source.StatusReads);                        // the opening's one read, and no more
        });
    }

    [Fact]
    public void Opening_Queue_from_the_menu_shows_the_page_whose_readings_are_then_the_words()
    {
        var source = new FakeQueueSource();
        InTray(source, t =>
        {
            Wait(t.Icon.Choose(TrayViewModel.Queue));
            Settle();
            Assert.Same(t.Page, t.Shell.Page);
            Assert.True(t.Page.IsShown);
            var reads = source.StatusReads;                              // the page's first read, and the opening's
            Assert.Equal("Queue: 1 running, 2 waiting", t.Open());
            Assert.Equal(reads, source.StatusReads);                    // the page reads: the menu does not
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            t.Advance(TimeSpan.FromSeconds(6));                         // the page's own tick
            Assert.Equal("Queue: Idle", t.Open());
        });
    }

    [Fact]
    public void A_failure_is_one_Activity_line_each_time_it_changes()
    {
        var source = new FakeQueueSource { Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.") };
        InTray(source, t =>
        {
            t.Open();
            Assert.Equal("Queue: Not available", t.Open());
            t.Open();
            Assert.Equal(1, t.Warnings("The tray's Queue line says Not available: bosun.exe is not installed."));
            source.Status = Reading<StatusDoc>.Fail(ReadingProblem.Failed, "bosun status --json ran past 20 s.");
            t.Open();
            t.Open();
            Assert.Equal(1, t.Warnings("ran past 20 s."));
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            t.Open();
            Assert.Equal("Queue: Idle", t.Open());
            source.Status = Reading<StatusDoc>.Fail(ReadingProblem.Failed, "bosun status --json ran past 20 s.");
            t.Open();
            Assert.Equal("Queue: Not available", t.Open());
            Assert.Equal(2, t.Warnings("ran past 20 s."));             // failing again after it worked is a change
            Assert.Equal(3, t.Warnings("The tray's Queue line says Not available"));
        });
    }

    [Fact]
    public void One_opening_of_the_real_menu_moves_from_Checking_to_the_counts_without_reopening()
    {
        var source = new FakeQueueSource { HoldStatus = new TaskCompletionSource() };
        var hold = source.HoldStatus;
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
            var log = Shells.Log();
            var page = new QueuePage(source, scheduler, log);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
            var store = new Bosun.Core.PanelSettingsStore(SettingsFixture.FilePath, new MemoryFiles());
            var windows = new FakeTrayShell();
            var controller = new TrayController(shell, new NativeTrayIcon(windows), new Notifier(store, log), store, _ => { }, queue: page.Words, refreshes: scheduler);
            controller.Start(new FakeWindow(), inTray: true);
            Settle();
            // While Windows holds the menu open, the read the opening started ends.
            windows.WhileOpen = () =>
            {
                hold.SetResult();
                Settle();
            };
            windows.Send(FakeTrayShell.Callback, NativeTrayIcon.WM_CONTEXTMENU);
            Settle();
            Assert.Single(windows.Menus);                                        // one opening
            Assert.Contains((2, "Queue: Checking"), windows.Menus[0]);           // shown at once
            Assert.Equal([(2, "Queue: 1 running, 2 waiting")], windows.Changes); // and changed in place, its id kept
            Assert.Equal(1, source.StatusReads);
            return 0;
        });
    }

    [Fact]
    public void A_slow_tray_read_that_ends_after_a_newer_page_reading_is_dropped()
    {
        var source = new FakeQueueSource { HoldStatus = new TaskCompletionSource() };
        var slow = source.HoldStatus;
        InTray(source, t =>
        {
            t.Open();                                                            // the tray's read starts: busy, and slow
            Assert.Equal(1, source.StatusReads);
            source.Status = Reading<StatusDoc>.Of(IdleStatus(), Now);
            Wait(t.Shell.GoToAsync(t.Shell.Sections.Single(s => s.Key == "queue")));
            t.Shell.WindowShown();
            Settle();
            Assert.Equal(QueueSummary.Idle, t.Page.Words.Words);                // the page's newer reading
            slow.SetResult();                                                    // the older tray read ends last
            Settle();
            Assert.Equal(QueueSummary.Idle, t.Page.Words.Words);
            Assert.Equal("Queue: Idle", t.Open());
        });
    }
}
