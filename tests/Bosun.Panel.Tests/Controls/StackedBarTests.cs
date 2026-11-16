using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The stacked bar (D02 T03 §5, design/SPEC.md §19 Stacked bar): each segment is its share of the bar, a gap keeps two
/// segments apart in every theme, the legend gives every share in words beside its swatch, and unreadable shares are
/// "Not available", never a previous bar.
/// </summary>
public class StackedBarTests
{
    const double Wide = 402;

    static StackedBar Bar(params (string Name, double Value)[] parts) => new()
    {
        Parts = parts.Select(p => new BarPart(p.Name, p.Value)).ToList(), Unit = "GB", Width = Wide, HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    static BarTrack TrackOf(StackedBar bar) => ShellSupport.Under<BarTrack>(bar).Single();

    static string Said(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

    static List<string> Lines(StackedBar bar) => ShellSupport.Under<ItemsControl>(bar).Single().Items.Cast<LegendLine>().Select(l => l.Text).ToList();

    [Fact]
    public void Each_segment_is_its_share_of_the_bar_less_the_gaps()
    {
        PageRenderer.InPage("Dark", () => Bar(("Windows", 3), ("WSL", 1), ("Free", 4)), page =>
        {
            var bar = ShellSupport.Under<StackedBar>(page).Single();
            var segments = TrackOf(bar).Segments;
            var room = Wide - 2 * BarTrack.Gap;
            Assert.Equal([room * 3 / 8, room * 1 / 8, room * 4 / 8], segments.Select(s => s.Width).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
            Assert.Equal(Wide, segments.Sum(s => s.Width) + 2 * BarTrack.Gap, 6);
            Assert.Equal(segments[0].Width + BarTrack.Gap, segments[1].X, 6);
            Assert.Equal(["Windows 3.0 GB", "WSL 1.0 GB", "Free 4.0 GB"], Lines(bar));
            Assert.Equal("Windows 3.0 GB, WSL 1.0 GB, Free 4.0 GB", Said(bar));

            bar.Parts = [new("Windows", 1), new("WSL", 1)];                 // new shares: widths, legend and name together
            PageRenderer.Settle(page);
            segments = TrackOf(bar).Segments;
            Assert.Equal([(Wide - BarTrack.Gap) / 2, (Wide - BarTrack.Gap) / 2], segments.Select(s => s.Width).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
            Assert.Equal(["Windows 1.0 GB", "WSL 1.0 GB"], Lines(bar));
            Assert.Equal("Windows 1.0 GB, WSL 1.0 GB", Said(bar));
            return 0;
        }, 480, 120);
    }

    public static TheoryData<string, string?> Themes() => new() { { "Dark", null }, { "Light", null }, { "HighContrast", "NightSky" }, { "HighContrast", "Desert" } };

    [Theory]
    [MemberData(nameof(Themes))]
    public void A_gap_of_the_background_keeps_two_segments_apart_and_each_swatch_is_its_segment_colour(string theme, string? palette)
    {
        var colours = palette is null ? null : Helpers.ContrastPalettes.Both.Single(p => p.Name == palette).Palette;
        PageRenderer.InPage(theme, () => new StackedBar
        {
            Parts = [new("Windows", 3), new("WSL", 1), new("Free", 4, Rest: true)], Unit = "GB", Width = Wide,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        }, page =>
        {
            var bar = ShellSupport.Under<StackedBar>(page).Single();
            var track = TrackOf(bar);
            var at = track.TranslatePoint(new Point(0, track.ActualHeight / 2), page);
            var pixels = PageRenderer.Pixels(page);
            Color Pixel(double x)
            {
                var p = pixels[(int)at.Y * (int)page.Width + (int)Math.Floor(at.X + x)];
                return Color.FromArgb((byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p);
            }
            var roles = bar.Legend.Select(l => l.Role).ToList();
            Assert.Equal(["B.Info", "B.Accent", StackedBar.RestRole], roles);   // no state colour; the rest the quiet one
            Color Role(int i) => ((SolidColorBrush)page.FindResource(roles[i])).Color;
            var background = ((SolidColorBrush)page.FindResource("B.Base")).Color;
            var segments = track.Segments;
            for (var i = 0; i < segments.Count; i++)
                Assert.Equal(Role(i), Pixel(segments[i].X + segments[i].Width / 2));
            Assert.Equal(background, Pixel(segments[0].X + segments[0].Width + BarTrack.Gap / 2)); // the gap after Windows
            Assert.Equal(background, Pixel(segments[1].X + segments[1].Width + BarTrack.Gap / 2)); // and after WSL
            var swatches = ShellSupport.Under<Swatch>(bar).ToList();
            Assert.Equal(3, swatches.Count);
            for (var i = 0; i < 3; i++) Assert.Equal(Role(i), ((SolidColorBrush)swatches[i].Fill!).Color);
            return 0;
        }, 480, 120, colours);
    }

    [Fact]
    public void A_zero_share_has_no_segment_but_keeps_its_legend_line_and_all_zero_is_an_empty_bar()
    {
        PageRenderer.InPage("Dark", () => Bar(("Windows", 3), ("WSL", 0), ("Free", 1)), page =>
        {
            var bar = ShellSupport.Under<StackedBar>(page).Single();
            var segments = TrackOf(bar).Segments;
            Assert.Equal(0, segments[1].Width);
            Assert.Equal(Wide, segments[0].Width + segments[2].Width + BarTrack.Gap, 6); // one gap, between the two drawn
            Assert.Equal(["Windows 3.0 GB", "WSL 0.0 GB", "Free 1.0 GB"], Lines(bar));

            bar.Parts = [new("Windows", 0), new("WSL", 0)];
            PageRenderer.Settle(page);
            Assert.All(TrackOf(bar).Segments, s => Assert.Equal(0, s.Width));   // all zero: no segment, not the full width
            Assert.True(bar.Available);
            Assert.Equal(["Windows 0.0 GB", "WSL 0.0 GB"], Lines(bar));
            Assert.Equal("Windows 0.0 GB, WSL 0.0 GB", Said(bar));
            return 0;
        }, 480, 120);
    }

    public static TheoryData<int> Unreadable() => new() { 0, 1, 2, 3, 4, 5 };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Unreadable_shares_are_Not_available_never_the_previous_bar(int which)
    {
        IReadOnlyList<BarPart>? parts = which switch
        {
            0 => null,
            1 => [],
            2 => [new("Windows", double.NaN), new("WSL", 1)],
            3 => [new("Windows", -2), new("WSL", 1)],
            4 => [new(null!, 2), new("WSL", 1)],              // review round 1: a missing name threw on the legend
            _ => [new(" ", 2), new("WSL", 1)],
        };
        PageRenderer.InPage("Dark", () => Bar(("Windows", 3), ("WSL", 1)), page =>
        {
            var bar = ShellSupport.Under<StackedBar>(page).Single();
            bar.Parts = parts;
            PageRenderer.Settle(page);
            Assert.False(bar.Available);
            Assert.Equal(StackedBar.NotAvailable, Said(bar));
            Assert.Contains(StackedBar.NotAvailable, PageRenderer.TextsOf(page));
            Assert.Empty(Lines(bar));
            Assert.All(TrackOf(bar).Segments, s => Assert.Equal(0, s.Width)); // not the 3 and 1 it had
            bar.Parts = [new("Free", 2)];
            PageRenderer.Settle(page);
            Assert.Equal("Free 2.0 GB", Said(bar));
            Assert.Equal(Wide, TrackOf(bar).Segments.Single().Width, 6);
            Assert.DoesNotContain(StackedBar.NotAvailable, PageRenderer.TextsOf(page).Where(t => ShellSupport.Under<TextBlock>(bar).Any(b => b.IsVisible && b.Text == t)));
            return 0;
        }, 480, 120);
    }

    [Fact]
    public void Shares_near_the_limit_of_a_double_lay_out_without_overflowing()
    {
        // Review round 1: 1e307 twice overflowed the sum and each width to infinity.
        var huge = StackedBar.Layout([1e307, 1e307], Wide, BarTrack.Gap);
        Assert.Equal([(Wide - BarTrack.Gap) / 2, (Wide - BarTrack.Gap) / 2], huge.Select(s => s.Width).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
        var max = StackedBar.Layout([double.MaxValue, double.MaxValue, 0], Wide, BarTrack.Gap);
        Assert.All(max, s => Assert.True(double.IsFinite(s.Width) && double.IsFinite(s.X)));
        Assert.Equal(Wide, max.Sum(s => s.Width) + BarTrack.Gap, 6);
    }

    [Fact]
    public void A_screen_reader_hears_one_sentence_and_nothing_inside_again()
    {
        PageRenderer.InPage("Dark", () => Bar(("Windows", 12.8), ("WSL", 9.1)), page =>
        {
            var bar = ShellSupport.Under<StackedBar>(page).Single();
            var peer = UIElementAutomationPeer.CreatePeerForElement(bar);
            Assert.Equal(("Windows 12.8 GB, WSL 9.1 GB", AutomationControlType.Text), (peer.GetName(), peer.GetAutomationControlType()));
            Assert.Null(peer.GetChildren());
            Assert.False(bar.Focusable);
            return 0;
        }, 480, 120);
    }
}
