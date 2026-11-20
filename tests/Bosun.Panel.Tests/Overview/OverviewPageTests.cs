using Bosun.Core.Memory;
using Bosun.Panel.Instances;
using Bosun.Panel.Memory;
using Bosun.Panel.Overview;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Instances;
using Bosun.Panel.Tests.Queue;
using Xunit;
using static Bosun.Panel.Tests.Instances.InstanceFixtures;
using static Bosun.Panel.Tests.Overview.OverviewFixtures;

namespace Bosun.Panel.Tests.Overview;

/// <summary>
/// The Overview (D03 T01 §5, design/SPEC.md §2): each source read on its own while shown and nothing while hidden, a
/// held read holding back nothing else, the header's Refresh and Retry (also through Ctrl+K), and the four cards'
/// words and gauges from shared fakes, each as its own page says it.
/// </summary>
public class OverviewPageTests
{
    static void On(Action test) => OverviewRig.On(test);

    // ---- reading ----

    [Fact]
    public void Each_source_is_read_at_its_own_pace_while_shown_and_nothing_while_hidden()
    {
        On(() =>
        {
            var rig = new OverviewRig();
            Assert.Same(rig.Page, rig.Shell.Page);                                  // what opens first
            Assert.Equal((1, 1, 1, 1), (rig.Memory.WindowsReads, rig.Memory.GuestReads, rig.Queue.StatusReads, rig.Instances.Reads));
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal((2, 2, 2, 1), (rig.Memory.WindowsReads, rig.Memory.GuestReads, rig.Queue.StatusReads, rig.Instances.Reads));
            rig.Advance(TimeSpan.FromSeconds(25));
            Assert.Equal(2, rig.Instances.Reads);                                   // every 30 s
            Assert.Equal(0, rig.Memory.Reports);                                    // the Memory page's report is its own
            Assert.Equal(0, rig.Queue.HistoryReads + rig.Queue.LimitsReads);        // and so are the queue's history and limits

            rig.GoTo("settings");
            var (w, q, i) = (rig.Memory.WindowsReads, rig.Queue.StatusReads, rig.Instances.Reads);
            rig.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal((w, q, i), (rig.Memory.WindowsReads, rig.Queue.StatusReads, rig.Instances.Reads));  // hidden: nothing
        });
    }

    [Fact]
    public void A_held_queue_read_holds_back_neither_the_memory_cards_nor_the_page()
    {
        On(() =>
        {
            var held = new TaskCompletionSource();
            var rig = new OverviewRig(queue: new FakeQueueSource { HoldStatus = held });
            Assert.Equal(PageState.Data, rig.Page.State);                            // memory answered: the page has data
            Assert.Equal("20.0 GB", rig.Page.Windows.Said.Split(',')[0]);
            Assert.Equal(OverviewPage.Checking, rig.Page.Queue.Said);                // the queue's card waits for its own
            held.SetResult();
            OverviewRig.Settle();
            Assert.Equal("1 running, 2 waiting", rig.Page.Queue.Said);
        });
    }

