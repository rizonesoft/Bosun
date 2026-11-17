using System.Windows;
using Bosun.Panel.Controls;
using Bosun.Panel.Memory;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The chart's labels and axis (D02 T04 §5): no label over any drawn segment, isolated point or the pressure line, all
/// inside the chart and apart; the span the page gives, so the last point and the end label are at the report's time;
/// ticks on the local clock across a daylight-saving change, each drawn where its time is.
/// </summary>
public class ChartAxisTests
{
    static readonly DateTime To = new(2026, 11, 16, 11, 37, 0, DateTimeKind.Utc); // not on a bucket's edge

    /// <summary>A day falling from high at its start to under the line at its end, with a gap and a lone point.</summary>
    static List<HistoryPoint> Day()
    {
        var from = To - HistoryRange.Day.Length;
        var points = new List<HistoryPoint>();
        for (var i = 0; i < 288; i++)
        {
            var at = from.AddMinutes(5 * i);
            long? mb = i is > 100 and < 110 or 150 or 152 ? null : 16000 - i * 50;
            points.Add(new HistoryPoint(at, mb is null ? null : at, mb));
        }
        points.Add(new HistoryPoint(To, To, 1500));
        return points;
    }

    static bool Crosses(Point a, Point b, Rect box)
    {
        if (box.Contains(a) || box.Contains(b)) return true;
        // Sampled along the segment: fine enough for 1 px boxes.
        var steps = (int)Math.Max(2, (b - a).Length);
        for (var i = 1; i < steps; i++)
            if (box.Contains(a + (b - a) * i / steps)) return true;
        return false;
    }

    /// <summary>The same falling line, over any range: one point per range's bucket.</summary>
    static List<HistoryPoint> Over(HistoryRange range)
    {
        var from = To - range.Length;
        var count = (int)(range.Length / range.Bucket);
        var points = new List<HistoryPoint>();
        for (var i = 0; i < count; i++)
        {
            var at = from + range.Bucket * i;
            long? mb = i == count / 3 ? null : 16000 - 14000L * i / count;
            points.Add(new HistoryPoint(at, mb is null ? null : at, mb));
        }
        points.Add(new HistoryPoint(To, To, 1500));
        return points;
    }

    public static readonly TheoryData<string, int, string> RangesWidthsEnds = new()
    {
        { "24 hours", 1100, "Now" }, { "24 hours", 560, "Now" }, { "24 hours", 560, "11:37, 3 hours ago" },
        { "7 days", 1100, "Now" }, { "7 days", 560, "Now" }, { "7 days", 560, "11:37, 3 hours ago" },
        { "14 days", 1100, "Now" }, { "14 days", 560, "Now" }, { "14 days", 560, "11:37, 3 hours ago" },
    };

    static HistoryRange Named(string name) => name == HistoryRange.Day.Name ? HistoryRange.Day : name == HistoryRange.Week.Name ? HistoryRange.Week : HistoryRange.Fortnight;

