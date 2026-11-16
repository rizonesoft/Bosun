using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Bosun.Panel.Controls;

/// <summary>One share of a stacked bar: what it is, how much, and whether it is the rest of the total (such as free memory).</summary>
public sealed record BarPart(string Name, double Value, bool Rest = false);

/// <summary>One line of a stacked bar's legend: its words, and the colour role its swatch shares with its segment.</summary>
public sealed record LegendLine(string Text, string Role);

/// <summary>One segment of a stacked bar: its value and its colour role.</summary>
public sealed record BarSegment(double Value, string Role);

/// <summary>
/// Shares of a total in one bar, such as memory by group (D02 T03 §5, design/SPEC.md §19 Stacked bar): a segment per
/// share at its share of the bar in a colour that carries no state (the rest of the total in a quieter one), a 2 px gap between two segments so they stay apart where a contrast theme makes their
/// colours one, and a legend under it giving every share in words beside its swatch, so colour is never the only key.
/// A zero share draws no segment and keeps its legend line; all-zero shares draw the empty track with every line at 0.
/// No shares at all, or a negative or non-finite one, read "Not available": the empty track and no legend, never a
/// previous bar. A screen reader hears the legend as one sentence; nothing inside is said again.
/// </summary>
public class StackedBar : Control
{
    public const string NotAvailable = "Not available";

    /// <summary>
    /// The segments' colour roles, in order: a share takes the next one. None is a state colour, since a share of memory
    /// is not good or bad news, as the mockup draws it (Windows in the info blue, WSL in the accent).
    /// </summary>
    public static IReadOnlyList<string> Roles { get; } = ["B.Info", "B.Accent", "B.TextMute", "B.TextDim"];

    /// <summary>The rest of the total, such as free memory: the strong line colour, quieter than any share.</summary>
    public const string RestRole = "B.LineStrong";

    /// <summary>The role each share draws in: the rest its own, the others the next of <see cref="Roles"/>.</summary>
    public static IReadOnlyList<string> RolesOf(IReadOnlyList<BarPart> parts)
    {
        var next = 0;
        return parts.Select(p => p.Rest ? RestRole : Roles[next++ % Roles.Count]).ToList();
    }

    public static readonly DependencyProperty PartsProperty = DependencyProperty.Register(
        nameof(Parts), typeof(IReadOnlyList<BarPart>), typeof(StackedBar), new FrameworkPropertyMetadata(null, OnInputChanged));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(StackedBar), new FrameworkPropertyMetadata("", OnInputChanged));

    private static readonly DependencyPropertyKey LegendKey = DependencyProperty.RegisterReadOnly(
        nameof(Legend), typeof(IReadOnlyList<LegendLine>), typeof(StackedBar), new FrameworkPropertyMetadata(Array.Empty<LegendLine>()));

    public static readonly DependencyProperty LegendProperty = LegendKey.DependencyProperty;

    private static readonly DependencyPropertyKey ShownKey = DependencyProperty.RegisterReadOnly(
        nameof(Shown), typeof(IReadOnlyList<BarSegment>), typeof(StackedBar), new FrameworkPropertyMetadata(Array.Empty<BarSegment>()));

    public static readonly DependencyProperty ShownProperty = ShownKey.DependencyProperty;

    private static readonly DependencyPropertyKey AvailableKey = DependencyProperty.RegisterReadOnly(
        nameof(Available), typeof(bool), typeof(StackedBar), new FrameworkPropertyMetadata(false));

    public static readonly DependencyProperty AvailableProperty = AvailableKey.DependencyProperty;

    static StackedBar() => FocusableProperty.OverrideMetadata(typeof(StackedBar), new FrameworkPropertyMetadata(false));

    /// <summary>The shares, in the order they are drawn from the left.</summary>
    public IReadOnlyList<BarPart>? Parts { get => (IReadOnlyList<BarPart>?)GetValue(PartsProperty); set => SetValue(PartsProperty, value); }

    /// <summary>The shares' unit, said after each value ("GB").</summary>
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>The legend's lines, one per share; empty while the shares can't be read.</summary>
    public IReadOnlyList<LegendLine> Legend => (IReadOnlyList<LegendLine>)GetValue(LegendProperty);

    /// <summary>The segments the bar draws; empty while the shares can't be read.</summary>
    public IReadOnlyList<BarSegment> Shown => (IReadOnlyList<BarSegment>)GetValue(ShownProperty);

    /// <summary>Whether the shares can be read.</summary>
    public bool Available => (bool)GetValue(AvailableProperty);

    /// <summary>What a screen reader hears: every share in words, or "Not available".</summary>
    public string SpokenName => Available ? string.Join(", ", Legend.Select(l => l.Text)) : NotAvailable;

    /// <summary>Whether shares can be drawn and said: at least one, each named, finite and not negative.</summary>
    public static bool Readable(IReadOnlyList<BarPart>? parts) =>
        parts is { Count: > 0 } && parts.All(p => p is not null && double.IsFinite(p.Value) && p.Value >= 0);

    /// <summary>
    /// Where each share's segment sits in a bar of a width: its share of the width less the gaps, a gap after each drawn
    /// segment but the last; a zero share, or every share when they are all zero, has no width.
    /// </summary>
    public static IReadOnlyList<(double X, double Width)> Layout(IReadOnlyList<double> values, double width, double gap)
    {
        var total = values.Sum();
        var drawn = values.Count(v => v > 0);
        if (total <= 0 || drawn == 0) return values.Select(_ => (0.0, 0.0)).ToList();
        var room = Math.Max(0, width - gap * (drawn - 1));
        var placed = new List<(double, double)>(values.Count);
        var x = 0.0;
        foreach (var v in values)
        {
            if (v <= 0)
            {
                placed.Add((x, 0));
                continue;
            }
            var w = room * v / total;
            placed.Add((x, w));
            x += w + gap;
        }
        return placed;
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (StackedBar)d;
        var was = bar.SpokenName;
        var parts = bar.Parts;
        var readable = Readable(parts);
        var unit = string.IsNullOrWhiteSpace(bar.Unit) ? "" : " " + bar.Unit.Trim();
        var roles = readable ? RolesOf(parts!) : [];
        bar.SetValue(AvailableKey, readable);
        bar.SetValue(ShownKey, readable ? parts!.Select((p, i) => new BarSegment(p.Value, roles[i])).ToList() : Array.Empty<BarSegment>());
        bar.SetValue(LegendKey, readable
            ? parts!.Select((p, i) => new LegendLine($"{p.Name.Trim()} {p.Value.ToString("0.0", CultureInfo.InvariantCulture)}{unit}", roles[i])).ToList()
            : Array.Empty<LegendLine>());
        if (was != bar.SpokenName && UIElementAutomationPeer.FromElement(bar) is { } peer)
            peer.RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, was, bar.SpokenName);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Said(this);

    private sealed class Said(StackedBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.SpokenName;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(StackedBar);

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}

