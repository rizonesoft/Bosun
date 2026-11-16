using System.Globalization;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Memory;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tests.Shell;
using Xunit;
using static Bosun.Panel.Tests.Memory.MemoryPageTests;

namespace Bosun.Panel.Tests.Memory;

/// <summary>
/// The Memory page's history (D03 T01 §3): each range from its own report, the chart and the table the same points,
/// the history's own states, the pressure line the stored threshold, and the third read with the page.
/// </summary>
public class MemoryHistoryTests
{
    /// <summary>A report's series: a minute each, in the logger's UTC, ending at the report's 09:00.</summary>
    internal static string Series(params (int MinutesBefore, long? Mb)[] points) => string.Join(",", points.Select(p =>
        $$"""{"minute":"{{Now.UtcDateTime.AddMinutes(-p.MinutesBefore).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)}}","win_available_mb":{{(p.Mb is { } mb ? mb.ToString(CultureInfo.InvariantCulture) : "null")}},"wsl_available_mb":null,"repos":0,"repo_private_mb":0}"""));

    static Reading<ReportDoc> Day(string series) => Reading<ReportDoc>.Of(ReportOf(series: series), Now);

    [Fact]
    public void Each_range_draws_its_own_report_and_a_switch_mid_read_never_shows_the_last_range()
    {
        var source = new Source
        {
            Report = Day(Series((3, 1536), (60, 6000))),
            Fortnight = Reading<ReportDoc>.Of(ReportOf(series: Series((3 * 24 * 60, 900))), Now),
        };
        source.HoldFortnight = new TaskCompletionSource();
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.Equal(289, page.Points.Count);
            Assert.Equal((long?)1536, History.Lowest(page.Points)!.LowestMb);                 // from the day report
            Assert.StartsWith("Lowest 1.5 GB at ", page.LowestSummary, StringComparison.Ordinal);
            page.Range = HistoryRange.Week;                                                    // the fortnight is still being read
            Assert.Empty(page.Points);                                                          // not the day's points
            Assert.Equal(MemoryPage.ReadingHistory, page.HistoryState);
            source.HoldFortnight!.SetResult();
            Settle();
            Assert.Equal(337, page.Points.Count);
            Assert.Equal((long?)900, History.Lowest(page.Points)!.LowestMb);                  // three days back, inside the week
            page.Range = HistoryRange.Day;
            Assert.Equal((long?)1536, History.Lowest(page.Points)!.LowestMb);
        });
    }

    [Fact]
    public void A_history_that_cant_be_read_says_why_and_a_later_failure_keeps_the_last_points_with_their_time()
    {
        var source = new Source { Report = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s.") };
        OnMemoryPage(source, (page, _, _, tick) =>
        {
            Assert.Empty(page.Points);
            Assert.Equal("The history could not be read: bosun report ran past 60 s.", page.HistoryState);
            source.Report = Day(Series((10, 4096)));                                            // read at last
            tick(Bosun.Core.RefreshScheduler.Slow + TimeSpan.FromSeconds(1));
            Assert.Equal("", page.HistoryState);
            source.Report = Reading<ReportDoc>.Fail(ReadingProblem.Failed, "bosun report ran past 60 s.");
            tick(Bosun.Core.RefreshScheduler.Slow + TimeSpan.FromSeconds(1));
            Assert.Equal((long?)4096, History.Lowest(page.Points)!.LowestMb);                  // the last points kept
            Assert.StartsWith("The history could not be read again: bosun report ran past 60 s. Showing the read from ", page.HistoryState, StringComparison.Ordinal);
            Assert.EndsWith(" ago.", page.HistoryState, StringComparison.Ordinal);                             // its day and its age, never a bare time
            Assert.True(page.HistoryFailed);
            Assert.Contains(page.PaletteCommands, c => c.Name == "Retry reading the history");                // its own Retry, the cards still live
            Assert.Equal(Bosun.Panel.Shell.PageState.Data, page.State);
            source.Report = Day(Series((10, 4096)));
            page.PaletteCommands.Single(c => c.Name == "Retry reading the history").Command.Execute(null);
            Settle();
            Assert.False(page.HistoryFailed);                                                                    // recovered
            Assert.Equal("", page.HistoryState);
            Assert.DoesNotContain(page.PaletteCommands, c => c.Name == "Retry reading the history");
        });
    }

    [Fact]
    public void No_Windows_sample_in_the_range_says_so_and_an_incomplete_read_is_said()
    {
        var source = new Source { Report = Reading<ReportDoc>.Of(ReportOf(complete: false, series: Series((5, null))), Now) };
        OnMemoryPage(source, (page, _, _, _) =>
        {
            Assert.All(page.Points, p => Assert.Null(p.LowestMb));                              // a null sample is missing, not 0
            Assert.Equal("", page.LowestSummary);
            Assert.Contains(MemoryPage.NoHistory, page.HistoryState, StringComparison.Ordinal);
            Assert.Contains("The history is incomplete: 1 file could not be read and 7 rows were rejected.", page.HistoryState, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void The_pressure_line_is_the_stored_threshold_and_moves_when_Settings_saves_another()
    {
        var settings = new SettingsFixture().Open();
        settings.Store.Update(s => s with { PressureFloorMb = 3072 });
        OnMemoryPage(new Source(), (page, _, _, _) =>
        {
            Assert.Equal(3072, page.FloorMb);
            Assert.Equal("Dashed line: the 3.0 GB pressure alert.", page.FloorNote);
            settings.Store.Update(s => s with { PressureFloorMb = 4096 });                        // saved, no restart
            Settle();
            Assert.Equal(4096, page.FloorMb);
        }, settings.Store);
    }

    [Fact]
    public void Show_as_table_and_as_chart_are_buttons_and_palette_commands_the_table_the_same_points()
    {
        OnMemoryPage(new Source { Report = Day(Series((3, 1536))) }, (page, shell, _, _) =>
        {
            Assert.False(page.ShowTable);
            Assert.Equal(["Show the history as a table"], page.PaletteCommands.Where(c => c.Name.StartsWith("Show", StringComparison.Ordinal)).Select(c => c.Name));
            shell.Palette.Open();
            Assert.Contains(shell.Palette.Rows, r => r.Name == "Show the history as a table");
            shell.Palette.Close();
            page.ShowAsTable.Execute(null);
            Assert.True(page.ShowTable);
            Assert.Equal(["Show the history as a chart"], page.PaletteCommands.Where(c => c.Name.StartsWith("Show", StringComparison.Ordinal)).Select(c => c.Name));
            page.ShowAsChart.Execute(null);
            Assert.False(page.ShowTable);
        });
    }

    [Fact]
    public void The_view_toggles_between_the_chart_and_its_table_of_the_same_points_and_says_the_range()
    {
        PageRenderer.InPage("Dark", () => Rendered(new Source { Report = Day(Series((3, 1536), (9, 2000))) }).View, page =>
        {
            Settle();
            PageRenderer.Settle(page);
            var view = ShellSupport.Under<MemoryView>(page).Single();
            var memory = (MemoryPage)view.DataContext;
            var chart = ShellSupport.Under<SeriesChart>(view).Single();
            var table = ShellSupport.Under<DataGrid>(view).First(g => g.Name == "HistoryTable");
            Assert.True(chart.IsVisible);
            Assert.False(table.IsVisible);
            Assert.StartsWith("Available memory, 24 hours: lowest 1.5 GB at ", UIElementAutomationPeer.CreatePeerForElement(chart).GetName(), StringComparison.Ordinal);
            memory.ShowAsTable.Execute(null);
            PageRenderer.Settle(page);
            Assert.False(chart.IsVisible);
            Assert.True(table.IsVisible);
            Assert.Equal(["Time", "Lowest available"], table.Columns.Select(c => (string)c.Header));
            Assert.Equal(memory.Points.Count, table.Items.Count);                                // the chart's own points
            Assert.Contains(table.Items.Cast<HistoryPoint>(), p => p.Lowest == "1.5 GB");
            Assert.Contains(table.Items.Cast<HistoryPoint>(), p => p.Lowest == "No sample");
            // Sorted by value and by time, never by their words (completion review: "10.0 GB" before "2.0 GB").
            Assert.Equal(["At", "LowestSort"], table.Columns.Select(c => c.SortMemberPath));
            var rows = System.Windows.Data.CollectionViewSource.GetDefaultView(table.ItemsSource);
            rows.SortDescriptions.Add(new System.ComponentModel.SortDescription("LowestSort", System.ComponentModel.ListSortDirection.Ascending));
            var sorted = rows.Cast<HistoryPoint>().ToList();
            Assert.Equal("1.5 GB", sorted[0].Lowest);
            Assert.Equal("No sample", sorted[^1].Lowest);                                                    // a gap after every value
            rows.SortDescriptions.Clear();
            return 0;
        }, 1360, 1100);
    }

    [Fact]
    public void The_third_read_starts_stops_and_refreshes_with_the_page()
    {
        var source = new Source();
        OnMemoryPage(source, (page, shell, scheduler, _) =>
        {
            Assert.Equal(1, source.Fortnights);                                                  // read as the page was shown
            var reads = source.Fortnights;
            page.RefreshNow!.Execute(null);
            Settle();
            Assert.True(source.Fortnights > reads, "Refresh read the fortnight too");
            shell.Show(shell.Sections.First(s => s.Key != "memory"));
            Assert.Equal(0, scheduler.Refreshes.Count(r => r.IsActive));                         // hidden: none of the three reads
        });
    }
}
