using System.ComponentModel;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Memory;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Memory;

/// <summary>
/// The Memory page (D03 T01 §2, design/SPEC.md §3): its cards and its table from the snapshots and the last 24 hours'
/// report, each value checked against the field it comes from, nothing unread ever shown as 0.
/// </summary>
public class MemoryPageTests
{
    internal static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A source the test sets: each reading as given, a report that may be held, and how often each was asked.</summary>
    internal sealed class Source : IMemorySource
    {
        public Reading<SnapshotDoc> Windows = Reading<SnapshotDoc>.Of(Snapshot(), Now);
        public Reading<SnapshotDoc> Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(), Now);
        public Reading<ReportDoc> Report = Reading<ReportDoc>.Of(ReportOf(), Now);
        public Reading<ReportDoc> Fortnight = Reading<ReportDoc>.Of(ReportOf(), Now);
        public TaskCompletionSource? HoldReport, HoldSnapshots, HoldFortnight;
        public int Snapshots, Reports, Fortnights;

        public async Task<Reading<SnapshotDoc>> SnapshotAsync(string side, CancellationToken cancellationToken = default)
        {
            if (side == "windows") Snapshots++;
            if (HoldSnapshots is { } hold) await hold.Task.WaitAsync(cancellationToken);
            return side == "windows" ? Windows : Wsl;
        }

        public long? LastFloor;