/// <summary>
/// The stacked bar's bar (D02 T03 §5): its track, and a segment per share in its role's colour, drawn once per change of
/// values, size or theme.
/// </summary>
public class BarTrack : FrameworkElement
{
    /// <summary>The gap between two segments.</summary>
    public const double Gap = 2;

    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<BarSegment>), typeof(BarTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(BarTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>A brush per role, each a reference to its theme token, so a theme change redraws the bar.</summary>
    private static readonly Dictionary<string, DependencyProperty> RoleProperties = StackedBar.Roles.Append(StackedBar.RestRole).ToDictionary(r => r, r =>
        DependencyProperty.Register("Role" + r.Replace(".", ""), typeof(Brush), typeof(BarTrack), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender)));

    static BarTrack() => HeightProperty.OverrideMetadata(typeof(BarTrack), new FrameworkPropertyMetadata(12.0));

    public BarTrack()
    {
        foreach (var (role, property) in RoleProperties) SetResourceReference(property, role);
    }

    public IReadOnlyList<BarSegment>? Values { get => (IReadOnlyList<BarSegment>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>The empty bar under the segments.</summary>
    public Brush? Track { get => (Brush?)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    /// <summary>The segments last drawn, one per value, a zero one with no width.</summary>
    public IReadOnlyList<(double X, double Width)> Segments { get; private set; } = [];

    /// <summary>The brush a role draws in now.</summary>
    public Brush? RoleBrush(string role) => (Brush?)GetValue(RoleProperties[role]);

    protected override void OnRender(DrawingContext dc)
    {
        var values = Values ?? [];
        Segments = StackedBar.Layout(values.Select(v => v.Value).ToList(), ActualWidth, Gap);
        var radius = Math.Min(3, ActualHeight / 2);
        if (values.Sum(v => v.Value) <= 0)
        {
            if (Track is { } track) dc.DrawRoundedRectangle(track, null, new Rect(0, 0, ActualWidth, ActualHeight), ActualHeight / 2, ActualHeight / 2);
            return;
        }
        for (var i = 0; i < Segments.Count; i++)
        {
            var (x, w) = Segments[i];
            if (w <= 0) continue;
            dc.DrawRoundedRectangle(RoleBrush(values[i].Role), null, new Rect(x, 0, w, ActualHeight), Math.Min(radius, w / 2), radius);
        }
    }
}

/// <summary>A legend line's swatch (D02 T03 §5): a 9 px square in its role's colour, the same as its segment's.</summary>
public class Swatch : FrameworkElement
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.Register(
        nameof(Role), typeof(string), typeof(Swatch), new FrameworkPropertyMetadata(StackedBar.Roles[0], OnRoleChanged));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Swatch), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    static Swatch()
    {
        WidthProperty.OverrideMetadata(typeof(Swatch), new FrameworkPropertyMetadata(9.0));
        HeightProperty.OverrideMetadata(typeof(Swatch), new FrameworkPropertyMetadata(9.0));
    }

    public Swatch() => SetResourceReference(FillProperty, StackedBar.Roles[0]);

    /// <summary>The theme token its colour comes from.</summary>
    public string Role { get => (string)GetValue(RoleProperty); set => SetValue(RoleProperty, value); }

    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    private static void OnRoleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Swatch)d).SetResourceReference(FillProperty, e.NewValue as string ?? StackedBar.Roles[0]);

    protected override void OnRender(DrawingContext dc) => dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
}
