using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The radial gauge (D02 T03 §5, design/SPEC.md §19 Radial gauge): its arc is a value over its total, an unreadable
/// value or total is "Not available" on the empty track and never 0, the arc and word are the state's colour (Windows'
/// own in a contrast theme), a screen reader hears the value and word, and a change sweeps over the slow duration
/// through the motion gate, holding no clock with motion off, hidden or unloaded.
/// </summary>
public class GaugeTests
{
    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    static Color Paint(object? brush) => ((SolidColorBrush)brush!).Color;

    static Color Brush(FrameworkElement at, string key) => ((SolidColorBrush)at.FindResource(key)).Color;

    static GaugeArc ArcOf(RadialGauge gauge) => ShellSupport.Under<GaugeArc>(gauge).Single();

    static TextBlock Part(RadialGauge gauge, string name) => (TextBlock)gauge.Template.FindName(name, gauge);

    static string Said(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

    [Theory]
    [InlineData(0.0, 135.0)]
    [InlineData(16.0, 270.0)]
    [InlineData(32.0, 405.0)]
    [InlineData(40.0, 405.0)]
    public void The_arc_is_the_value_over_its_total(double free, double angle)
    {
        // 7.4 GB of 32 GB is a real quantity; 0, half and all of it are the ends and the top; past the total fills it.
        PageRenderer.InPage("Dark", () => new RadialGauge { Value = free, Maximum = 32, Unit = "GB free", Word = "Safe" }, page =>
        {
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            Assert.Equal(angle, GaugeArc.AngleOf(gauge.Fraction!.Value), 6);
            Assert.Equal(gauge.Fraction!.Value, ArcOf(gauge).Sweep, 6);
            gauge.Value = 7.4;
            PageRenderer.Settle(page);
            Assert.Equal(7.4 / 32, gauge.Fraction!.Value, 6);
            Assert.Equal(135 + 270 * 7.4 / 32, GaugeArc.AngleOf(ArcOf(gauge).Sweep), 6);
            return 0;
        });
    }

    public static TheoryData<double?, double?> Unreadable() => new()
    {
        { null, 32 }, { double.NaN, 32 }, { double.PositiveInfinity, 32 }, { -1, 32 },
        { 7.4, null }, { 7.4, 0 }, { 7.4, -8 }, { 7.4, double.NaN },
    };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void An_unreadable_value_or_total_is_Not_available_on_the_empty_track_never_0(double? value, double? maximum)
    {
        PageRenderer.InPage("Dark", () => new RadialGauge { Value = value, Maximum = maximum, Unit = "GB free", Word = "Safe" }, page =>
        {
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            var texts = PageRenderer.TextsOf(page).Where(t => !string.IsNullOrEmpty(t)).ToList();
            Assert.Contains(RadialGauge.NotAvailable, texts);
            Assert.DoesNotContain(texts, t => t.Contains('0'));          // no 0, and no word or unit with it
            Assert.Equal((double?)null, gauge.Fraction);
            Assert.Equal((double?)null, ArcOf(gauge).Target);
            Assert.Equal(0, ArcOf(gauge).Sweep);
            Assert.Equal(RadialGauge.NotAvailable, Said(gauge));
            gauge.Value = 7.4;                                            // and a readable one comes back
            gauge.Maximum = 32;
            PageRenderer.Settle(page);
            Assert.Equal("7.4 GB free, Safe", Said(gauge));
            return 0;
        });
    }

    public static TheoryData<string, StatusKind> States()
    {
        var data = new TheoryData<string, StatusKind>();
        foreach (var theme in new[] { "Dark", "Light" })
            foreach (var state in Enum.GetValues<StatusKind>())
                data.Add(theme, state);
        return data;
    }

    [Theory]
    [MemberData(nameof(States))]
    public void The_arc_and_word_are_the_state_colour(string theme, StatusKind state)
    {
        PageRenderer.InPage(theme, () => new RadialGauge { Value = 7.4, Maximum = 32, Unit = "GB free", Word = "Safe", State = state }, page =>
        {
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            var role = state switch
            {
                StatusKind.Ok => "B.Ok", StatusKind.Warn => "B.Warn", StatusKind.Danger => "B.Danger", StatusKind.Info => "B.Info", _ => "B.Accent",
            };
            Assert.Equal(Brush(page, role), Paint(ArcOf(gauge).Fill));
            Assert.Equal(Brush(page, role), Paint(Part(gauge, "word").Foreground));
            Assert.Equal(Brush(page, "B.Hover"), Paint(ArcOf(gauge).Track));
            Assert.Equal(Brush(page, "B.Text"), Paint(Part(gauge, "number").Foreground));
            return 0;
        });
    }

    [Theory]
    [InlineData("NightSky")]
    [InlineData("Desert")]
    public void In_a_contrast_theme_it_draws_in_Windows_own_colours(string name)
    {
        var palette = Helpers.ContrastPalettes.Both.Single(p => p.Name == name).Palette;
        PageRenderer.InPage("HighContrast", () => new RadialGauge { Value = 7.4, Maximum = 32, Unit = "GB free", Word = "Tight", State = StatusKind.Warn }, page =>
        {
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            Assert.Equal(palette.Highlight, Paint(ArcOf(gauge).Fill));     // the arc the highlight
            Assert.Equal(palette.GrayText, Paint(ArcOf(gauge).Track));     // the track seen against the window
            Assert.Equal(palette.WindowText, Paint(Part(gauge, "word").Foreground));
            Assert.Equal(palette.WindowText, Paint(Part(gauge, "unit").Foreground));
            Assert.Equal(palette.WindowText, Paint(Part(gauge, "number").Foreground));
            return 0;
        }, palette: palette);
    }

    [Fact]
    public void A_screen_reader_hears_the_value_unit_and_word_and_follows_a_change()
    {
        PageRenderer.InPage("Dark", () => new RadialGauge { Value = 7.4, Maximum = 32, Unit = "GB free", Word = "Safe", State = StatusKind.Ok }, page =>
        {
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            var peer = UIElementAutomationPeer.CreatePeerForElement(gauge);
            Assert.Equal(("7.4 GB free, Safe", AutomationControlType.Text), (peer.GetName(), peer.GetAutomationControlType()));
            Assert.Null(peer.GetChildren());                              // nothing inside is said again
            Assert.False(gauge.Focusable);
            gauge.Value = 2.1;
            gauge.Word = "Tight";
            PageRenderer.Settle(page);
            Assert.Equal("2.1 GB free, Tight", peer.GetName());
            Assert.Equal(("2.1", "GB free", "Tight"), (Part(gauge, "number").Text, Part(gauge, "unit").Text, Part(gauge, "word").Text));
            return 0;
        });
    }

    /// <summary>A gauge in a page with its own motion gate, as the application installs one.</summary>
    static void WithGate(bool animates, Action<Border, RadialGauge, FakeAnimation, Motion> test)
    {
        PageRenderer.InPage("Dark", () => new RadialGauge { Value = 8, Maximum = 32, Unit = "GB free", Word = "Safe" }, page =>
        {
            var windows = new FakeAnimation(animates);
            var gate = new Motion(page.Resources, windows);
            page.Resources[Motion.GateKey] = gate;
            var gauge = ShellSupport.Under<RadialGauge>(page).Single();
            PageRenderer.Settle(page);
            test(page, gauge, windows, gate);
            return 0;
        });
    }

    [Fact]
    public void A_change_sweeps_part_way_then_lands_over_the_slow_duration()
    {
        WithGate(true, (page, gauge, _, gate) =>
        {
            var arc = ArcOf(gauge);
            Assert.Equal(0.25, arc.Sweep, 6);
            gauge.Value = 24;
            Pump(TimeSpan.FromMilliseconds(gate.Slow.TimeSpan.TotalMilliseconds / 3));
            Assert.True(arc.Moving && arc.HasAnimatedProperties, "a sweep is under way");
            Assert.InRange(arc.Sweep, 0.2501, 0.7499);
            Assert.Equal("24 GB free, Safe", Said(gauge));               // the words change at once
            Pump(gate.Slow.TimeSpan + TimeSpan.FromMilliseconds(250));
            Assert.Equal(0.75, arc.Sweep, 6);
            Assert.False(arc.Moving || arc.HasAnimatedProperties, "the clock is let go once it lands");
        });
    }

    [Fact]
    public void With_motion_off_a_change_lands_at_once_with_no_clock()
    {
        WithGate(false, (_, gauge, _, _) =>
        {
            gauge.Value = 24;
            var arc = ArcOf(gauge);
            Assert.Equal(0.75, arc.Sweep, 6);
            Assert.False(arc.Moving || arc.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Motion_turned_off_mid_change_lands_it_at_once()
    {
        WithGate(true, (_, gauge, windows, gate) =>
        {
            gauge.Value = 24;
            Pump(TimeSpan.FromMilliseconds(gate.Slow.TimeSpan.TotalMilliseconds / 4));
            windows.Set(false);
            var arc = ArcOf(gauge);
            Assert.Equal(0.75, arc.Sweep, 6);
            Assert.False(arc.Moving || arc.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Hidden_or_unloaded_mid_change_it_lands_and_lets_the_gate_go()
    {
        WithGate(true, (page, gauge, _, gate) =>
        {
            var arc = ArcOf(gauge);
            gauge.Value = 24;
            Pump(TimeSpan.FromMilliseconds(gate.Slow.TimeSpan.TotalMilliseconds / 4));
            gauge.Visibility = Visibility.Collapsed;
            Assert.Equal(0.75, arc.Sweep, 6);
            Assert.False(arc.Moving || arc.HasAnimatedProperties);
            gauge.Visibility = Visibility.Visible;
            PageRenderer.Settle(page);
            gauge.Value = 4;
            Assert.True(arc.Moving, "shown again, it sweeps");
            page.Child = null;
            PageRenderer.Settle(page);
            Assert.False(arc.Moving || arc.HasAnimatedProperties);
            int Listeners() => (typeof(Motion).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(gate) as Delegate)?
                .GetInvocationList().Length ?? 0;
            Assert.Equal(0, Listeners());
        });
    }
}