        public async Task<Reading<ReportDoc>> ReportAsync(int days, long? lowMb = null, CancellationToken cancellationToken = default)
        {
            if (days == 14) LastFloor = lowMb;
            if (days == 14)                                                                // the fortnight, for 7 and 14 days
            {
                Fortnights++;
                var answer = Fortnight;                                                    // what this request reads, fixed when it is asked
                if (HoldFortnight is { } held) await held.Task.WaitAsync(cancellationToken);
                return answer;
            }
            Assert.Equal(1, days);                                                         // the last 24 hours
            Reports++;
            if (HoldReport is { } hold) await hold.Task.WaitAsync(cancellationToken);
            return Report;
        }
    }

    internal sealed class Driver : IRefreshDriver
    {
        public void Start(Action tick)
        {
        }

        public void Stop()
        {
        }
    }

    internal static string Repos(params (string Repo, long Mb, long Processes)[] repos) =>
        string.Join(",", repos.Select(r => $$"""{"repo":"{{r.Repo}}","processes":{{r.Processes}},"private_mb":{{r.Mb}},"rss_mb":{{r.Mb}}}"""));

    internal static SnapshotDoc Snapshot(string repos = "", string memory = """{"available_mb":8192,"total_mb":32768,"commit_mb":38000,"commit_limit_mb":65536,"pages_in_per_sec":3}""",
        string vm = """{"running":true,"processes":1,"private_mb":12083,"rss_mb":9000}""", bool written = true) =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"windows","at":"2026-11-16T09:00:00Z","interval_s":5,"memory":{{memory}},"wsl_vm":{{vm}},"repos":[{{(repos.Length > 0 ? repos : Repos(("D:/Recto", 6348, 14), ("D:/src/Bosun", 922, 5)))}}],"processes":[],"history_written":{{(written ? "true" : "false")}}}""",
            MemoryJson.Default.SnapshotDoc)!;

    internal static SnapshotDoc WslSnapshot(string? repos = null) =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"wsl","at":"2026-11-16T09:00:00Z","interval_s":5,"memory":{"available_mb":6000,"total_mb":14336},"wsl_vm":null,"repos":[{{repos ?? Repos(("/home/me/Resolute", 4505, 22))}}],"processes":[],"history_written":true}""",
            MemoryJson.Default.SnapshotDoc)!;

    /// <summary>The decision's evidence for a test report: 14 days at 32 GB, nothing pressing, by default.</summary>
    internal const string Evidence = ""","low_minutes":0,"days_with_samples":14,"peak_commit_ratio":0.6,"total_mb":32768,"capacity_since":null""";

    internal static ReportDoc ReportOf(bool complete = true, string? repos = null, long overlap = 3, string series = "", string evidence = Evidence, long lowMb = 2048) =>
        JsonSerializer.Deserialize($$$"""{"schema_version":1,"generated_at":"2026-11-16T09:00:00Z","since":"2026-11-15T09:00:00Z","days_requested":1,"days_covered":1,"first_sample":"2026-11-15T09:00:05","last_sample":"2026-11-16T08:59:55","low_mb":{{{lowMb}}},"read":{"complete":{{{(complete ? "true" : "false")}}},"files":2,"unreadable_files":{{{(complete ? 0 : 1)}}},"rejected_rows":{{{(complete ? 0 : 7)}}}},"windows":{"samples":17000,"low_samples":3,"min_available_mb":1536,"worst_minute":"2026-11-16T07:20","worst_repos":[],"max_commit_mb":41000,"commit_limit_mb":65536,"max_pages_in_per_sec":40{{{evidence}}}},"wsl":{"samples":17000,"min_available_mb":2000,"max_swap_mb":512,"max_psi_full_avg10_x100":0,"max_oom_kills":0,"oom_kills_in_period":0,"oom_baseline_known":true},"builds":{"jobs":9,"max_overlap":{{{overlap}}},"at":"2026-11-16T07:20:00Z"},"active_repos":{"max":4,"minute":"2026-11-16T07:20","total_private_mb":20000},"series":[{{{series}}}],"repos":[{{{repos ?? """{"repo":"D:/Recto","side":"win","peak_private_mb":11674,"peak_rss_mb":9000,"peak_processes":30},{"repo":"/home/me/Resolute","side":"wsl","peak_private_mb":9011,"peak_rss_mb":8000,"peak_processes":40},{"repo":"D:/Isotone","side":"win","peak_private_mb":7168,"peak_rss_mb":6000,"peak_processes":12}"""}}}]}""",
            MemoryJson.Default.ReportDoc)!;

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A shell whose Memory page reads <paramref name="source"/>, shown, and a clock the test moves.</summary>
    static void OnPage(Source source, Action<MemoryPage, ShellViewModel, RefreshScheduler, Action<TimeSpan>> test, Bosun.Core.PanelSettingsStore? settings = null)
    {
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var page = new MemoryPage(source, scheduler, log, settings);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["memory"] = page }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "memory"));
            for (var i = 0; i < 4; i++) Pump();
            test(page, shell, scheduler, by =>
            {
                clock += by;
                scheduler.Tick();
                for (var i = 0; i < 4; i++) Pump();
            });
            return 0;
        });
    }

    [Fact]
    public void Each_card_is_the_field_it_is_read_from()
    {
        OnPage(new Source(), (page, _, _, _) =>
        {
            Assert.Equal(PageState.Data, page.State);
            Assert.Equal(new Metric("24.0 GB"), page.WindowsUsed);                                       // total_mb 32768 less available_mb 8192
            Assert.Equal("8.0 GB available of 32.0 GB; committed 37.1 GB", page.WindowsNote); // the room left; commit_mb 38000
            Assert.Equal(new Metric("11.8 GB"), page.WslVm);                                             // wsl_vm.private_mb 12083
            Assert.Equal("Cap 14.0 GB", page.WslNote);                                       // the WSL snapshot's total_mb 14336
            Assert.Equal(new Metric("3 at once"), page.Overlap);                                         // builds.max_overlap
            Assert.Equal("Most in the last 24 hours; lowest headroom 1.5 GB", page.OverlapNote); // windows.min_available_mb 1536
            Assert.Equal(SkeletonShape.Gauge, page.LoadingShape);
        });
    }

    [Fact]
    public void Rows_join_on_side_and_repo_with_their_own_peaks()
    {
        var source = new Source { Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(Repos(("/home/me/Resolute", 4505, 22), ("/home/me/Bosun", 300, 3))), Now) };
        OnPage(source, (page, _, _, _) =>
        {
            var rows = page.RowsView.Cast<RepoRow>().ToList();
            Assert.Equal(
            [
                ("Recto", "Windows", "6.2", "11.4", 14L), ("Resolute", "WSL", "4.4", "8.8", 22L), ("Bosun", "Windows", "0.9", MemoryPage.NotInHistory, 5L),
                ("Bosun", "WSL", "0.3", MemoryPage.NotInHistory, 3L), ("Isotone", "Windows", "0.0", "7.0", 0L),
            ], rows.Select(r => (r.Repo, r.Side, r.Private, r.Peak, r.Processes)));            // highest private first; "win" in the report is Windows
            Assert.Equal("", page.TableNote);
        });
    }

    [Theory]
    [InlineData(nameof(RepoRow.PrivateGb))]
    [InlineData(nameof(RepoRow.PeakGb))]
    [InlineData(nameof(RepoRow.Processes))]
    public void Each_number_column_sorts_numerically_both_ways_and_keeps_its_sort_over_a_refresh(string column)
    {
        var source = new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(Repos(("D:/A", 9 * 1024, 9), ("D:/B", 10 * 1024, 10), ("D:/C", 2 * 1024, 2))), Now),
            Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(" "), Now),
            Report = Reading<ReportDoc>.Of(ReportOf(repos: """{"repo":"D:/A","side":"win","peak_private_mb":9216,"peak_rss_mb":1,"peak_processes":1},{"repo":"D:/B","side":"win","peak_private_mb":10240,"peak_rss_mb":1,"peak_processes":1},{"repo":"D:/C","side":"win","peak_private_mb":2048,"peak_rss_mb":1,"peak_processes":1}"""), Now),
        };
        OnPage(source, (page, _, _, tick) =>
        {
            string Order() => string.Concat(page.RowsView.Cast<RepoRow>().Select(r => r.Repo));
            foreach (var (direction, expected) in new[] { (ListSortDirection.Ascending, "CAB"), (ListSortDirection.Descending, "BAC") })
            {
                page.RowsView.SortDescriptions.Clear();
                page.RowsView.SortDescriptions.Add(new SortDescription(column, direction));
                Assert.Equal(expected, Order());                                             // 10 after 9, not before
                tick(RefreshScheduler.Slow + TimeSpan.FromSeconds(1));                        // read again: the sort stays
                Assert.Equal(expected, Order());
                Assert.Equal(direction, page.RowsView.SortDescriptions.Single().Direction);
            }
        });
    }

    [Fact]
    public void An_unreadable_counter_or_source_is_Not_available_with_why_never_0_and_recovers()
    {
        var source = new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(memory: """{"available_mb":null,"total_mb":32768,"commit_mb":null}"""), Now),
            Wsl = Reading<SnapshotDoc>.Fail(ReadingProblem.Failed, "The WSL logger's file could not be read."),
            Report = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s."),
        };
        OnPage(source, (page, _, _, tick) =>
        {
            Assert.Equal(new NotAvailable("The Windows logger did not measure total and available memory."), page.WindowsUsed);
            Assert.Equal("Available: not measured; committed: not measured", page.WindowsNote);
            Assert.StartsWith("Cap: not available.", page.WslNote, StringComparison.Ordinal);
            Assert.Equal(new NotAvailable("bosun report ran past 60 s."), page.Overlap);
            Assert.Equal("Peaks not available: bosun report ran past 60 s.", page.PeaksNote);
            Assert.All(page.Rows, r => Assert.Equal("Not available", r.Peak));
            Assert.DoesNotContain(page.Rows, r => r.Repo == "Isotone");                       // no peaks read: no row from them

            source.Windows = Reading<SnapshotDoc>.Of(Snapshot(), Now);                         // every source readable again
            source.Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(), Now);
            source.Report = Reading<ReportDoc>.Of(ReportOf(), Now);
            tick(RefreshScheduler.Slow + TimeSpan.FromSeconds(1));
            Assert.Equal(new Metric("24.0 GB"), page.WindowsUsed);
            Assert.Equal("Cap 14.0 GB", page.WslNote);
            Assert.Equal(new Metric("3 at once"), page.Overlap);
            Assert.Equal("", page.PeaksNote);
        });
    }

    [Fact]
    public void A_repo_known_only_from_history_on_an_unreadable_side_has_its_current_figures_Not_available()
    {
        // Round 1: WSL running, its snapshot unreadable, and a WSL repo in the history showed 0.0 GB and 0 processes.
        var source = new Source { Wsl = Reading<SnapshotDoc>.Fail(ReadingProblem.Failed, "The WSL logger's file could not be read.") };
        OnPage(source, (page, _, _, _) =>
        {
            var resolute = page.Rows.Single(r => r.Repo == "Resolute");
            Assert.Equal(("WSL", "Not available", "Not available", "8.8"), (resolute.Side, resolute.Private, resolute.ProcessesNow, resolute.Peak));
            var isotone = page.Rows.Single(r => r.Repo == "Isotone");                         // Windows was read: truly not running
            Assert.Equal(("0.0", "0"), (isotone.Private, isotone.ProcessesNow));
        });
    }

    [Fact]
    public void A_repo_under_both_win_and_windows_in_the_history_is_one_row_with_its_highest_peak()
    {
        // Round 1: the two names for one side made the peaks throw, and the page stopped updating unseen.
        var source = new Source
        {
            Report = Reading<ReportDoc>.Of(ReportOf(repos: """{"repo":"D:/Recto","side":"win","peak_private_mb":11674,"peak_rss_mb":1,"peak_processes":1},{"repo":"D:/Recto","side":"windows","peak_private_mb":12288,"peak_rss_mb":1,"peak_processes":1}"""), Now),
        };
        OnPage(source, (page, _, _, _) =>
        {
            Assert.Equal(PageState.Data, page.State);
            var recto = page.Rows.Single(r => r.Repo == "Recto");
            Assert.Equal("12.0", recto.Peak);
            Assert.Equal("3 at once", ((Metric)page.Overlap!).Text);
        });
    }

    [Fact]
    public void A_Windows_snapshot_that_cant_be_read_fails_the_page_and_one_gone_stale_shows_its_age()
    {
        var source = new Source { Windows = Reading<SnapshotDoc>.Fail(ReadingProblem.Missing, "The Windows logger has written nothing yet.") };
        OnPage(source, (page, _, _, tick) =>
        {
            Assert.Equal(PageState.Failed, page.State);
            Assert.Contains("The Windows logger has written nothing yet.", page.FailureMessage);
            source.Windows = Reading<SnapshotDoc>.Stale(Snapshot(), Now, TimeSpan.FromMinutes(4), "The Windows logger last wrote 4 min ago.");
            page.Retry!.Execute(null);
            for (var i = 0; i < 6; i++) Pump();
            Assert.Equal(PageState.Data, page.State);
            Assert.NotNull(page.StaleNote);                                                    // the last data, said to be old
            Assert.Equal(new Metric("24.0 GB"), page.WindowsUsed);
        });
    }

    [Fact]
    public void WSL_not_running_has_no_rows_and_stale_WSL_rows_say_their_age_without_touching_Windows()
    {
        var source = new Source { Windows = Reading<SnapshotDoc>.Of(Snapshot(vm: """{"running":false,"processes":0,"private_mb":0,"rss_mb":0}"""), Now) };
        OnPage(source, (page, _, _, tick) =>
        {
            Assert.Equal(new Metric("Not running"), page.WslVm);
            Assert.DoesNotContain(page.Rows, r => r.Side == "WSL");
            Assert.Equal("", page.WslAge);

            source.Windows = Reading<SnapshotDoc>.Of(Snapshot(), Now);                         // the VM started; its logger is behind
            source.Wsl = Reading<SnapshotDoc>.Stale(WslSnapshot(), Now, TimeSpan.FromMinutes(3), "The WSL logger last wrote 3 min ago.");
            tick(RefreshScheduler.Fast + TimeSpan.FromSeconds(1));
            Assert.Contains(page.Rows, r => r.Side == "WSL");
            Assert.Equal("The WSL figures are not current: The WSL logger last wrote 3 min ago.", page.WslAge);
            Assert.Null(page.StaleNote);                                                       // Windows' own rows are current
        });
    }

    [Fact]
    public void History_not_whole_is_said_beside_its_figures()
    {
        var source = new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(written: false), Now),
            Report = Reading<ReportDoc>.Of(ReportOf(complete: false), Now),
        };
        OnPage(source, (page, _, _, _) =>
        {
            Assert.StartsWith("History is not being written", page.HistoryNote, StringComparison.Ordinal);
            Assert.Equal("The last 24 hours' history is incomplete: 1 file could not be read and 7 rows were rejected, so peaks may be higher.", page.PeaksNote);
        });
    }

    [Fact]
    public void Both_reads_stop_when_hidden_a_slow_report_never_holds_the_snapshots_and_Refresh_runs_both()
    {
        var source = new Source { HoldReport = new TaskCompletionSource() };
        OnPage(source, (page, shell, scheduler, tick) =>
        {
            Assert.Equal(new Metric("24.0 GB"), page.WindowsUsed);                                        // drawn while the report is held
            Assert.IsType<NotAvailable>(page.Overlap);
            Assert.Equal(3, scheduler.Refreshes.Count(r => r.IsActive));                     // snapshots, the day and the fortnight
            var (snapshots, reports) = (source.Snapshots, source.Reports);
            source.HoldReport = null;
            page.RefreshNow!.Execute(null);
            for (var i = 0; i < 6; i++) Pump();
            Assert.True(source.Snapshots > snapshots, "Refresh read the snapshots");
            shell.Show(shell.Sections.First(s => s.Key != "memory"));
            Assert.Equal(0, scheduler.Refreshes.Count(r => r.IsActive));                      // hidden: neither reads
        });
    }

    [Fact]
    public void With_no_repo_the_table_says_so_in_its_own_line_and_fills_when_one_appears()
    {
        var source = new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(repos: " "), Now),
            Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(" "), Now),
            Report = Reading<ReportDoc>.Of(ReportOf(repos: " "), Now),
        };
        OnPage(source, (page, _, _, tick) =>
        {
            Assert.Empty(page.Rows);
            Assert.Equal(MemoryPage.NoRepos, page.TableNote);
            Assert.Equal(PageState.Data, page.State);                                         // the cards still show
            source.Windows = Reading<SnapshotDoc>.Of(Snapshot(), Now);
            tick(RefreshScheduler.Fast + TimeSpan.FromSeconds(1));
            Assert.NotEmpty(page.Rows);
            Assert.Equal("", page.TableNote);
        });
    }

    [Fact]
    public void An_unreadable_card_is_drawn_as_Not_available_with_its_reason_not_as_a_figure()
    {
        // Found by the render: the card's value template drew a NotAvailable as its own text in the metric's size.
        PageRenderer.InPage("Dark", () => Rendered(new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(memory: """{"available_mb":null,"total_mb":32768,"commit_mb":null}"""), Now),
        }).View, page =>
        {
            for (var i = 0; i < 6; i++) Pump();
            PageRenderer.Settle(page);
            var texts = PageRenderer.TextsOf(ShellSupport.Under<MemoryView>(page).Single());
            Assert.Contains("Not available", texts);
            Assert.Contains("The Windows logger did not measure total and available memory.", texts);
            Assert.DoesNotContain(texts, t => t.StartsWith("Not available:", StringComparison.Ordinal) || t == "0.0 GB");
            return 0;
        }, 1360, 860);
    }

    [Theory]
    [InlineData(true, true, false, true, MemoryPage.NoRepos)]
    [InlineData(true, true, true, true, MemoryPage.NoRepos)]
    [InlineData(false, true, false, true, "No repo has processes running now. The last 24 hours' history could not be read, so earlier repos can't be listed.")]
    [InlineData(false, false, false, true, "No repo has processes running now. The last 24 hours' history is still being read.")]
    [InlineData(true, true, true, false, "No repo has processes running on Windows now; WSL's could not be read.")]
    public void An_empty_table_says_only_what_the_reads_established(bool historyRead, bool historyTried, bool vmRunning, bool wslRead, string said) =>
        Assert.Equal(said, MemoryPage.Empty(historyRead, historyTried, vmRunning, wslRead));

    [Fact]
    public void An_empty_table_with_the_history_unread_does_not_claim_none_ran()
    {
        var source = new Source
        {
            Windows = Reading<SnapshotDoc>.Of(Snapshot(repos: " "), Now),
            Wsl = Reading<SnapshotDoc>.Of(WslSnapshot(" "), Now),
            Report = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s."),
        };
        OnPage(source, (page, _, _, _) =>
        {
            Assert.Empty(page.Rows);
            Assert.NotEqual(MemoryPage.NoRepos, page.TableNote);
            Assert.Contains("could not be read", page.TableNote, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_shell_draws_the_page_with_its_cards_and_table()
    {
        PageRenderer.InPage("Dark", () =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["memory"] = new MemoryPage(new Source(), scheduler, log) }), log);
            shell.Show(shell.Sections.Single(s => s.Key == "memory"));
            return new ShellView { DataContext = shell };
        }, page =>
        {
            for (var i = 0; i < 6; i++) Pump();
            PageRenderer.Settle(page);
            var view = ShellSupport.Under<MemoryView>(page).Single();
            var texts = PageRenderer.TextsOf(view);
            foreach (var text in new[] { "Windows used", "24.0 GB", "8.0 GB available of 32.0 GB; committed 37.1 GB", "WSL VM", "11.8 GB", "Overlapping builds", "3 at once", "By repo" })
                Assert.Contains(text, texts);
            var grid = ShellSupport.Under<DataGrid>(view).Single(g => g.Name == "ByRepo");
            Assert.Equal(["Repo", "Side", "Private GB", "Peak GB", "Processes"], grid.Columns.Select(c => (string)c.Header));
            Assert.Equal(4, grid.Items.Count);
            // Round 2: the folder's name alone can't tell two repos apart; the whole path is its tooltip.
            var recto = ShellSupport.Under<TextBlock>(grid).First(t => t.Text == "Recto");
            Assert.Equal("D:/Recto", recto.ToolTip);
            Assert.Equal("D:/Recto", System.Windows.Automation.AutomationProperties.GetHelpText(recto));
            // Each Retry says what it does (D03 T01 §6 completion review).
            var buttons = ShellSupport.Under<Button>(view).Where(b => b.Name.Length > 0).ToDictionary(b => b.Name);
            Assert.Equal("Read the history again now", buttons["HistoryRetry"].ToolTip);
            Assert.Equal("Read the last 14 days again for the decision", buttons["DecisionRetry"].ToolTip);
            Assert.Equal("Retry reading the decision's history", System.Windows.Automation.AutomationProperties.GetName(buttons["DecisionRetry"]));
            return 0;
        }, 1360, 860);
    }

    /// <summary>The shell on the Memory page over <paramref name="source"/>, for the renders: ready once the page has drawn.</summary>
    internal static void OnMemoryPage(Source source, Action<MemoryPage, ShellViewModel, RefreshScheduler, Action<TimeSpan>> test, Bosun.Core.PanelSettingsStore? settings = null) =>
        OnPage(source, test, settings);

    internal static void Settle()
    {
        for (var i = 0; i < 6; i++) Pump();
    }

    internal static (System.Windows.FrameworkElement View, Func<System.Windows.FrameworkElement, Task> Ready) Rendered(Source source, Action<MemoryPage>? then = null,
        Bosun.Core.PanelSettingsStore? settings = null)
    {
        var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
        var log = Shells.Log();
        var page = new MemoryPage(source, scheduler, log, settings);
        var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["memory"] = page }), log);
        shell.Show(shell.Sections.Single(s => s.Key == "memory"));
        return (new ShellView { DataContext = shell }, async _ =>
        {
            var settles = source.HoldSnapshots is null;
            // Held loading: the skeleton appears only after its delay (Skeleton.DefaultDelay), so a quick load never flashes.
            if (!settles) await Task.Delay(Bosun.Panel.Controls.Skeleton.DefaultDelay + TimeSpan.FromMilliseconds(150));
            if (then is not null)
            {
                for (var i = 0; i < 20; i++) await Dispatcher.Yield(DispatcherPriority.Background);
                // The card's first reading is in before what follows changes the source.
                for (var i = 0; i < 50 && source.HoldFortnight is null && page.DecisionState == MemoryPage.ReadingDecision; i++)
                    await Dispatcher.Yield(DispatcherPriority.Background);
                var fortnights = source.Fortnights;
                then(page);
                // What it started (a refresh) is let finish: the fortnight it asked for is read and drawn.
                for (var i = 0; i < 50 && source.Fortnights == fortnights && source.HoldFortnight is null; i++) await Dispatcher.Yield(DispatcherPriority.Background);
                for (var i = 0; i < 10; i++) await Dispatcher.Yield(DispatcherPriority.Background);
            }
            for (var i = 0; i < 50 && settles && page.State != PageState.Failed && (page.State == PageState.Loading || page.Overlap is null || page.Rows.Count == 0 && page.TableNote.Length == 0); i++)
                await Dispatcher.Yield(DispatcherPriority.Background);
        });
    }
}
