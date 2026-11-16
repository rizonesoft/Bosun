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

    public static readonly DependencyProperty RangeNameProperty = DependencyProperty.Register(
        nameof(RangeName), typeof(string), typeof(SeriesChart), new FrameworkPropertyMetadata("24 hours", OnSaid));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(Brush), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PressureProperty = DependencyProperty.Register(
        nameof(Pressure), typeof(Brush), typeof(SeriesChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public Brush? Line { get => (Brush?)GetValue(LineProperty); set => SetValue(LineProperty, value); }

    public Brush? Pressure { get => (Brush?)GetValue(PressureProperty); set => SetValue(PressureProperty, value); }

    public Brush? Label { get => (Brush?)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

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

    /// <summary>The runs of measured points laid out in a box, the line breaking at every missing bucket.</summary>
    public static IReadOnlyList<IReadOnlyList<Point>> Layout(IReadOnlyList<HistoryPoint> points, double width, double height, long topMb)
    {
        var runs = new List<IReadOnlyList<Point>>();
        if (points.Count == 0 || topMb <= 0) return runs;
        var step = points.Count > 1 ? width / (points.Count - 1) : 0;
        List<Point>? run = null;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i].LowestMb is not { } mb)
            {
                run = null;
                continue;
            }
            if (run is null) runs.Add(run = []);
            run.Add(new Point(i * step, height - height * mb / topMb));
        }
        return runs;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points ?? [];
        var plotHeight = Math.Max(0, ActualHeight - Inset);
        var top = Math.Max(FloorMb, points.Where(p => p.LowestMb is not null).Select(p => p.LowestMb!.Value).DefaultIfEmpty(0).Max());
        TopMb = top + top / 10;
        Runs = Layout(points, ActualWidth, plotHeight, TopMb);
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
        FloorY = TopMb > 0 ? plotHeight - plotHeight * FloorMb / TopMb : double.NaN;
        if (Pressure is { } pressure && !double.IsNaN(FloorY))
        {
            var dashed = new Pen(pressure, 1) { DashStyle = new DashStyle([4, 3], 0) };
            dashed.Freeze();
            dc.DrawLine(dashed, new Point(0, FloorY), new Point(ActualWidth, FloorY));
        }
        if (Label is { } label)
        {
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var size = TryFindResource("T.Caption") is double caption ? caption : 12;
            var face = new Typeface(TryFindResource("F.Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            FormattedText Text(string s) => new(s, CultureInfo.CurrentUICulture, FlowDirection, face, size, label, dpi);
            if (!double.IsNaN(FloorY))
            {
                var floor = Text($"{(FloorMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} GB pressure line");
                dc.DrawText(floor, new Point(Math.Max(0, ActualWidth - floor.Width), Math.Max(0, FloorY - floor.Height - 2)));
            }
            dc.DrawText(Text((TopMb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB"), new Point(0, 0));
            if (points.Count > 0)
            {
                string When(DateTime at) => at.ToLocalTime().ToString(RangeName == "24 hours" ? "HH:mm" : "d MMM", CultureInfo.InvariantCulture);
                dc.DrawText(Text(When(points[0].Start)), new Point(0, plotHeight + 2));
                var end = Text(When(points[^1].Start));
                dc.DrawText(end, new Point(Math.Max(0, ActualWidth - end.Width), plotHeight + 2));
            }
        }
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