    [Fact]
    public void Hidden_and_shown_again_with_a_read_held_the_old_read_is_dropped_and_a_fresh_one_shows()
    {
        On(() =>
        {
            var held = new TaskCompletionSource();
            var rig = new OverviewRig(queue: new FakeQueueSource { HoldStatus = held });
            rig.GoTo("settings");                                                   // the held read is cancelled
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.IdleStatus(), QueueFixtures.Now);
            held.SetResult();
            OverviewRig.Settle();
            Assert.Equal(OverviewPage.Checking, rig.Page.Queue.Said);                // nothing applied while hidden
            rig.GoTo("overview");
            Assert.Equal(QueueSummary.Idle, rig.Page.Queue.Said);                    // the fresh read, never the old busy one
            Assert.Equal(0, rig.PageOf("queue").Waiting);
        });
    }

    [Fact]
    public void Refresh_reads_every_source_and_Retry_after_all_failed_recovers_both_from_the_header_and_Ctrl_K()
    {
        On(() =>
        {
            var rig = new OverviewRig();
            var (w, q, i) = (rig.Memory.WindowsReads, rig.Queue.StatusReads, rig.Instances.Reads);
            rig.Page.RefreshNow!.Execute(null);
            OverviewRig.Settle();
            Assert.Equal((w + 1, q + 1, i + 1), (rig.Memory.WindowsReads, rig.Queue.StatusReads, rig.Instances.Reads));

            // Every source answers with nothing: Failed, quoting each.
            rig.Memory.Windows = Missing("windows");
            rig.Memory.Guest = Missing("wsl");
            rig.Queue.Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            rig.Instances.Answer = View(null, Failed("windows", null, "bosun instances --json could not be started."));
            rig.Shell.Palette.Open();
            var refresh = rig.Shell.Palette.Rows.Single(r => r.Name == "Refresh" && r.Group == "Overview");
            Assert.True(OverviewRig.Wait(rig.Shell.Palette.RunAsync(refresh)));
            OverviewRig.Settle();
            Assert.Equal(PageState.Failed, rig.Page.State);
            Assert.Contains("bosun.exe is not installed.", rig.Page.FailureMessage);
            Assert.Contains("bosun instances --json could not be started.", rig.Page.FailureMessage);
            Assert.Contains("current-windows.json is not there", rig.Page.FailureMessage);

            // Retry through Ctrl+K, once the sources answer again.
            rig.Memory.Windows = Fresh(WindowsSnapshot());
            rig.Shell.Palette.Open();
            var retry = rig.Shell.Palette.Rows.Single(r => r.Name == "Retry" && r.Group == "Overview");
            Assert.True(OverviewRig.Wait(rig.Shell.Palette.RunAsync(retry)));
            OverviewRig.Settle();
            Assert.Equal(PageState.Data, rig.Page.State);
            Assert.StartsWith("20.0 GB", rig.Page.Windows.Said, StringComparison.Ordinal);

            // And the failed state's own Retry, the same command the frame's button runs.
            rig.Memory.Windows = Missing("windows");
            rig.Page.RefreshNow.Execute(null);
            OverviewRig.Settle();
            Assert.Equal(PageState.Failed, rig.Page.State);
            rig.Memory.Windows = Fresh(WindowsSnapshot());
            rig.Page.Retry!.Execute(null);
            OverviewRig.Settle();
            Assert.Equal(PageState.Data, rig.Page.State);
        });
    }

    // ---- the cards ----

    [Fact]
    public void The_memory_cards_read_Windows_and_the_guest_each_from_its_own_snapshot_as_text_and_gauge()
    {
        On(() =>
        {
            var rig = new OverviewRig();
            var windows = rig.Page.Windows;
            Assert.Equal("Windows available: 20.0 GB, Safe. Open Memory", windows.OpenName);
            Assert.Equal("20.0 GB available of 32.0 GB", windows.Note);
            Assert.Equal(20.0 / 32.0, windows.Fraction);
            Assert.Equal("", windows.Warning);
            var guest = rig.Page.Guest;
            Assert.Equal("WSL guest available: 6.0 GB, Free. Open Memory", guest.OpenName);
            Assert.Equal("6.0 GB available of 14.0 GB", guest.Note);
            Assert.Equal(6.0 / 14.0, guest.Fraction);

            // The card says what the Memory page says of the same snapshot.
            rig.GoTo("memory");
            Assert.StartsWith(windows.Note + ";", ((MemoryPage)rig.PageOf("memory")).WindowsNote, StringComparison.Ordinal);
            Assert.Equal("Cap 14.0 GB", ((MemoryPage)rig.PageOf("memory")).WslNote);
        });
    }

    [Fact]
    public void The_Windows_word_is_judged_against_the_stored_threshold_and_its_recovery_band()
    {
        Assert.Equal(("Low", Bosun.Panel.Controls.StatusKind.Danger), OverviewPage.WindowsWord(2000, 2048));
        Assert.Equal(("Tight", Bosun.Panel.Controls.StatusKind.Warn), OverviewPage.WindowsWord(3072, 2048));
        Assert.Equal(("Safe", Bosun.Panel.Controls.StatusKind.Ok), OverviewPage.WindowsWord(3073, 2048));
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory { Windows = Fresh(WindowsSnapshot(available: 2500)) });
            Assert.Equal("Tight", rig.Page.Windows.Word);
            Assert.True(rig.Store.Update(s => s with { PressureFloorMb = 4096 }).Succeeded);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("Low", rig.Page.Windows.Word);                              // the saved threshold, at the next read
        });
    }

    [Fact]
    public void Stale_Windows_data_keeps_its_figures_with_its_age()
    {
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory { Windows = Stale(WindowsSnapshot(), TimeSpan.FromMinutes(12), "Windows") });
            Assert.Equal("20.0 GB available of 32.0 GB", rig.Page.Windows.Note);
            Assert.Equal("Not current: The Windows logger last wrote 12 minutes ago.", rig.Page.Windows.Warning);
            Assert.Equal(PageState.Data, rig.Page.State);
        });
    }

    [Fact]
    public void A_running_VM_with_a_stale_guest_keeps_the_guest_figures_marked_with_their_age()
    {
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory { Guest = Stale(GuestSnapshot(), TimeSpan.FromMinutes(4), "WSL") });
            Assert.Equal(6.0 / 14.0, rig.Page.Guest.Fraction);
            Assert.Equal("Not current: The WSL logger last wrote 4 minutes ago.", rig.Page.Guest.Warning);
        });
    }

    [Fact]
    public void A_stopped_VM_says_WSL_not_running_with_its_last_sample_or_that_it_has_never_reported()
    {
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory
            {
                Windows = Fresh(WindowsSnapshot(vmRunning: false)),
                Guest = Stale(GuestSnapshot(), TimeSpan.FromMinutes(20), "WSL"),
            });
            var guest = rig.Page.Guest;
            Assert.Equal(OverviewPage.WslNotRunning, guest.Said);
            Assert.False(guest.ShowGauge);
            Assert.Null(guest.Fraction);
            Assert.Equal("Last sample 20 minutes ago: 6.0 GB available of 14.0 GB.", guest.Note);

            rig.Memory.Guest = Missing("wsl");
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal(OverviewPage.WslNotRunning, guest.Said);
            Assert.Equal(OverviewPage.WslNeverReported, guest.Note);
        });
    }

    [Fact]
    public void A_stale_Windows_snapshot_saying_the_VM_stopped_never_hides_a_current_guest_sample()
    {
        // Round 1: the Windows logger stopped after it saw a stopped VM; WSL started since, and its guest logger writes.
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory
            {
                Windows = Stale(WindowsSnapshot(vmRunning: false), TimeSpan.FromMinutes(5), "Windows"),
                Guest = Fresh(GuestSnapshot()),
            });
            var guest = rig.Page.Guest;
            Assert.NotEqual(OverviewPage.WslNotRunning, guest.Said);
            Assert.Equal(6.0 / 14.0, guest.Fraction);
            Assert.Equal("6.0 GB available of 14.0 GB", guest.Note);

            // A stale Windows snapshot and a stale guest: whether the VM runs is not known, so neither is said.
            rig.Memory.Guest = Stale(GuestSnapshot(), TimeSpan.FromMinutes(20), "WSL");
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.NotEqual(OverviewPage.WslNotRunning, guest.Said);
            Assert.Equal("Not current: The WSL logger last wrote 20 minutes ago.", guest.Warning);
        });
    }

    [Fact]
    public void A_sample_with_more_available_than_total_is_Not_available_never_a_Safe_verdict()
    {
        // Round 1: 32,768 MB available of 4,096 MB read as Safe with a full gauge.
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory
            {
                Windows = Fresh(WindowsSnapshot(available: 32768, total: 4096)),
                Guest = Fresh(GuestSnapshot(available: 20000, total: 14336)),
            });
            Assert.IsType<NotAvailable>(rig.Page.Windows.Value);
            Assert.Null(rig.Page.Windows.Fraction);
            Assert.NotEqual("Safe", rig.Page.Windows.Word);
            Assert.Equal(OverviewPage.Impossible("Windows", 32768, 4096), rig.Page.Windows.Note);
            Assert.IsType<NotAvailable>(rig.Page.Guest.Value);
            Assert.Equal(OverviewPage.Impossible("WSL", 20000, 14336), rig.Page.Guest.Note);
        });
    }

    [Fact]
    public void A_snapshot_that_cant_be_read_is_Not_available_with_its_reason_on_an_empty_track_never_0()
    {
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory { Windows = Missing("windows"), Guest = Missing("wsl") });
            var windows = rig.Page.Windows;
            Assert.IsType<NotAvailable>(windows.Value);
            Assert.True(windows.ShowGauge);
            Assert.Null(windows.Fraction);
            Assert.Contains("current-windows.json is not there", windows.Note);
            Assert.StartsWith("Windows available: Not available: ", windows.OpenName, StringComparison.Ordinal);
            // The VM's state is the Windows side's to say: unknown, the guest's own reason is said.
            Assert.Equal(OverviewPage.WslNeverReported, rig.Page.Guest.Note);
            Assert.Equal(PageState.Data, rig.Page.State);                            // the queue and instances have data
            Assert.Empty(rig.Page.Needs.Rows);                                       // a card's failed read is not a row
        });
    }

    [Fact]
    public void The_queue_card_says_the_queue_as_QueueSummary_does_and_as_the_Queue_page_does()
    {
        On(() =>
        {
            var rig = new OverviewRig();
            Assert.Equal(QueueSummary.WordsOf(QueueFixtures.BusyStatus()), rig.Page.Queue.Said);
            Assert.Equal("1 running, 2 waiting", rig.Page.Queue.Said);
            Assert.Equal("1 of 2 slots in use", rig.Page.Queue.Note);
            rig.GoTo("queue");
            Assert.Equal(((QueuePage)rig.PageOf("queue")).LiveWords, rig.Page.Queue.Said);

            rig.Queue.Status = Reading<StatusDoc>.Fail(ReadingProblem.Failed, "bosun status --json ended with exit code 1: the records folder can't be listed.");
            rig.GoTo("overview");
            Assert.Equal("Not available: bosun status --json ended with exit code 1: the records folder can't be listed.", rig.Page.Queue.Said);
        });
    }

    public static TheoryData<string, string, string, string> Scans => new()
    {
        // A WSL job seen by both sides is one, on its WSL side.
        { "both", "5", "Windows 3, WSL (Ubuntu) 2", "" },
        // A stale distro's rows are not counted, and it is said.
        { "stale", "1", "Windows 1", "WSL (Ubuntu) last reported 12 minutes ago: it is stopped, or its logger is." },
        // A side that could not be read is said beside the count of the side that could.
        { "unread", "1", "WSL (Ubuntu) 1", "Windows: bosun instances --json did not answer within 20 s and was stopped." },
        // A scan that read everything and found nothing.
        { "empty", "0", "Nothing is running on either side.", "" },
        // No distro reported: WSL is not counted, and said.
        // No distro reported (a first run) or the reports folder can't be listed: the reader's own words, each its own cause.
        { "no-wsl", "1", "Windows 1", FirstRun },
        { "unlisted", "1", "Windows 1", Unlisted },
    };

    // InstanceClient's own words for each cause, with their recovery.
    const string FirstRun = "No WSL distro has reported its instances to C:\\logs, so only Windows is listed. Each distro's logger reports every 30 s once it runs (wsl/install.sh sets it up).";
    const string Unlisted = "The WSL reports in C:\\logs can't be listed (Access to the path is denied.), so only Windows is listed.";

    static Bosun.Core.Instances.InstancesView ScanOf(string kind) => kind switch
    {
        "both" => Both(),
        "stale" => View(null, Win(I("claude", 100, repo: "D:/Bosun")), StaleWsl("Ubuntu", TimeSpan.FromMinutes(12), I("claude", 300, side: "wsl", repo: "/home/me/app"))),
        "unread" => View(null, Failed("windows", null, "bosun instances --json did not answer within 20 s and was stopped."), Wsl("Ubuntu", I("claude", 300, side: "wsl"))),
        "empty" => View(null, Win(), Wsl("Ubuntu")),
        "unlisted" => View(Unlisted, Win(I("claude", 100))),
        _ => View(FirstRun, Win(I("claude", 100))),
    };

    [Theory]
    [MemberData(nameof(Scans))]
    public void The_instances_card_counts_the_merged_live_instances_once_by_side_with_what_could_not_be_read(string kind, string count, string note, string warning)
    {
        On(() =>
        {
            var rig = new OverviewRig(instances: new FakeInstances { Answer = ScanOf(kind) });
            var card = rig.Page.Instances;
            Assert.Equal(count, card.Said);
            Assert.Equal(note, card.Note);
            Assert.Equal(warning, card.Warning);
            // What it says could not be read is what the Instances page says, word for word.
            rig.GoTo("instances");
            foreach (var notice in ((InstancesPage)rig.PageOf("instances")).Notices) Assert.Contains(notice, card.Warning);
        });
    }

    [Fact]
    public void An_editor_with_three_open_windows_counts_as_one_instance()
    {
        // D03 T02 §4: a row each window root, one process.
        On(() =>
        {
            var rig = new OverviewRig(instances: new FakeInstances
            {
                Answer = View(null, Win(
                    EditorWindow(500, "s1#0", @"D:\one\app", "D:/one/app", 4096),
                    EditorWindow(500, "s2#0", @"E:\two\app", "E:/two/app", null),
                    EditorWindow(500, "s3#0", null, null, null),
                    I("claude", 100, cwd: @"D:\one\app", repo: "D:/one/app"))),
            });
            Assert.Equal("2", rig.Page.Instances.Said);
            Assert.Equal("Windows 2", rig.Page.Instances.Note);
        });
    }

    [Fact]
    public void An_instances_scan_with_only_stale_reports_and_failures_is_Not_available_never_a_measured_0()
    {
        // Round 1: a stale WSL report and bosun.exe unavailable showed "0" beside the failures.
        On(() =>
        {
            var rig = new OverviewRig(instances: new FakeInstances
            {
                Answer = View(null, Failed("windows", null, "bosun instances --json could not be started."),
                    StaleWsl("Ubuntu", TimeSpan.FromMinutes(12), I("claude", 300, side: "wsl"))),
            });
            var card = rig.Page.Instances;
            Assert.IsType<NotAvailable>(card.Value);
            Assert.NotEqual("0", card.Said);
            Assert.Contains("bosun instances --json could not be started.", card.Said);
            Assert.Contains("WSL (Ubuntu) last reported 12 minutes ago", card.Said);
        });
    }

    [Fact]
    public void Ten_thousand_problems_from_one_source_are_one_notice_saying_how_many_more()
    {
        // Round 1: 10,000 problems each became a notice, copied on the UI thread into one warning.
        var problems = Enumerable.Range(0, 10_000).Select(i => new Bosun.Core.Instances.SourceProblemDoc { Source = "processes", Why = $"x{i}" }).ToList();
        var side = new Bosun.Core.Instances.SideReading("wsl", "Ubuntu",
            Reading<Bosun.Core.Instances.InstancesDoc>.Of(Doc("wsl", "Ubuntu", [], problems), InstanceFixtures.Now));
        var notices = InstancesPage.NoticesOf(View(null, Win(), side));
        Assert.Equal(["WSL (Ubuntu): its process list could not be read: x0 (and 9,999 more)"], notices);
    }

    [Fact]
    public void An_instances_scan_with_every_side_unread_is_Not_available_with_each_reason()
    {
        On(() =>
        {
            var rig = new OverviewRig(instances: new FakeInstances
            {
                Answer = View(null, Failed("windows", null, "bosun instances --json could not be started."), Failed("wsl", "Ubuntu", "instances-wsl-Ubuntu.json can't be read: denied.")),
            });
            Assert.Equal("Not available: Windows: bosun instances --json could not be started. WSL (Ubuntu): instances-wsl-Ubuntu.json can't be read: denied.",
                rig.Page.Instances.Said);
        });
    }

    // ---- completion review: each card on its own, the VM's state, impossible figures, the queue, instances, failure ----

    [Fact]
    public void A_held_guest_read_holds_back_the_Windows_card_and_a_held_Windows_read_holds_back_the_guest_card()
    {
        On(() =>
        {
            var guestHeld = new TaskCompletionSource();
            var rig = new OverviewRig(new FakeMemory { HoldGuest = guestHeld });
            Assert.StartsWith("20.0 GB", rig.Page.Windows.Said, StringComparison.Ordinal);   // read, while the guest's file is held
            Assert.Equal(OverviewPage.Checking, rig.Page.Guest.Said);
            guestHeld.SetResult();
            OverviewRig.Settle();
            Assert.StartsWith("6.0 GB", rig.Page.Guest.Said, StringComparison.Ordinal);

            var windowsHeld = new TaskCompletionSource();
            var reverse = new OverviewRig(new FakeMemory { HoldWindows = windowsHeld });
            Assert.StartsWith("6.0 GB", reverse.Page.Guest.Said, StringComparison.Ordinal);
            Assert.Equal(OverviewPage.Checking, reverse.Page.Windows.Said);
            windowsHeld.SetResult();
            OverviewRig.Settle();
            Assert.StartsWith("20.0 GB", reverse.Page.Windows.Said, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_Windows_sample_newer_than_the_guests_saying_the_VM_stopped_is_a_shutdown_and_a_guest_sample_newer_is_a_restart()
    {
        On(() =>
        {
            // Both fresh. Shutdown: the guest wrote at 08:59:40, Windows saw the VM stopped at 08:59:55.
            var rig = new OverviewRig(new FakeMemory
            {
                Windows = Fresh(WindowsSnapshot(vmRunning: false, at: "2026-11-18T08:59:55Z")),
                Guest = Fresh(GuestSnapshot(at: "2026-11-18T08:59:40Z")),
            });
            Assert.Equal(OverviewPage.WslNotRunning, rig.Page.Guest.Said);
            Assert.Equal("Last sample less than a minute ago: 6.0 GB available of 14.0 GB.", rig.Page.Guest.Note);
            rig.GoTo("memory");                                                      // the Memory page says the same
            Assert.Equal("Not running", ((Metric)((MemoryPage)rig.PageOf("memory")).WslVm!).Text);

            // Restart: Windows saw it stopped at 08:59:40, the guest has written since, at 08:59:55.
            rig.Memory.Windows = Fresh(WindowsSnapshot(vmRunning: false, at: "2026-11-18T08:59:40Z"));
            rig.Memory.Guest = Fresh(GuestSnapshot(at: "2026-11-18T08:59:55Z"));
            rig.GoTo("overview");
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.NotEqual(OverviewPage.WslNotRunning, rig.Page.Guest.Said);
            Assert.Equal(6.0 / 14.0, rig.Page.Guest.Fraction);
            rig.GoTo("memory");
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.IsNotType<Metric>(((MemoryPage)rig.PageOf("memory")).WslVm);      // not "Not running": it restarted
        });
    }

    [Fact]
    public void An_impossible_Windows_sample_is_no_reading_for_the_pressure_check_neither_starting_nor_ending_an_episode()
    {
        On(() =>
        {
            // The sample says 1,000 MB available of 500 MB in all: under the floor, but it can't be.
            var sample = Fresh(WindowsSnapshot(available: 1000, total: 500));
            var rig = new OverviewRig().WithTray(() => sample);
            rig.RunCheck();
            Assert.False(rig.Check!.InEpisode);                                      // it did not start one
            Assert.Contains("can't be", rig.Check.Problem);
            Assert.Contains(rig.Page.Needs.Rows, row => row.Key == "check");         // a check problem with its reason

            sample = Fresh(WindowsSnapshot(available: 1000));                        // a real one under the floor: an episode
            rig.RunCheck();
            Assert.True(rig.Check.InEpisode);
            sample = Fresh(WindowsSnapshot(available: 9000, total: 4000));           // impossible and over the floor: it does not end it
            rig.RunCheck();
            Assert.True(rig.Check.InEpisode);
            Assert.Contains("can't be", rig.Check.Problem);
            sample = Fresh(WindowsSnapshot(available: 9000));                        // a real one over the recovery band: it ends
            rig.RunCheck();
            Assert.False(rig.Check.InEpisode);
            Assert.Null(rig.Check.Problem);
        });
    }

    [Fact]
    public void A_stopped_guests_last_sample_prints_no_impossible_figures()
    {
        On(() =>
        {
            var rig = new OverviewRig(new FakeMemory
            {
                Windows = Fresh(WindowsSnapshot(vmRunning: false)),
                Guest = Stale(GuestSnapshot(available: 20000, total: 14336), TimeSpan.FromMinutes(20), "WSL"),
            });
            Assert.Equal(OverviewPage.WslNotRunning, rig.Page.Guest.Said);
            Assert.Equal("Last sample 20 minutes ago.", rig.Page.Guest.Note);
        });
    }

    [Fact]
    public void The_queue_card_draws_the_in_use_fraction_when_every_slot_is_known_and_never_claims_a_count_when_one_is_not()
    {
        On(() =>
        {
            // Known: one of two slots in use.
            var rig = new OverviewRig();
            var queue = rig.Page.Queue;
            Assert.True(queue.ShowGauge);
            Assert.Equal(0.5, queue.Fraction);
            Assert.Equal("1 of 2 slots in use", queue.Note);
            Assert.Equal("Queue: 1 running, 2 waiting. Open Queue", queue.OpenName);
            Assert.Equal("1 of 2 slots in use", queue.Help);

            // Partly unknown: one free, one that could not be checked.
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.Status($"{QueueFixtures.Free(0)},{QueueFixtures.Held(1, null, busy: null)}"), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.False(queue.ShowGauge);
            Assert.Null(queue.Fraction);
            Assert.Equal("1 of 2 slots not known", queue.Note);
            Assert.Equal("Queue: 1 slot not known. Open Queue", queue.OpenName);
            Assert.Equal("1 of 2 slots not known", queue.Help);

            // Partly unknown with one held: at least that many.
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.Status($"{QueueFixtures.Held(0, QueueFixtures.Holder(0))},{QueueFixtures.Held(1, null, busy: null)}"), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("At least 1 in use, 1 of 2 slots not known", queue.Note);
            Assert.Equal("Queue: 1 running, 1 slot not known. Open Queue", queue.OpenName);

            // Wholly unknown.
            rig.Queue.Status = Reading<StatusDoc>.Of(QueueFixtures.Status($"{QueueFixtures.Held(0, null, busy: null)},{QueueFixtures.Held(1, null, busy: null)}"), QueueFixtures.Now);
            rig.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("2 of 2 slots not known", queue.Note);
            Assert.Equal("Queue: 2 slots not known. Open Queue", queue.OpenName);
            Assert.False(queue.ShowGauge);
        });
    }

    [Fact]
    public void The_instances_card_keeps_the_readers_own_reason_for_missing_WSL_reports_in_the_count_and_when_every_side_failed()
    {
        On(() =>
        {
            foreach (var reason in new[] { FirstRun, Unlisted })
            {
                var counted = new OverviewRig(instances: new FakeInstances { Answer = View(reason, Win(I("claude", 100))) });
                Assert.Contains(reason, counted.Page.Instances.Warning);

                var failed = new OverviewRig(instances: new FakeInstances { Answer = View(reason, Failed("windows", null, "bosun instances --json could not be started.")) });
                Assert.IsType<NotAvailable>(failed.Page.Instances.Value);
                Assert.Contains("bosun instances --json could not be started.", failed.Page.Instances.Said);
                Assert.Contains(reason, failed.Page.Instances.Said);
            }
        });
    }

    [Fact]
    public void Every_card_without_a_usable_value_is_Failed_with_Retry_even_when_a_source_parsed()
    {
        On(() =>
        {
            var rig = new OverviewRig(
                new FakeMemory { Windows = Fresh(WindowsSnapshot(available: 32768, total: 4096)), Guest = Fresh(GuestSnapshot(total: null)) },
                new FakeQueueSource { Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.") },
                new FakeInstances { Answer = View(null, Failed("windows", null, "bosun instances --json could not be started."), StaleWsl("Ubuntu", TimeSpan.FromMinutes(12), I("claude", 300, side: "wsl"))) });
            Assert.All(rig.Page.Cards, card => Assert.IsType<NotAvailable>(card.Value));
            Assert.Equal(PageState.Failed, rig.Page.State);
            Assert.Contains("can't be", rig.Page.FailureMessage);
            Assert.Contains("bosun.exe is not installed.", rig.Page.FailureMessage);
            Assert.Contains("WSL (Ubuntu) last reported 12 minutes ago", rig.Page.FailureMessage);
            Assert.NotNull(rig.Page.Retry);

            // One card with a value takes it out of Failed.
            rig.Memory.Windows = Fresh(WindowsSnapshot());
            rig.Page.Retry!.Execute(null);
            OverviewRig.Settle();
            Assert.Equal(PageState.Data, rig.Page.State);
        });
    }
}
