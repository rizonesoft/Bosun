using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Controls;

/// <summary>
/// How full something is, at a glance (D02 T03 §5, design/SPEC.md §19 Radial gauge): a 270 degree arc of the value over
/// its total, with the value, its unit and a state word in the centre. An unreadable value, or one with no total, draws
/// the empty track and "Not available", never 0: a negative or non-finite value, and a missing, zero or non-finite
/// total, are refused rather than clamped. A value past its total fills the arc. A change of value sweeps the arc over the
/// motion gate's slow duration (at once with animation effects off). A screen reader hears the value and the word.
/// </summary>
public class RadialGauge : Control
{
    public const string NotAvailable = "Not available";

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(RadialGauge), new FrameworkPropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double?), typeof(RadialGauge), new FrameworkPropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata("", OnInputChanged));

    public static readonly DependencyProperty WordProperty = DependencyProperty.Register(
        nameof(Word), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata("", OnInputChanged));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(StatusKind), typeof(RadialGauge), new FrameworkPropertyMetadata(StatusKind.Neutral));

    private static readonly DependencyPropertyKey FractionKey = DependencyProperty.RegisterReadOnly(
        nameof(Fraction), typeof(double?), typeof(RadialGauge), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty FractionProperty = FractionKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShownValueKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownValue), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata(NotAvailable));

    public static readonly DependencyProperty ShownValueProperty = ShownValueKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShownWordKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownWord), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty ShownWordProperty = ShownWordKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShownNumberKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownNumber), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata(NotAvailable));

    public static readonly DependencyProperty ShownNumberProperty = ShownNumberKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShownUnitKey = DependencyProperty.RegisterReadOnly(
        nameof(ShownUnit), typeof(string), typeof(RadialGauge), new FrameworkPropertyMetadata(""));

    public static readonly DependencyProperty ShownUnitProperty = ShownUnitKey.DependencyProperty;

    /// <summary>The number as drawn large in the centre ("7.4"), or "Not available".</summary>
    public string ShownNumber => (string)GetValue(ShownNumberProperty);

    /// <summary>The unit as drawn under it, empty while there is no value.</summary>
    public string ShownUnit => (string)GetValue(ShownUnitProperty);

    static RadialGauge() => FocusableProperty.OverrideMetadata(typeof(RadialGauge), new FrameworkPropertyMetadata(false));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>The total the value is out of: the arc is value over total.</summary>
    public double? Maximum { get => (double?)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    /// <summary>The value's unit, as it is said after it ("GB free").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>The state in a word ("Safe", "Tight"), required with a value.</summary>
    public string Word { get => (string)GetValue(WordProperty); set => SetValue(WordProperty, value); }

    /// <summary>The state's colour for the arc and the word.</summary>
    public StatusKind State { get => (StatusKind)GetValue(StateProperty); set => SetValue(StateProperty, value); }

    /// <summary>Value over total, from 0 to 1; null when there is no readable value or total.</summary>
    public double? Fraction => (double?)GetValue(FractionProperty);

    /// <summary>The value and its unit as drawn ("7.4 GB free"), or "Not available".</summary>
    public string ShownValue => (string)GetValue(ShownValueProperty);

    /// <summary>The word as drawn, empty while there is no value.</summary>
    public string ShownWord => (string)GetValue(ShownWordProperty);

    /// <summary>The fraction of a value over its total, or null when either can't be read: never clamped up from below 0.</summary>
    public static double? FractionOf(double? value, double? maximum) =>
        value is { } v && double.IsFinite(v) && v >= 0 && maximum is { } m && double.IsFinite(m) && m > 0 ? Math.Min(v / m, 1) : null;

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RadialGauge)d;
        var fraction = FractionOf(gauge.Value, gauge.Maximum);
        var wasName = gauge.SpokenName;
        var number = fraction is null ? NotAvailable : gauge.Value!.Value.ToString("0.##", CultureInfo.InvariantCulture);
        var unit = fraction is null ? "" : gauge.Unit?.Trim() ?? "";
        gauge.SetValue(FractionKey, fraction);
        gauge.SetValue(ShownNumberKey, number);
        gauge.SetValue(ShownUnitKey, unit);
        gauge.SetValue(ShownValueKey, unit.Length == 0 ? number : $"{number} {unit}");
        gauge.SetValue(ShownWordKey, fraction is null ? "" : gauge.Word?.Trim() ?? "");
        if (wasName != gauge.SpokenName && UIElementAutomationPeer.FromElement(gauge) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, wasName, gauge.SpokenName);
    }

    /// <summary>What a screen reader hears: "7.4 GB free, Safe", or "Not available".</summary>
    public string SpokenName => string.IsNullOrEmpty(ShownWord) ? ShownValue : $"{ShownValue}, {ShownWord}";

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(RadialGauge owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(RadialGauge);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// The gauge's arc (D02 T03 §5): its track, and the sweep of its fraction over 270 degrees, drawn once per change.
/// <see cref="Target"/> is where the sweep is going; <see cref="Sweep"/> moves to it over the motion gate's slow duration
/// with its easing, at once with animation effects off, off mid-change, or when the gauge is hidden or unloaded.
/// </summary>
public class GaugeArc : FrameworkElement
{
    /// <summary>The arc runs from 135 degrees (bottom left) through the top to 405 (bottom right): 270 degrees.</summary>
    public const double Start = 135, Span = 270;

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(double?), typeof(GaugeArc), new FrameworkPropertyMetadata(null, OnTargetChanged));

    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
        nameof(Sweep), typeof(double), typeof(GaugeArc), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(GaugeArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(GaugeArc), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(GaugeArc), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private Motion? _gate;

    public GaugeArc()
    {
        Loaded += (_, _) => Gate();
        Unloaded += (_, _) =>
        {
            Land();
            if (_gate is not null) _gate.Changed -= Land;
            _gate = null;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) Land();
        };
    }

    /// <summary>The fraction the arc shows, or null for an empty track.</summary>
    public double? Target { get => (double?)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    /// <summary>The fraction drawn now, moving to <see cref="Target"/> during a change.</summary>
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }

    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>Whether a change is under way.</summary>
    public bool Moving { get; private set; }

    /// <summary>The angle, in degrees, the arc ends at for a fraction: 135 at 0, 270 at a half, 405 at 1.</summary>
    public static double AngleOf(double fraction) => Start + Span * Math.Clamp(fraction, 0, 1);

    /// <summary>The installed motion gate, found once the arc is loaded and listened to until it is unloaded.</summary>
    private Motion? Gate()
    {
        if (_gate is null && IsLoaded && TryFindResource(Motion.GateKey) is Motion gate)
        {
            _gate = gate;
            _gate.Changed += Land;
        }
        return _gate;
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((GaugeArc)d).Move();

    private void Move()
    {
        var to = Target ?? 0;
        var gate = Gate();
        if (Target is null || gate is not { Animates: true } || gate.Slow.TimeSpan <= TimeSpan.Zero || !IsVisible || !IsLoaded)
        {
            Land();
            return;
        }
        var sweep = new DoubleAnimationUsingKeyFrames { Duration = gate.Slow, FillBehavior = FillBehavior.HoldEnd };
        sweep.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(gate.Slow.TimeSpan), gate.Ease));
        sweep.Completed += (_, _) =>
        {
            if (Moving && Target is { } t && Math.Abs(t - to) < 1e-9) Land();
        };
        Moving = true;
        BeginAnimation(SweepProperty, sweep, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>Ends a change where it was going, now: the clock is let go.</summary>
    private void Land()
    {
        BeginAnimation(SweepProperty, null);
        Sweep = Target ?? 0;
        Moving = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness * 2) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 - Thickness / 2;
        if (Track is { } track) dc.DrawGeometry(null, Pen(track), Arc(centre, radius, Start, Start + Span));
        if (Target is not null && Sweep > 0 && Fill is { } fill) dc.DrawGeometry(null, Pen(fill), Arc(centre, radius, Start, AngleOf(Sweep)));
    }

    private Pen Pen(Brush brush)
    {
        var pen = new Pen(brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }

    private static StreamGeometry Arc(Point centre, double radius, double from, double to)
    {
        Point At(double degrees)
        {
            var r = degrees * Math.PI / 180;
            return new Point(centre.X + radius * Math.Cos(r), centre.Y + radius * Math.Sin(r));
        }
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(from), false, false);
            g.ArcTo(At(to), new Size(radius, radius), 0, to - from > 180, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        return arc;
    }
}
