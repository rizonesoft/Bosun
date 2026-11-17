using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Bosun.Panel.Memory;

namespace Bosun.Panel.Controls;

/// <summary>
/// The history chart (D03 T01 §3, design/SPEC.md §3): available memory over a range, drawn by the panel itself with no
/// chart package. Each point is a bucket's lowest; a bucket with no sample breaks the line, so nothing joins two
/// measured points across a gap. The pressure line is dashed at the stored threshold, labelled with its value, and
/// moves when the threshold does. The y axis runs from 0 to the highest of the points and the line, in GB. A screen
/// reader hears the range, the lowest point and its time, and the line.
/// </summary>
public class SeriesChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<HistoryPoint>), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSaid));

    public static readonly DependencyProperty FloorMbProperty = DependencyProperty.Register(
        nameof(FloorMb), typeof(long), typeof(SeriesChart), new FrameworkPropertyMetadata(2048L, FrameworkPropertyMetadataOptions.AffectsRender, OnSaid));

    public static readonly DependencyProperty BucketProperty = DependencyProperty.Register(
        nameof(Bucket), typeof(TimeSpan), typeof(SeriesChart), new FrameworkPropertyMetadata(TimeSpan.FromMinutes(5), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RangeNameProperty = DependencyProperty.Register(
        nameof(RangeName), typeof(string), typeof(SeriesChart), new FrameworkPropertyMetadata("24 hours", OnSaid));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(Brush), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PressureProperty = DependencyProperty.Register(
        nameof(Pressure), typeof(Brush), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FromProperty = DependencyProperty.Register(
        nameof(From), typeof(DateTime?), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToProperty = DependencyProperty.Register(
        nameof(To), typeof(DateTime?), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EndTextProperty = DependencyProperty.Register(
        nameof(EndText), typeof(string), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(Brush), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The room under the line for the axis labels.</summary>
    public const double Inset = 18;

    static SeriesChart()
    {
        HeightProperty.OverrideMetadata(typeof(SeriesChart), new FrameworkPropertyMetadata(180.0));
        FocusableProperty.OverrideMetadata(typeof(SeriesChart), new FrameworkPropertyMetadata(false));
    }

    public SeriesChart()
    {
        SetResourceReference(LineProperty, "B.Accent");
        SetResourceReference(PressureProperty, "B.Danger");
        SetResourceReference(LabelProperty, "B.TextDim");
    }

    public IReadOnlyList<HistoryPoint>? Points { get => (IReadOnlyList<HistoryPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    /// <summary>The pressure line, in MB.</summary>
    public long FloorMb { get => (long)GetValue(FloorMbProperty); set => SetValue(FloorMbProperty, value); }

    public string RangeName { get => (string)GetValue(RangeNameProperty); set => SetValue(RangeNameProperty, value); }

    /// <summary>The range's bucket, so the x axis runs to the last bucket's end.</summary>
    public TimeSpan Bucket { get => (TimeSpan)GetValue(BucketProperty); set => SetValue(BucketProperty, value); }

    public Brush? Line { get => (Brush?)GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public Brush? Pressure { get => (Brush?)GetValue(PressureProperty); set => SetValue(PressureProperty, value); }

    public Brush? Label { get => (Brush?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    /// <summary>
    /// The span the chart covers, in UTC, from its page (D02 T04 §5): the report's own period, so points and ticks share
    /// one mapping and the end is the report's time, not the last bucket's end up to an hour later. Null: the points'.
    /// </summary>
    public DateTime? From { get => (DateTime?)GetValue(FromProperty); set => SetValue(FromProperty, value); }

    public DateTime? To { get => (DateTime?)GetValue(ToProperty); set => SetValue(ToProperty, value); }

    /// <summary>What the axis's end says: "Now" while the history is current, a kept reading's time and age otherwise.</summary>
    public string? EndText { get => (string?)GetValue(EndTextProperty); set => SetValue(EndTextProperty, value); }

    /// <summary>The area the line is plotted in: inside the left gutter that holds the value labels, above the axis.</summary>
    public Rect Plot { get; private set; }

    /// <summary>Every label drawn, by its box: the value labels, the axis's ends and each tick label kept.</summary>
    public IReadOnlyList<(string Text, Rect Box)> Labels { get; private set; } = [];

    /// <summary>Each tick: its time, where it is drawn, and its label when one was kept (null when it would collide).</summary>
    public IReadOnlyList<(DateTime At, double X, string? Text)> Ticks { get; private set; } = [];

    /// <summary>
    /// Where ticks go between two UTC times (D02 T04 §5), placed by local time so a daylight-saving change moves none off
    /// its hour: every 6 hours on the local clock for a day, each local midnight for a week, every other one for a fortnight.
    /// </summary>
    public static IReadOnlyList<DateTime> TickTimes(DateTime fromUtc, DateTime toUtc, string rangeName, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var ticks = new List<DateTime>();
        var local = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone);
        var day = rangeName == HistoryRange.Day.Name;
        var cursor = day ? new DateTime(local.Year, local.Month, local.Day, local.Hour / 6 * 6, 0, 0) : local.Date;
        for (var guard = 0; guard < 400; guard++)
        {
            cursor = day ? cursor.AddHours(6) : cursor.AddDays(1);
            if (zone.IsInvalidTime(cursor)) continue;
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(cursor, DateTimeKind.Unspecified), zone);
            if (utc >= toUtc) break;
            if (utc <= fromUtc) continue;
            if (rangeName == HistoryRange.Fortnight.Name && DateOnly.FromDateTime(cursor).DayNumber % 2 != 0) continue;
            ticks.Add(utc);
        }
        return ticks;
    }

    /// <summary>The runs of the line last drawn: each an unbroken run of measured points, a gap between two.</summary>
    public IReadOnlyList<IReadOnlyList<Point>> Runs { get; private set; } = [];

    /// <summary>Where the pressure line was last drawn, from the top.</summary>
    public double FloorY { get; private set; } = double.NaN;

    /// <summary>The highest value of the y axis last drawn, in MB.</summary>
    public long TopMb { get; private set; }

    /// <summary>What a screen reader hears.</summary>
    public string SpokenName
    {
        get
        {
            var line = $"pressure line {(FloorMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} GB";
            if (Points is not { } points || History.Lowest(points) is not { } low) return $"Available memory, {RangeName}: no history in this range yet; {line}";
            return $"Available memory, {RangeName}: lowest {low.Lowest} at {low.LowestAt!.Value.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture)}; {line}";
        }
    }

    /// <summary>
    /// The runs of measured points laid out in a box, the line breaking at every missing bucket. Each point is placed at
    /// its own minute between the first bucket's start and the last bucket's end (completion review: a minimum at 14:59
    /// had been drawn at its bucket's 14:00).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point>> Layout(IReadOnlyList<HistoryPoint> points, double width, double height, long topMb, TimeSpan bucket)
    {
        var runs = new List<IReadOnlyList<Point>>();
        if (points.Count == 0 || topMb <= 0) return runs;
        var from = points[0].Start;
        var span = (points[^1].Start + bucket - from).Ticks;
        double X(DateTime at) => span <= 0 ? 0 : width * (at - from).Ticks / span;
        List<Point>? run = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].LowestMb is not { } mb)
            {
                run = null;
                continue;
            }
            if (run is null) runs.Add(run = []);
            run.Add(new Point(X(points[i].At), height - height * mb / topMb));
        }
        return runs;
    }

    /// <summary>The runs laid out inside <paramref name="plot"/>, each point at its own time between <paramref name="from"/> and <paramref name="to"/>.</summary>
    public static IReadOnlyList<IReadOnlyList<Point>> Layout(IReadOnlyList<HistoryPoint> points, Rect plot, long topMb, DateTime from, DateTime to)
    {
        var runs = new List<IReadOnlyList<Point>>();
        if (points.Count == 0 || topMb <= 0) return runs;
        var span = (to - from).Ticks;
        double X(DateTime at) => plot.Left + (span <= 0 ? 0 : plot.Width * Math.Clamp((double)(at - from).Ticks / span, 0, 1));
        List<Point>? run = null;
        foreach (var point in points)
        {
            if (point.LowestMb is not { } mb)
            {
                run = null;
                continue;
            }
            if (run is null) runs.Add(run = []);
            run.Add(new Point(X(point.At), plot.Top + plot.Height - plot.Height * mb / topMb));
        }
        return runs;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points ?? [];
        var plotHeight = Math.Max(0, ActualHeight - Inset);
        var top = Math.Max(FloorMb, points.Where(p => p.LowestMb is not null).Select(p => p.LowestMb!.Value).DefaultIfEmpty(0).Max());
        TopMb = top + top / 10;
        var from = From ?? (points.Count > 0 ? points[0].Start : DateTime.UtcNow);
        var to = To ?? (points.Count > 0 ? points[^1].Start + Bucket : DateTime.UtcNow);

        // The value labels sit in a gutter of their own, left of the plot: on the plot they lay over the line (D02 T04 §5).
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = TryFindResource("T.Caption") is double caption ? caption : 12;
        var face = new Typeface(TryFindResource("F.Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var ink = Label ?? TryFindResource("B.TextDim") as Brush ?? new SolidColorBrush();
        FormattedText Text(string text) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, ink, dpi);
        var topText = Text(MemoryPage.GbText(TopMb / 1024.0) + " GB");
        var floorText = Text(MemoryPage.GbText(FloorMb / 1024.0) + " GB");
        var gutter = Math.Ceiling(Math.Max(topText.Width, floorText.Width)) + 8;
        Plot = new Rect(gutter, 0, Math.Max(0, ActualWidth - gutter), plotHeight);
        Runs = Layout(points, Plot, TopMb, from, to);
        FloorY = TopMb > 0 ? plotHeight - plotHeight * FloorMb / TopMb : double.NaN;

        if (Line is { } line)
        {
            var pen = new Pen(line, 1.7) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            foreach (var run in Runs)
            {
                if (run.Count == 1)
                {
                    dc.DrawEllipse(line, null, run[0], 1.7, 1.7);
                    continue;
                }
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(run[0], false, false);
                    g.PolyLineTo(run.Skip(1).ToList(), true, true);
                }
                geometry.Freeze();
                dc.DrawGeometry(null, pen, geometry);
            }
        }
        if (Pressure is { } pressure && !double.IsNaN(FloorY))
        {
            var dashed = new Pen(pressure, 1) { DashStyle = new DashStyle([4, 3], 0) };
            dashed.Freeze();
            dc.DrawLine(dashed, new Point(Plot.Left, FloorY), new Point(Plot.Right, FloorY));
        }

        var labels = new List<(string, Rect)>();
        void Put(string text, FormattedText formatted, Point at)
        {
            if (Label is not null) dc.DrawText(formatted, at);
            labels.Add((text, new Rect(at, new Size(formatted.Width, formatted.Height))));
        }
        // The top value right-aligned in the gutter at the top; the line's value beside where the line is, kept clear of it.
        Put(topText.Text, topText, new Point(gutter - 8 - topText.Width, 0));
        if (!double.IsNaN(FloorY))
        {
            var y = Math.Clamp(FloorY - floorText.Height / 2, topText.Height + 2, Math.Max(topText.Height + 2, plotHeight - floorText.Height));
            Put(floorText.Text, floorText, new Point(gutter - 8 - floorText.Width, y));
        }

        // The axis: its start with its day, its end "Now" or a kept reading's time, and the ticks between that fit. With no
        // span and nothing read there is no axis: two made-up times would say a period was shown (completion review).
        if (From is null && To is null && points.Count == 0)
        {
            Labels = labels;
            Ticks = [];
            return;
        }
        var dayRange = RangeName == HistoryRange.Day.Name;
        var startText = Text(from.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture));
        var endText = Text(EndText ?? to.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture));
        var axisY = plotHeight + 2;
        Put(startText.Text, startText, new Point(Plot.Left, axisY));
        Put(endText.Text, endText, new Point(Math.Max(Plot.Left, ActualWidth - endText.Width), axisY));
        var ticks = new List<(DateTime, double, string?)>();
        var tickPen = new Pen(ink, 1);
        tickPen.Freeze();
        var span = (to - from).Ticks;
        foreach (var at in TickTimes(from, to, RangeName))
        {
            var x = Plot.Left + (span <= 0 ? 0 : Plot.Width * (double)(at - from).Ticks / span);
            if (Label is not null) dc.DrawLine(tickPen, new Point(x, plotHeight - 3), new Point(x, plotHeight));
            var label = Text(at.ToLocalTime().ToString(dayRange ? "HH:mm" : "d MMM", CultureInfo.InvariantCulture));
            var box = new Rect(new Point(x - label.Width / 2, axisY), new Size(label.Width, label.Height));
            var room = new Rect(box.X - 8, box.Y, box.Width + 16, box.Height);
            var fits = box.Left >= Plot.Left && box.Right <= ActualWidth && !labels.Any(l => l.Item2.IntersectsWith(room));
            if (fits) Put(label.Text, label, box.TopLeft);
            ticks.Add((at, x, fits ? label.Text : null));
        }
        Labels = labels;
        Ticks = ticks;
    }

    private static void OnSaid(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement((SeriesChart)d) is { } peer) peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, "", ((SeriesChart)d).SpokenName);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(SeriesChart owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(SeriesChart);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
