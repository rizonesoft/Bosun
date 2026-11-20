using System.ComponentModel;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Instances;
using Bosun.Panel.Instances;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;
using static Bosun.Panel.Tests.Instances.InstanceFixtures;

namespace Bosun.Panel.Tests.Instances;

/// <summary>
/// The Instances page (D03 T02 §2, design/SPEC.md §4): every instance grouped by repo with each cell in words, a job's
/// slot, its refresh and states, each source that could not be read said by name, a stale distro's rows marked old,
/// numeric sorts, and Open folder and Open terminal from a row and from Ctrl+K, each with its Activity line.
/// </summary>
public class InstancesPageTests
{
    sealed class Driver : IRefreshDriver
    {
        public void Start(Action tick)
        {
        }

        public void Stop()
        {
        }
    }

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    internal static void Settle()
    {
        for (var i = 0; i < 6; i++) Pump();
    }

    internal sealed record Harness(InstancesPage Page, ShellViewModel Shell, ActivityLog Log, FakeInstances Source, FakeLauncher Launcher, Action<TimeSpan> Advance);

    internal static void OnPage(FakeInstances source, Action<Harness> test, bool windowsTerminal = true, Func<string, bool>? folderExists = null, TimeSpan? folderWait = null)
    {
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var launcher = new FakeLauncher();
            var page = new InstancesPage(source, scheduler, log, launcher, () => windowsTerminal, folderExists ?? (_ => true), folderWait)
            {
                Clock = new FixedClock(Now),
            };
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["instances"] = page }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "instances"));
            for (var i = 0; i < 4; i++) Pump();
            test(new Harness(page, shell, log, source, launcher, by =>
            {
                clock += by;
                scheduler.Tick();
                for (var i = 0; i < 4; i++) Pump();
            }));
            return 0;
        });
    }

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The shell on the Instances page over <paramref name="source"/>, for a render, and when it has read.</summary>
    internal static (System.Windows.FrameworkElement View, Func<System.Windows.FrameworkElement, Task> Ready) Rendered(FakeInstances source)
    {
        var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
        var log = Shells.Log();
        var page = new InstancesPage(source, scheduler, log, new FakeLauncher(), () => true, _ => true) { Clock = new FixedClock(Now) };
        var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["instances"] = page }), log);
        shell.Show(shell.Sections.Single(s => s.Key == "instances"));
        return (new ShellView { DataContext = shell }, async _ =>
        {
            // Held loading: the skeleton shows only after its delay, so a quick read never flashes it.
            if (source.Hold is not null) await Task.Delay(Bosun.Panel.Controls.Skeleton.DefaultDelay + TimeSpan.FromMilliseconds(150));
            for (var i = 0; i < 50 && source.Hold is null && page.State == PageState.Loading; i++) await Dispatcher.Yield(DispatcherPriority.Background);
            for (var i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.Background);
        });
    }

    static InstanceLine Line(InstancesPage page, long pid) => page.Lines.Single(l => l.Row.Instance.Pid == pid);

    [Fact]
    public void Each_instance_is_a_row_in_words_grouped_by_its_repo_with_a_job_s_slot()
    {
        OnPage(new FakeInstances(), h =>
        {
            var page = h.Page;
            Assert.Equal(PageState.Data, page.State);
            var claude = Line(page, 100);
            Assert.Equal(("Claude Code", "Windows", "Working", "2.0 GB", "2 h 12 min"), (claude.Instance, claude.Side, claude.State, claude.Memory, claude.Uptime));
            Assert.Equal("Claude Code says busy (session record)", claude.StateDetail);
            Assert.Equal("claude, pid 100, in D:\\Bosun", claude.InstanceDetail);
            Assert.Equal(("VS Code", "Unknown", "10.0 GB", "5 min"), (Line(page, 101).Instance, Line(page, 101).State, Line(page, 101).Memory, Line(page, 101).Uptime));
            var reviewing = Line(page, 300);
            Assert.Equal(("WSL (Ubuntu)", "Reviewing"), (reviewing.Side, reviewing.State));
            // The job: its WSL half joined to its Windows holder, so it shows its slot.
            var job = Line(page, 4242);
            Assert.Equal(("Build job: make test", "Working, slot 1"), (job.Instance, job.State));
            // Not available, with why, never 0.
            var codex = Line(page, 102);
            Assert.Equal((InstanceLine.NotAvailable, InstanceLine.NotAvailable), (codex.Memory, codex.Uptime));
            Assert.Equal(("Not available: its memory could not be read", "Not available: its start could not be read"), (codex.MemoryWhy, codex.UptimeWhy));
            Assert.Equal(("1 h", "1 d", "2 h 12 min", "45 s"), (InstanceLine.Duration(3600), InstanceLine.Duration(86400), InstanceLine.Duration(7920), InstanceLine.Duration(45)));

            // D:/Bosun holds the Windows agent and editor and the WSL agent under /mnt/d; /home/me/app is the distro's; codex is in no repo.
            var groups = page.LinesView.Groups.Cast<CollectionViewGroup>().Select(g => (g.Name, g.ItemCount)).ToList();
            // In the scanner's repo order (InstanceClient.Merge), then the instances outside any repo.
            Assert.Equal([("/home/me/app in WSL (Ubuntu)", 1), ("D:/Bosun", 3), (InstancesPage.NotInRepo, 1)], groups.Select(g => ((string)g.Name, g.ItemCount)));
        });
    }

    [Fact]
    public void A_job_waiting_for_guest_memory_says_so_and_has_no_uptime_yet()
    {
        var source = new FakeInstances
        {
            Answer = View(null, Win(I("job", 7, cwd: @"D:\Bosun", repo: "D:/Bosun", started: null, state: "idle", slot: 2, command: "cmake --build",
                evidence: "holds slot 2; its command has not started (waiting for guest memory)"))),
        };
        OnPage(source, h =>
        {
            var job = Line(h.Page, 7);
            Assert.Equal(("Idle, slot 2: waiting for guest memory", InstanceLine.NotAvailable, "Not available: its command has not started"), (job.State, job.Uptime, job.UptimeWhy));
        });
    }

    [Fact]
    public void Two_distros_with_one_path_are_two_groups_and_a_Windows_repo_with_its_mnt_instance_is_one()
    {
        var source = new FakeInstances
        {
            Answer = View(null,
                Win(I("claude", 1, cwd: @"D:\app", repo: "D:/app")),
                Wsl("Ubuntu", I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"), I("codex", 3, side: "wsl", cwd: "/mnt/d/app", repo: "/mnt/d/app")),
                Wsl("Debian", I("claude", 2, side: "wsl", cwd: "/home/me/app", repo: "/home/me/app"))),
        };
        OnPage(source, h =>
        {
            var groups = h.Page.LinesView.Groups.Cast<CollectionViewGroup>().ToDictionary(g => (string)g.Name, g => g.ItemCount);
            Assert.Equal(2, groups["D:/app"]);                                                  // Windows and its /mnt/d folder: one repo
            Assert.Equal(1, groups["/home/me/app in WSL (Ubuntu)"]);
            Assert.Equal(1, groups["/home/me/app in WSL (Debian)"]);
            Assert.Equal(2, h.Page.Lines.Count(l => l.Row.Instance.Pid == 2));                 // same pid, two distros: two rows
        });
    }

    [Fact]
    public void It_refreshes_every_30_s_while_shown_and_reads_nothing_while_hidden()
    {
        var source = new FakeInstances();
        OnPage(source, h =>
        {
            Assert.Equal(1, source.Reads);
            source.Answer = View(null, Win(I("claude", 100, cwd: @"D:\Bosun", repo: "D:/Bosun")));
            h.Advance(TimeSpan.FromSeconds(31));
            Assert.Equal(2, source.Reads);
            Assert.Single(h.Page.Lines);                                                       // the rows changed with it
            h.Shell.Show(h.Shell.Sections.Single(s => s.Key == "overview"));
            h.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(2, source.Reads);                                                     // hidden: nothing read
        });
    }

    [Fact]
    public void It_shows_loading_then_a_failure_with_Retry_that_recovers()
    {
        var source = new FakeInstances
        {
            Hold = new TaskCompletionSource(),
            Answer = View(null, Failed("windows", null, "bosun.exe was not found."), Failed("wsl", "Ubuntu", "instances-wsl-Ubuntu.json is not an instances report: bad.")),
        };
        OnPage(source, h =>
        {
            Assert.Equal(PageState.Loading, h.Page.State);
            Assert.Equal(Bosun.Panel.Controls.SkeletonShape.Table, h.Page.LoadingShape);
            var held = source.Hold!;
            source.Hold = null;
            held.SetResult();
            Settle();
            Assert.Equal(PageState.Failed, h.Page.State);
            Assert.Equal($"Windows: bosun.exe was not found.{Environment.NewLine}WSL (Ubuntu): instances-wsl-Ubuntu.json is not an instances report: bad.", h.Page.FailureMessage);
            source.Answer = Both();
            h.Page.Retry!.Execute(null);
            Settle();
            Assert.Equal(PageState.Data, h.Page.State);
        });
    }

    [Fact]
    public void Nothing_running_is_said_only_when_every_side_was_read_whole()
    {
        var source = new FakeInstances { Answer = View(null, Win(), Wsl("Ubuntu")) };
        OnPage(source, h =>
        {
            Assert.Equal((PageState.Empty, InstancesPage.NothingRunning), (h.Page.State, h.Page.EmptyReason));
            source.Answer = View(null, new SideReading("windows", null, Bosun.Core.Memory.Reading<InstancesDoc>.Of(
                Doc("windows", null, [], [new SourceProblemDoc { Source = "processes", Why = "access denied" }], unreadable: 3), Now)));
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal(PageState.Empty, h.Page.State);
            Assert.StartsWith("Nothing is listed, but the scan was incomplete: ", h.Page.EmptyReason, StringComparison.Ordinal);
            Assert.Contains("Windows: its process list could not be read: access denied.", h.Page.EmptyReason, StringComparison.Ordinal);
            Assert.Contains("Windows: 3 processes could not be read, so an instance may be missing.", h.Page.EmptyReason, StringComparison.Ordinal);

            // No distro has reported: Windows empty is not nothing running on either side (the completion review).
            source.Answer = View("No WSL distro has reported its instances to X:\\logs, so only Windows is listed.", Win());
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal(PageState.Empty, h.Page.State);
            Assert.Equal("Nothing is listed, but the scan was incomplete: No WSL distro has reported its instances to X:\\logs, so only Windows is listed.", h.Page.EmptyReason);
            // And with Windows unread too, the failure says both.
            source.Answer = View("The WSL reports in X:\\logs can't be listed (access denied), so only Windows is listed.", Failed("windows", null, "bosun.exe was not found."));
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal(PageState.Failed, h.Page.State);
            Assert.Equal($"Windows: bosun.exe was not found.{Environment.NewLine}The WSL reports in X:\\logs can't be listed (access denied), so only Windows is listed.", h.Page.FailureMessage);
        });
    }

    [Fact]
    public void A_source_that_could_not_be_read_is_said_by_name_beside_the_rows_that_were()
    {
        var source = new FakeInstances
        {
            Answer = View(null, Win(I("claude", 1, cwd: @"D:\a", repo: "D:/a")), Failed("wsl", "Ubuntu", "instances-wsl-Ubuntu.json is not an instances report: bad.")),
        };
        OnPage(source, h =>
        {
            Assert.Equal(PageState.Data, h.Page.State);
            Assert.Single(h.Page.Lines);
            Assert.Equal(["WSL (Ubuntu): instances-wsl-Ubuntu.json is not an instances report: bad."], h.Page.Notices);
            // Windows unread, the distro's rows kept and Windows said.
            source.Answer = View(null, Failed("windows", null, "bosun instances --json ran past 20 s."), Wsl("Ubuntu", I("claude", 2, side: "wsl", cwd: "/home/me/a", repo: "/home/me/a")));
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal(PageState.Data, h.Page.State);
            Assert.Equal(2, Assert.Single(h.Page.Lines).Row.Instance.Pid);
            Assert.Equal(["Windows: bosun instances --json ran past 20 s."], h.Page.Notices);
            // No distro has reported: the client's notice, above the Windows rows.
            source.Answer = View("No WSL distro has reported its instances, so only Windows is listed.", Win(I("claude", 1, cwd: @"D:\a", repo: "D:/a")));
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal("No WSL distro has reported its instances, so only Windows is listed.", h.Page.WslNotice);
        });
    }

    [Fact]
    public void Each_stale_distro_keeps_its_rows_marked_old_with_its_own_age_and_one_that_reports_again_is_current()
    {
        var source = new FakeInstances
        {
            Answer = View(null, Win(I("claude", 1, cwd: @"D:\a", repo: "D:/a")),
                StaleWsl("Ubuntu", TimeSpan.FromMinutes(4), I("claude", 2, side: "wsl", cwd: "/home/me/a", repo: "/home/me/a")),
                StaleWsl("Debian", TimeSpan.FromMinutes(20), I("codex", 3, side: "wsl", cwd: "/home/me/b", repo: "/home/me/b"))),
        };
        OnPage(source, h =>
        {
            var page = h.Page;
            Assert.Equal(PageState.Data, page.State);                                         // Windows is current: the page is not old
            Assert.Null(page.StaleNote);
            Assert.Equal(("Last seen 4 minutes ago", true), (Line(page, 2).State, Line(page, 2).IsStale));
            Assert.Equal("Last seen 20 minutes ago", Line(page, 3).State);
            Assert.Contains("WSL (Ubuntu) last reported 4 minutes ago: it is stopped, or its logger is.", page.Notices);
            Assert.Contains("WSL (Debian) last reported 20 minutes ago: it is stopped, or its logger is.", page.Notices);
            Assert.False(Line(page, 1).IsStale);

            source.Answer = View(null, Win(I("claude", 1, cwd: @"D:\a", repo: "D:/a")),
                Wsl("Ubuntu", I("claude", 2, side: "wsl", cwd: "/home/me/a", repo: "/home/me/a")),
                StaleWsl("Debian", TimeSpan.FromMinutes(21), I("codex", 3, side: "wsl", cwd: "/home/me/b", repo: "/home/me/b")));
            page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal(("Working", false), (Line(page, 2).State, Line(page, 2).IsStale));  // Ubuntu is back
            Assert.Equal("Last seen 21 minutes ago", Line(page, 3).State);                        // Debian still old
            Assert.DoesNotContain(page.Notices, n => n.StartsWith("WSL (Ubuntu)", StringComparison.Ordinal));

            // Every side read is old: the page as a whole is what was last seen, from the oldest report.
            source.Answer = View(null, Failed("windows", null, "bosun.exe was not found."),
                StaleWsl("Debian", TimeSpan.FromMinutes(22), I("codex", 3, side: "wsl", cwd: "/home/me/b", repo: "/home/me/b")));
            page.RefreshNow!.Execute(null);
            Settle();
            Assert.StartsWith("WSL (Debian) last reported 22 minutes ago: it is stopped, or its logger is. Showing what was read 22 minutes ago", page.StaleNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void GB_and_Uptime_sort_by_their_numbers_with_unavailable_last_both_ways()
    {
        var source = new FakeInstances
        {
            Answer = View(null, Win(
                I("claude", 1, cwd: @"D:\a", repo: "D:/a", mb: 2048, started: "2026-11-16T08:55:00Z"),
                I("codex", 2, cwd: @"D:\a", repo: "D:/a", mb: 10240, started: "2026-11-16T07:00:00Z"),
                I("code", 3, cwd: @"D:\a", repo: "D:/a", mb: null, started: null))),
        };
        OnPage(source, h =>
        {
            long[] Order() => h.Page.LinesView.Cast<InstanceLine>().Select(l => l.Row.Instance.Pid).ToArray();
            h.Page.SortBy(nameof(InstanceLine.Memory), ListSortDirection.Ascending);
            Assert.Equal([1L, 2, 3], Order());                                                 // 2 GB, 10 GB, then not available
            h.Page.SortBy(nameof(InstanceLine.Memory), ListSortDirection.Descending);
            Assert.Equal([2L, 1, 3], Order());
            h.Page.SortBy(nameof(InstanceLine.Uptime), ListSortDirection.Ascending);
            Assert.Equal([1L, 2, 3], Order());                                                 // 5 min, 2 h, then not available
            h.Page.SortBy(nameof(InstanceLine.Uptime), ListSortDirection.Descending);
            Assert.Equal([2L, 1, 3], Order());
        });
    }

    [Fact]
    public void Each_action_opens_through_the_launcher_and_writes_its_Activity_line_and_a_failure_its_toast()
    {
        OnPage(new FakeInstances(), h =>
        {
            var claude = Line(h.Page, 100);
            claude.OpenFolder!.Execute(null);
            // Its line is written once the launch has returned: waited for itself, not for the launch (CI's red run).
            Until(() => h.Log.Entries.Any(e => e.Severity == LogSeverity.Info && e.Message.StartsWith("Open folder of Claude Code", StringComparison.Ordinal)));
            Assert.Equal(("explorer.exe", @"D:\Bosun"), (h.Launcher.Started[^1].FileName, h.Launcher.Started[^1].ArgumentList.Single()));
            Assert.Contains(h.Log.Entries, e => e.Severity == LogSeverity.Info && e.Message.StartsWith("Open folder of Claude Code", StringComparison.Ordinal));
            Line(h.Page, 4242).OpenTerminal!.Execute(null);
            Until(() => h.Launcher.Started.Count == 2);
            Assert.Equal(["-d", ".", "wsl.exe", "-d", "Ubuntu"], h.Launcher.Started[^1].ArgumentList);

            h.Launcher.Throw = new InvalidOperationException("wt.exe could not be started: access denied");
            claude.OpenTerminal!.Execute(null);
            Until(() => h.Log.Entries.Any(e => e.Severity == LogSeverity.Fail));
            Assert.Contains(h.Log.Entries, e => e.Severity == LogSeverity.Fail && e.Message.EndsWith("wt.exe could not be started: access denied", StringComparison.Ordinal));
            // And its toast, beside the line (round 3): a failure shown, with its reason.
            var toast = Assert.Single(h.Shell.Toasts.Shown, t => t.IsFail);
            Assert.EndsWith("wt.exe could not be started: access denied", toast.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Without_Windows_Terminal_the_terminal_is_a_console_of_its_own_and_a_folder_gone_is_refused()
    {
        OnPage(new FakeInstances(), h =>
        {
            Line(h.Page, 100).OpenTerminal!.Execute(null);
            Until(() => h.Launcher.Started.Count == 1);
            Assert.Equal(("cmd.exe", @"D:\Bosun"), (h.Launcher.Started[^1].FileName, h.Launcher.Started[^1].WorkingDirectory));
        }, windowsTerminal: false);

        OnPage(new FakeInstances(), h =>
        {
            Line(h.Page, 100).OpenFolder!.Execute(null);
            Until(() => h.Log.Entries.Any(e => e.Severity == LogSeverity.Fail));
            Assert.Empty(h.Launcher.Started);
            Assert.Contains(h.Log.Entries, e => e.Severity == LogSeverity.Fail && e.Message.EndsWith(@"The folder is no longer there: D:\Bosun", StringComparison.Ordinal));
            // Its actions are now off, with why, until a refresh reads the instance again (the completion review).
            var gone = Line(h.Page, 100);
            Assert.False(gone.OpenFolder!.CanExecute(null));
            Assert.False(gone.OpenTerminal!.CanExecute(null));
            Assert.Equal(@"The folder is no longer there: D:\Bosun", gone.OpenFolderTip);
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.True(Line(h.Page, 100).OpenFolder!.CanExecute(null));
            Line(h.Page, 4242).OpenFolder!.Execute(null);                                      // a distro's own folder is not looked at first
            Until(() => h.Launcher.Started.Count == 1);
            Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\app", h.Launcher.Started.Single().ArgumentList.Single());
        }, folderExists: _ => false);
    }

    /// <summary>Pumps until <paramref name="done"/>, the actions' work running off the UI thread.</summary>
    static void Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            Settle();
            Thread.Sleep(5);
        }
        Assert.True(done(), "the action did not finish");
    }

    [Fact]
    public void A_folder_that_does_not_answer_holds_neither_the_panel_nor_the_action_past_its_wait()
    {
        using var never = new ManualResetEventSlim();
        OnPage(new FakeInstances(), h =>
        {
            Line(h.Page, 100).OpenFolder!.Execute(null);                                        // returns at once: the check is off the UI thread
            Until(() => h.Log.Entries.Any(e => e.Severity == LogSeverity.Fail));
            Assert.Contains(h.Log.Entries, e => e.Message.EndsWith(@"The folder could not be reached within 0 s: D:\Bosun", StringComparison.Ordinal));
            Assert.Empty(h.Launcher.Started);
        }, folderExists: _ => never.Wait(TimeSpan.FromSeconds(30)), folderWait: TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public void A_row_with_no_folder_has_its_actions_disabled_with_why()
    {
        var source = new FakeInstances { Answer = View(null, Win(I("claude", 1, cwd: null, repo: null))) };
        OnPage(source, h =>
        {
            var line = Line(h.Page, 1);
            Assert.False(line.OpenFolder!.CanExecute(null));
            Assert.Equal(OpenCommands.NoFolder, line.OpenFolderTip);
            Assert.Equal(OpenCommands.NoFolder, line.OpenTerminalTip);
        });
    }

    [Fact]
    public void An_editor_s_window_rows_each_keep_their_selection_through_a_reordered_refresh_and_open_their_own_folder()
    {
        // D03 T02 §4: one editor with windows on two same-named repos and a WSL window, a row each; its memory on its first.
        Bosun.Core.Instances.InstancesView Scan(bool reversed)
        {
            InstanceDoc[] rows =
            [
                EditorWindow(500, "s1#0", @"D:\one\app", "D:/one/app", 4096),
                EditorWindow(500, "s1#1", @"E:\two\app", "E:/two/app", null),
                EditorWindow(500, "s2#0", "/home/me/app", "/home/me/app", null, location: "wsl", distro: "Ubuntu"),
            ];
            return View(null, Win(reversed ? [.. rows.Reverse()] : rows));
        }
        var source = new FakeInstances { Answer = Scan(reversed: false) };
        OnPage(source, h =>
        {
            var page = h.Page;
            var editor = page.Lines.Where(l => l.Row.Instance.Pid == 500).ToList();
            Assert.Equal(3, editor.Count);
            var second = editor.Single(l => l.Row.Instance.Window == "s1#1");
            Assert.Equal("E:/two/app", second.Group);
            Assert.Equal("/home/me/app in WSL (Ubuntu)", editor.Single(l => l.Row.Instance.Window == "s2#0").Group);
            Assert.Equal(InstanceLine.Shared, second.Memory);
            Assert.Equal("Shared with the editor's other windows: 4096 MB in all, shown on its first window", second.MemoryWhy);
            var first = editor.Single(l => l.Row.Instance.Window == "s1#0");
            Assert.NotEqual(InstanceLine.NotAvailable, first.Memory);
            Assert.Equal("4096 MB private, shared by all of the editor's open windows", first.MemoryWhy);

            page.Selected = second;
            source.Answer = Scan(reversed: true);
            page.RefreshNow!.Execute(null);
            Settle();
            Assert.Equal("s1#1", page.Selected!.Row.Instance.Window);
            page.Selected.OpenFolder!.Execute(null);
            Until(() => h.Launcher.Started.Count == 1);
            Assert.Equal(@"E:\two\app", h.Launcher.Started[^1].ArgumentList.Single());
            h.Shell.Palette.Open();
            h.Shell.Palette.Query = "Open terminal";
            _ = h.Shell.Palette.Rows.Single(r => r.Name.StartsWith("Open terminal", StringComparison.Ordinal)).Run();
            Until(() => h.Launcher.Started.Count == 2);
            Assert.Equal(@"E:\two\app", h.Launcher.Started[^1].WorkingDirectory);

            // The WSL window opens in its distro, through the distro's own path.
            page.Selected = page.Lines.Single(l => l.Row.Instance.Window == "s2#0");
            page.Selected.OpenTerminal!.Execute(null);
            Until(() => h.Launcher.Started.Count == 3);
            Assert.Equal(["-d", ".", "wsl.exe", "-d", "Ubuntu"], h.Launcher.Started[^1].ArgumentList);
            Assert.Equal(@"\\wsl.localhost\Ubuntu\home\me\app", h.Launcher.Started[^1].WorkingDirectory);
        });
    }

    [Fact]
    public void Each_editor_with_windows_not_placed_is_a_notice_of_its_own_and_goes_once_they_are()
    {
        // D03 T02 §5: a stale record, then two editors' missing windows: three notices, each editor's count kept.
        SourceProblemDoc P(string source, string why) => new() { Source = source, Why = why };
        Bosun.Core.Instances.InstancesView Scan(params SourceProblemDoc[] problems) => View(null,
            new SideReading("windows", null, Bosun.Core.Memory.Reading<InstancesDoc>.Of(Doc("windows", null, [EditorWindow(500, "s1#a", @"D:\one\app", "D:/one/app", 4096)], problems), Now)));
        var source = new FakeInstances
        {
            Answer = Scan(
                P("editor-windows", @"C:\x\stale.json is stale: its window's extension host 9 is gone"),
                P("editor-coverage", "Code.exe 500 has 2 of its 3 windows not placed: run pwsh scripts/install-editor-extension.ps1"),
                P("editor-coverage", "Code.exe 600 has 1 of its 2 windows not placed: run pwsh scripts/install-editor-extension.ps1")),
        };
        OnPage(source, h =>
        {
            var notices = InstancesPage.NoticesOf(source.Answer);
            Assert.Equal(3, notices.Count);
            Assert.Contains("Windows: Code.exe 500 has 2 of its 3 windows not placed: run pwsh scripts/install-editor-extension.ps1", notices);
            Assert.Contains("Windows: Code.exe 600 has 1 of its 2 windows not placed: run pwsh scripts/install-editor-extension.ps1", notices);
            Assert.Contains(notices, n => n.Contains("stale.json is stale", StringComparison.Ordinal));
            Assert.Contains(h.Page.Notices, n => n.Contains("Code.exe 600 has 1 of its 2", StringComparison.Ordinal));

            source.Answer = Scan();
            h.Page.RefreshNow!.Execute(null);
            Settle();
            Assert.DoesNotContain(h.Page.Notices, n => n.Contains("windows not placed", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_distro_whose_logger_is_older_than_drive_mounts_says_so_and_one_that_could_not_read_them_says_why()
    {
        // D03 T02 §6: a report with no drives and no word why is from an older logger; one that could not read its mount
        // table says so itself, as an automount problem, and is not told to update.
        var older = new SideReading("wsl", "Ubuntu", Bosun.Core.Memory.Reading<InstancesDoc>.Of(Doc("wsl", "Ubuntu", [I("claude", 300, side: "wsl", cwd: "/mnt/d/Bosun")], olderLogger: true), Now));
        Assert.Contains("WSL (Ubuntu): its logger is older than drive mounts, so its folders on Windows drives are shown as the distro's own: in the Bosun checkout, run pwsh scripts/install.ps1",
            InstancesPage.NoticesOf(View(null, Win(), older)));
        var unread = new SideReading("wsl", "Ubuntu", Bosun.Core.Memory.Reading<InstancesDoc>.Of(Doc("wsl", "Ubuntu", [I("claude", 300, side: "wsl")],
            [new SourceProblemDoc { Source = "automount", Why = "/proc/self/mountinfo can't be read (Permission denied)" }]), Now));
        var notices = InstancesPage.NoticesOf(View(null, Win(), unread));
        Assert.Contains("WSL (Ubuntu): its drive mounts (/proc/self/mountinfo) could not be read: /proc/self/mountinfo can't be read (Permission denied)", notices);
        Assert.DoesNotContain(notices, n => n.Contains("older than drive mounts", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_window_and_a_window_whose_folder_can_t_be_placed_are_told_apart()
    {
        // Round 2: an SSH window's row (no folder Bosun can place) read "an empty window".
        var source = new FakeInstances { Answer = View(null, Win(EditorWindow(600, "s1#empty", null, null, 4096), EditorWindow(600, "s2#0123456789abcdef", null, null, null))) };
        OnPage(source, h =>
        {
            Assert.Equal("code, pid 600, an empty window", h.Page.Lines.Single(l => l.Row.Instance.Window == "s1#empty").InstanceDetail);
            Assert.Equal("code, pid 600, a window whose folder Bosun can't place (a remote that is not WSL)",
                h.Page.Lines.Single(l => l.Row.Instance.Window == "s2#0123456789abcdef").InstanceDetail);
        });
    }

    [Fact]
    public void Ctrl_K_acts_on_the_selected_row_and_offers_nothing_without_one_and_a_refresh_keeps_or_clears_the_selection()
    {
        var source = new FakeInstances();
        OnPage(source, h =>
        {
            var page = h.Page;
            Assert.DoesNotContain(page.PaletteCommands, c => c.Name.StartsWith("Open", StringComparison.Ordinal));
            page.Selected = Line(page, 100);
            h.Shell.Palette.Open();
            h.Shell.Palette.Query = "Open folder";
            var row = h.Shell.Palette.Rows.Single(r => r.Name == "Open folder of Claude Code");
            _ = row.Run();
            Until(() => h.Launcher.Started.Count == 1);
            Assert.Equal(@"D:\Bosun", h.Launcher.Started.Single().ArgumentList.Single());

            page.RefreshNow!.Execute(null);                                                     // the same instance: still selected
            Settle();
            Assert.Equal(100, page.Selected!.Row.Instance.Pid);
            source.Answer = View(null, Win(I("code", 101, cwd: @"D:\Bosun", repo: "D:/Bosun")));
            page.RefreshNow!.Execute(null);                                                     // gone: nothing selected, not another row
            Settle();
            Assert.Null(page.Selected);
        });
    }

    [Fact]
    public void A_long_table_scrolls_with_the_page_so_its_last_row_and_actions_are_in_reach_at_the_least_window()
    {
        // Sixty instances in thirty repos: far taller than a 960 x 600 window (the completion review).
        var many = Enumerable.Range(0, 60).Select(i => I("claude", 1000 + i, cwd: $@"D:\r{i / 2}", repo: $"D:/r{i / 2}")).ToArray();
        PageRenderer.InPage("Dark", () =>
        {
            var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
            var log = Shells.Log();
            var page = new InstancesPage(new FakeInstances { Answer = View(null, Win(many)) }, scheduler, log, new FakeLauncher(), () => true, _ => true)
            {
                Clock = new FixedClock(Now),
            };
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["instances"] = page }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "instances"));
            return new ShellView { DataContext = shell };
        }, root =>
        {
            for (var i = 0; i < 6; i++) Pump();
            PageRenderer.Settle(root);
            // The table scrolls inside itself, in the height the frame gives it.
            var grid = ShellSupport.Under<DataGrid>(root).Single(g => g.Name == "Table");
            var scroll = ShellSupport.Under<ScrollViewer>(grid).First();
            Assert.True(scroll.ScrollableHeight > 0, $"the table scrolls (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight})");
            var page = ShellSupport.Under<ScrollViewer>(root).Single(s => s.Name == "PageScroll");
            Assert.True(grid.TransformToAncestor(page).Transform(new System.Windows.Point(0, grid.ActualHeight)).Y <= page.ActualHeight + 1, "the table ends inside the window");
            scroll.ScrollToBottom();
            PageRenderer.Settle(root);
            var last = ShellSupport.Under<Button>(root).Where(b => b.Name == "OpenTerminal").Last();
            // Measured in the scroll's own viewport (its content presenter), inside the ScrollViewer's padding.
            var presenter = ShellSupport.Under<ScrollContentPresenter>(scroll).First();
            var bottom = last.TransformToAncestor(presenter).Transform(new System.Windows.Point(0, last.ActualHeight)).Y;
            Assert.True(bottom <= scroll.ViewportHeight + 1, $"the last row's action is in view at the bottom ({bottom} of {scroll.ViewportHeight})");
            Assert.True(last.ActualHeight >= 32 && last.ActualWidth >= 32, $"a 32 px target ({last.ActualWidth} x {last.ActualHeight})");
            return 0;
        }, 960, 600);
    }

    [Fact]
    public void The_drawn_page_has_its_grid_columns_groups_and_named_actions()
    {
        PageRenderer.InPage("Dark", () =>
        {
            var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
            var log = Shells.Log();
            var page = new InstancesPage(new FakeInstances(), scheduler, log, new FakeLauncher(), () => true, _ => true) { Clock = new FixedClock(Now) };
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["instances"] = page }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "instances"));
            return new ShellView { DataContext = shell };
        }, page =>
        {
            for (var i = 0; i < 6; i++) Pump();
            PageRenderer.Settle(page);
            var view = ShellSupport.Under<Bosun.Panel.Instances.InstancesView>(page).Single();
            var grid = ShellSupport.Under<DataGrid>(view).Single(g => g.Name == "Table");
            Assert.Equal("Instances by repo", System.Windows.Automation.AutomationProperties.GetName(grid));
            Assert.Equal(["Instance", "Side", "State", "GB", "Uptime", "Open"], grid.Columns.Select(c => (string)c.Header));
            var texts = PageRenderer.TextsOf(view);
            Assert.Contains("D:/Bosun", texts);
            Assert.Contains("/home/me/app in WSL (Ubuntu)", texts);
            var buttons = ShellSupport.Under<Button>(view).Where(b => b.Name == "OpenFolder").ToList();
            Assert.Contains(buttons, b => System.Windows.Automation.AutomationProperties.GetName(b) == "Open folder of Claude Code (claude, pid 100, in D:\\Bosun)");
            return 0;
        }, 1360, 860);
    }
}
