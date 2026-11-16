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
        public TaskCompletionSource? HoldReport;
        public int Snapshots, Reports;

        public Task<Reading<SnapshotDoc>> SnapshotAsync(string side, CancellationToken cancellationToken = default)
        {
            if (side == "windows") Snapshots++;
            return Task.FromResult(side == "windows" ? Windows : Wsl);
        }

        public async Task<Reading<ReportDoc>> ReportAsync(int days, CancellationToken cancellationToken = default)
        {
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
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"windows","at":"2026-11-16T09:00:00Z","interval_s":5,"memory":{{memory}},"wsl_vm":{{vm}},"repos":[{{(repos.Length > 0 ? repos : Repos(("D:/Recto", 6348, 14), ("D:/Bosun", 922, 5)))}}],"processes":[],"history_written":{{(written ? "true" : "false")}}}""",
            MemoryJson.Default.SnapshotDoc)!;

    internal static SnapshotDoc WslSnapshot(string? repos = null) =>
        JsonSerializer.Deserialize($$"""{"schema_version":1,"side":"wsl","at":"2026-11-16T09:00:00Z","interval_s":5,"memory":{"available_mb":6000,"total_mb":14336},"wsl_vm":null,"repos":[{{repos ?? Repos(("/home/me/Resolute", 4505, 22))}}],"processes":[],"history_written":true}""",
            MemoryJson.Default.SnapshotDoc)!;

    internal static ReportDoc ReportOf(bool complete = true, string? repos = null, long overlap = 3) =>
        JsonSerializer.Deserialize($$$"""{"schema_version":1,"generated_at":"2026-11-16T09:00:00Z","since":"2026-11-15T09:00:00Z","days_requested":1,"days_covered":1,"first_sample":"2026-11-15T09:00:05","last_sample":"2026-11-16T08:59:55","low_mb":2048,"read":{"complete":{{{(complete ? "true" : "false")}}},"files":2,"unreadable_files":{{{(complete ? 0 : 1)}}},"rejected_rows":{{{(complete ? 0 : 7)}}}},"windows":{"samples":17000,"low_samples":3,"min_available_mb":1536,"worst_minute":"2026-11-16T07:20","worst_repos":[],"max_commit_mb":41000,"commit_limit_mb":65536,"max_pages_in_per_sec":40},"wsl":{"samples":17000,"min_available_mb":2000,"max_swap_mb":512,"max_psi_full_avg10_x100":0,"max_oom_kills":0},"builds":{"jobs":9,"max_overlap":{{{overlap}}},"at":"2026-11-16T07:20:00Z"},"active_repos":{"max":4,"minute":"2026-11-16T07:20","total_private_mb":20000},"repos":[{{{repos ?? """{"repo":"D:/Recto","side":"win","peak_private_mb":11674,"peak_rss_mb":9000,"peak_processes":30},{"repo":"/home/me/Resolute","side":"wsl","peak_private_mb":9011,"peak_rss_mb":8000,"peak_processes":40},{"repo":"D:/Isotone","side":"win","peak_private_mb":7168,"peak_rss_mb":6000,"peak_processes":12}"""}}}],"series":[]}""",
            MemoryJson.Default.ReportDoc)!;

    static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A shell whose Memory page reads <paramref name="source"/>, shown, and a clock the test moves.</summary>
    static void OnPage(Source source, Action<MemoryPage, ShellViewModel, RefreshScheduler, Action<TimeSpan>> test)
    {
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var page = new MemoryPage(source, scheduler, log);
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
            Assert.Equal("Committed 37.1 GB", page.WindowsNote);                            // commit_mb 38000
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
            Assert.Equal("Committed: not measured", page.WindowsNote);
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
            Assert.Equal("The last 24 hours' history is incomplete: 1 files could not be read and 7 rows were rejected, so peaks may be higher.", page.PeaksNote);
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
            Assert.Equal(2, scheduler.Refreshes.Count(r => r.IsActive));
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
            foreach (var text in new[] { "Windows used", "24.0 GB", "WSL VM", "11.8 GB", "Overlapping builds", "3 at once", "By repo" }) Assert.Contains(text, texts);
            var grid = ShellSupport.Under<DataGrid>(view).Single();
            Assert.Equal(["Repo", "Side", "Private GB", "Peak GB", "Processes"], grid.Columns.Select(c => (string)c.Header));
            Assert.Equal(4, grid.Items.Count);
            return 0;
        }, 1360, 860);
    }

    /// <summary>The shell on the Memory page over <paramref name="source"/>, for the renders: ready once the page has drawn.</summary>
    internal static (System.Windows.FrameworkElement View, Func<System.Windows.FrameworkElement, Task> Ready) Rendered(Source source)
    {
        var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
        var log = Shells.Log();
        var page = new MemoryPage(source, scheduler, log);
        var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["memory"] = page }), log);
        shell.Show(shell.Sections.Single(s => s.Key == "memory"));
        return (new ShellView { DataContext = shell }, async _ =>
        {
            for (var i = 0; i < 50 && (page.State == PageState.Loading || page.Overlap is null || page.Rows.Count == 0 && page.TableNote.Length == 0); i++)
                await Dispatcher.Yield(DispatcherPriority.Background);
        });
    }
}
