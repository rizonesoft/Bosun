using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;

namespace Bosun.Panel.Controls;

/// <summary>
/// A recent trend inside a card (D02 T03 §5, design/SPEC.md §19 Sparkline): no axis, the samples oldest at the left,
/// scaled from the lowest at the bottom to the highest at the top, a flat line across the middle when they are all one
/// value. It is drawn from the samples it holds whenever WPF draws it, so a resize or a theme change redraws it with
/// no new samples and nothing is drawn on a timer. A screen reader hears the latest value and its direction in words
/// ("12.4 GB, rising"); with one sample the value alone, and with none, or with a negative or non-finite one, "Not
/// available" written in place of the line, never the samples it had before.
/// </summary>
public class Sparkline : FrameworkElement
{
    public const string NotAvailable = "Not available";

    /// <summary>The line's room above and below, as the mockup draws it.</summary>
    public const double Inset = 3;

    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<double>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSaidChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(Sparkline), new FrameworkPropertyMetadata("", OnSaidChanged));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty QuietProperty = DependencyProperty.Register(
        nameof(Quiet), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static Sparkline()
    {
        HeightProperty.OverrideMetadata(typeof(Sparkline), new FrameworkPropertyMetadata(34.0));
        FocusableProperty.OverrideMetadata(typeof(Sparkline), new FrameworkPropertyMetadata(false));
    }

    public Sparkline()
    {
        SetResourceReference(StrokeProperty, "B.Accent");
        SetResourceReference(QuietProperty, "B.TextDim");
    }

    /// <summary>The colour "Not available" is written in: the theme's dim text.</summary>
    public Brush? Quiet { get => (Brush?)GetValue(QuietProperty); set => SetValue(QuietProperty, value); }

    /// <summary>Whether it last drew "Not available" in place of a line.</summary>
    public bool ShowsNotAvailable { get; private set; }

    /// <summary>The samples, oldest first. A new list redraws it; the same list changed in place does not.</summary>
    public IReadOnlyList<double>? Samples { get => (IReadOnlyList<double>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }

    /// <summary>The samples' unit, said after the latest value ("GB").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>The line's colour: the theme's accent, following a theme change.</summary>
    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>How many times it has drawn: a test's proof that nothing redraws while nothing changes.</summary>
    public int Draws { get; private set; }

    /// <summary>The points of the line last drawn, empty when none was.</summary>
    public IReadOnlyList<Point> Drawn { get; private set; } = [];

    /// <summary>What a screen reader hears.</summary>
    public string SpokenName => Say(Samples, Unit);

    /// <summary>Whether samples can be drawn and said: at least one, every one finite and not negative.</summary>
    public static bool Readable(IReadOnlyList<double>? samples) =>
        samples is { Count: > 0 } && samples.All(s => double.IsFinite(s) && s >= 0);

    /// <summary>The line's points for a size: evenly spaced, the lowest sample at the bottom and the highest at the top.</summary>
    public static IReadOnlyList<Point> Plot(IReadOnlyList<double> samples, double width, double height)
    {
        if (!Readable(samples) || samples.Count < 2) return [];
        double low = samples.Min(), high = samples.Max();
        return samples.Select((s, i) => new Point(i * width / (samples.Count - 1),
            high == low ? height / 2 : height - Inset - (s - low) / (high - low) * (height - 2 * Inset))).ToList();
    }

    /// <summary>The latest value and its direction over the samples, in words.</summary>
    public static string Say(IReadOnlyList<double>? samples, string? unit)
    {
        if (!Readable(samples)) return NotAvailable;
        var last = samples![^1];
        var value = last.ToString("0.0", CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(unit) ? "" : " " + unit.Trim());
        if (samples.Count < 2) return value;
        var change = last - samples[0];
        var still = Math.Max(Math.Abs(samples[0]), Math.Abs(last)) * 0.01;
        return $"{value}, {(Math.Abs(change) <= still ? "steady" : change > 0 ? "rising" : "falling")}";
    }

    protected override void OnRender(DrawingContext dc)
    {
        Draws++;
        var points = Samples is { } samples ? Plot(samples, ActualWidth, ActualHeight) : [];
        Drawn = points;
        ShowsNotAvailable = !Readable(Samples);
        if (ShowsNotAvailable)
        {
            if (Quiet is { } quiet)
            {
                var size = TryFindResource("T.Caption") is double caption ? caption : 12;
                var face = new Typeface(TryFindResource("F.Ui") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var text = new FormattedText(NotAvailable, CultureInfo.CurrentUICulture, FlowDirection, face, size, quiet, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(text, new Point(0, Math.Max(0, (ActualHeight - text.Height) / 2)));
            }
            return;
        }
        if (points.Count < 2 || Stroke is not { } stroke) return;
        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(points[0], false, false);
            g.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var g = area.Open())
        {
            g.BeginFigure(new Point(0, ActualHeight), true, true);
            g.PolyLineTo(points.Append(new Point(ActualWidth, ActualHeight)).ToList(), false, true);
        }
        area.Freeze();
        if (stroke is SolidColorBrush solid)
        {
            var wash = new SolidColorBrush(solid.Color) { Opacity = 0.12 };
            wash.Freeze();
            dc.DrawGeometry(wash, null, area);
        }
        var pen = new Pen(stroke, 1.7) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }

    private string _said = NotAvailable;

    private static void OnSaidChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var spark = (Sparkline)d;
        var was = spark._said;
        spark._said = spark.SpokenName;
        if (was != spark._said && UIElementAutomationPeer.FromElement(spark) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, spark._said);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(Sparkline owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(Sparkline);

        protected override bool IsContentElementCore() => true;

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