    [Theory]
    [MemberData(nameof(RangesWidthsEnds))]
    public void No_label_lies_over_the_line_a_lone_point_or_the_pressure_line_and_all_are_inside_and_apart(string rangeName, int width, string end)
    {
        var range = Named(rangeName);
        PageRenderer.InPage<int>("Dark", () => new SeriesChart
        {
            Points = range == HistoryRange.Day ? Day() : Over(range), FloorMb = 2048, Width = width, Height = 220, RangeName = range.Name, Bucket = range.Bucket,
            From = To - range.Length, To = To, EndText = end,
        }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            Assert.True(chart.Labels.Count >= 4, $"{chart.Labels.Count} labels");
            foreach (var (text, box) in chart.Labels)
            {
                foreach (var run in chart.Runs)
                {
                    if (run.Count == 1) Assert.False(new Rect(run[0].X - 2, run[0].Y - 2, 4, 4).IntersectsWith(box), $"'{text}' covers a lone point");
                    for (var i = 1; i < run.Count; i++) Assert.False(Crosses(run[i - 1], run[i], box), $"'{text}' at {box} lies over the line");
                }
                Assert.False(Crosses(new Point(chart.Plot.Left, chart.FloorY), new Point(chart.Plot.Right, chart.FloorY), box), $"'{text}' lies over the pressure line");
                Assert.True(box.Left >= 0 && box.Top >= 0 && box.Right <= chart.ActualWidth + 0.5 && box.Bottom <= chart.ActualHeight + 0.5, $"'{text}' at {box} leaves the chart");
                foreach (var (other, otherBox) in chart.Labels)
                    if (!ReferenceEquals(text, other)) Assert.False(box.IntersectsWith(otherBox), $"'{text}' overlaps '{other}'");
            }
            return 0;
        }, width + 40, 280);
    }

    [Fact]
    public void With_nothing_read_and_no_span_there_is_no_axis_and_no_made_up_times()
    {
        PageRenderer.InPage<int>("Dark", () => new SeriesChart { Points = [], FloorMb = 2048, Width = 600, Height = 220, RangeName = HistoryRange.Day.Name }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            Assert.Empty(chart.Ticks);
            Assert.DoesNotContain(chart.Labels, l => l.Text.Contains(':', StringComparison.Ordinal) || l.Text == "Now");
            return 0;
        }, 640, 280);
    }

    [Fact]
    public void The_last_point_and_the_end_label_are_at_the_report_s_time_not_the_last_bucket_s_end()
    {
        PageRenderer.InPage<int>("Dark", () => new SeriesChart
        {
            Points = Day(), FloorMb = 2048, Width = 800, Height = 220, RangeName = HistoryRange.Day.Name, Bucket = HistoryRange.Day.Bucket,
            From = To - HistoryRange.Day.Length, To = To, EndText = "Now",
        }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            Assert.Equal(chart.Plot.Right, chart.Runs[^1][^1].X, 0.5);                                         // the report's minute, at the end
            var end = chart.Labels.Single(l => l.Text == "Now").Box;
            Assert.Equal(chart.ActualWidth, end.Right, 0.5);
            return 0;
        }, 840, 280);
    }

    [Fact]
    public void Ticks_fall_on_the_local_clock_across_a_daylight_saving_change()
    {
        var london = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");
        // Clocks go forward at 01:00 GMT on 29 March 2026.
        var from = new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc);
        var day = SeriesChart.TickTimes(from, from.AddDays(1), HistoryRange.Day.Name, london);
        Assert.Equal(4, day.Count);
        Assert.All(day, t => Assert.Equal(0, TimeZoneInfo.ConvertTimeFromUtc(t, london).Hour % 6));
        Assert.Equal(new DateTime(2026, 3, 29, 5, 0, 0, DateTimeKind.Utc), day[2]);                            // 06:00 summer time

        var weekFrom = new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc);
        var week = SeriesChart.TickTimes(weekFrom, weekFrom.AddDays(7), HistoryRange.Week.Name, TimeZoneInfo.Utc);
        Assert.Equal(7, week.Count);
        Assert.All(week, t => Assert.Equal(TimeSpan.Zero, t.TimeOfDay));
        var fortnight = SeriesChart.TickTimes(weekFrom, weekFrom.AddDays(14), HistoryRange.Fortnight.Name, TimeZoneInfo.Utc);
        Assert.Equal(7, fortnight.Count);
        Assert.All(fortnight.Zip(fortnight.Skip(1)), p => Assert.Equal(TimeSpan.FromDays(2), p.Second - p.First));
    }

    [Theory]
    [InlineData("24 hours")]
    [InlineData("7 days")]
    [InlineData("14 days")]
    public void Each_tick_is_drawn_where_its_time_is_and_its_label_kept_when_it_fits(string range)
    {
        var length = range == "24 hours" ? HistoryRange.Day.Length : range == "7 days" ? HistoryRange.Week.Length : HistoryRange.Fortnight.Length;
        var from = To - length;
        PageRenderer.InPage<int>("Dark", () => new SeriesChart
        {
            Points = [new HistoryPoint(from, from, 8000), new HistoryPoint(To, To, 6000)], FloorMb = 2048, Width = 1100, Height = 220,
            RangeName = range, From = from, To = To, EndText = "Now",
        }, page =>
        {
            var chart = ShellSupport.Under<SeriesChart>(page).Single();
            var want = SeriesChart.TickTimes(from, To, range);
            Assert.Equal(want, chart.Ticks.Select(t => t.At));
            foreach (var (at, x, _) in chart.Ticks)
                Assert.Equal(chart.Plot.Left + chart.Plot.Width * (at - from).Ticks / (To - from).Ticks, x, 0.5);
            Assert.True(chart.Ticks.Count(t => t.Text is not null) >= want.Count / 2, "most ticks keep their label at this width");
            return 0;
        }, 1140, 280);
    }
}
