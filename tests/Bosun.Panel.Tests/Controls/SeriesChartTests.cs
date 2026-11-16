using System.Diagnostics;
using System.Windows;
using System.Windows.Automation.Peers;
using Bosun.Core.Memory;
using Bosun.Panel.Controls;
using Bosun.Panel.Memory;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The history chart (D03 T01 §3): the report's minutes put into each range's buckets, each its lowest at its minute;
/// a bucket with no sample breaks the line; the pressure line at the stored threshold; drawn by the panel itself.
/// </summary>
public class SeriesChartTests
{
    static readonly DateTime End = new(2026, 11, 16, 12, 0, 0, DateTimeKind.Utc);

    static SeriesPointDoc At(DateTime utc, long? mb) => new()
    {
        Minute = utc.ToString("yyyy-MM-dd'T'HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        WinAvailableMb = mb, WslAvailableMb = null, Repos = 0, RepoPrivateMb = 0,
    };

    [Fact]
    public void Each_bucket_is_its_lowest_minute_at_that_minute_within_the_range()
    {
        var series = new[]
        {
            At(End.AddMinutes(-3), 5000), At(End.AddMinutes(-2), 4000), At(End.AddMinutes(-1), 4500),          // the last bucket: 4000 at -2
            At(End.AddMinutes(-7), 6000),                                                                       // the one before
            At(End.AddHours(-25), 100),                                                                         // before the day: left out
            At(End.AddMinutes(-9), null),                                                                       // no Windows sample: not 0
        };
        var points = History.Buckets(series, HistoryRange.Day, End);
        Assert.Equal(288, points.Count);                                                                        // 24 hours of 5 minutes
        Assert.Equal(new DateTime(2026, 11, 15, 12, 5, 0, DateTimeKind.Utc), points[0].Start);
        Assert.Equal(End, points[^1].Start);
        var last = points[^2];                                                                                  // 11:55 to 12:00
        Assert.Equal((End.AddMinutes(-5), End.AddMinutes(-2), (long?)4000), (last.Start, last.LowestAt, last.LowestMb));
        Assert.Equal((long?)6000, points[^3].LowestMb);
        Assert.Equal(2, points.Count(p => p.LowestMb is not null));                                             // -3, -2 and -1 share one bucket
        Assert.Equal("No sample", points[0].Lowest);
        Assert.Equal((long?)4000, History.Lowest(points)!.LowestMb);
        Assert.Equal(336, History.Buckets(series, HistoryRange.Week, End).Count);
        Assert.Equal(336, History.Buckets(series, HistoryRange.Fortnight, End).Count);
    }

    [Fact]
    public void A_missing_bucket_breaks_the_line_and_never_joins_across_it()
    {
        var points = new[]
        {
            new HistoryPoint(End, End, 4096), new HistoryPoint(End, End, 2048), new HistoryPoint(End, null, null), new HistoryPoint(End, End, 1024),
        };
        var runs = SeriesChart.Layout(points, 300, 100, 4096);
        Assert.Equal(2, runs.Count);                                                                            // broken at the gap
        Assert.Equal([new Point(0, 0), new Point(100, 50)], runs[0]);                                         // 4096 at the top, 2048 half way
        Assert.Equal([new Point(300, 75)], runs[1]);                                                           // 1024 a quarter up, alone
        Assert.Empty(SeriesChart.Layout([new HistoryPoint(End, null, null)], 300, 100, 4096));                 // nothing measured: no line
    }

    [Fact]
    public void It_draws_its_runs_and_the_pressure_line_where_the_threshold_is_and_moves_it_with_no_restart()
    {
        var points = Enumerable.Range(0, 10).Select(i => new HistoryPoint(End, End, 4096L + i * 100)).ToList();
        PageRenderer.InPage("Dark", () => new SeriesChart { Points = points, FloorMb = 2048, Width = 400, Height = 200 }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            Assert.Single(chart.Runs);
            Assert.Equal(10, chart.Runs[0].Count);
            var plot = 200 - SeriesChart.Inset;
            Assert.Equal(plot - plot * 2048 / chart.TopMb, chart.FloorY, 6);
            var before = chart.FloorY;
            chart.FloorMb = 3072;                                                                               // Settings saved another
            PageRenderer.Settle(page);
            Assert.True(chart.FloorY < before, "a higher threshold draws the line higher");
            var pixels = PageRenderer.Pixels(page);
            Assert.Contains(pixels, p => p != pixels[0]);                                                      // something is drawn
            return 0;
        });
    }

    [Fact]
    public void A_screen_reader_hears_the_range_the_lowest_point_and_the_line()
    {
        var low = new DateTime(2026, 11, 16, 7, 20, 0, DateTimeKind.Utc);
        PageRenderer.InPage("Dark", () => new SeriesChart
        {
            Points = [new HistoryPoint(low, low, 1536), new HistoryPoint(End, End, 8192)], FloorMb = 2048, RangeName = "24 hours",
        }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            var said = UIElementAutomationPeer.CreatePeerForElement(chart).GetName();
            Assert.Equal($"Available memory, 24 hours: lowest 1.5 GB at {low.ToLocalTime():ddd d MMM HH:mm}; pressure line 2.0 GB", said);
            chart.Points = [new HistoryPoint(End, null, null)];
            Assert.Equal("Available memory, 24 hours: no history in this range yet; pressure line 2.0 GB", UIElementAutomationPeer.CreatePeerForElement(chart).GetName());
            return 0;
        });
    }

    [Fact]
    public void Fourteen_days_of_minutes_bucket_and_draw_in_under_50_ms()
    {
        var series = Enumerable.Range(0, 14 * 24 * 60).Select(m => At(End.AddMinutes(-m), 4000 + m % 997)).ToList();
        PageRenderer.InPage("Dark", () => new SeriesChart { Width = 900, Height = 200 }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            History.Buckets(series, HistoryRange.Fortnight, End);                                               // warmed
            var clock = Stopwatch.StartNew();
            chart.Points = History.Buckets(series, HistoryRange.Fortnight, End);
            PageRenderer.Settle(page);
            clock.Stop();
            Assert.Equal(336, chart.Runs.Single().Count);                                                       // drawn, not blank
            Assert.True(clock.ElapsedMilliseconds < 50, $"took {clock.ElapsedMilliseconds} ms");
            return 0;
        });
    }
}
