using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The sparkline (D02 T03 §5, design/SPEC.md §19 Sparkline): the line is the samples (spaced, in order, scaled), it is
/// drawn again for a new size, new samples or a new theme and never otherwise, and a screen reader hears the latest
/// value and its direction; unreadable samples are "Not available", never a line, a 0 or what it had before.
/// </summary>
public class SparklineTests
{
    static Sparkline Spark(params double[] samples) => new() { Samples = samples, Unit = "GB", Width = 200, Height = 34 };

    static string Said(UIElement element) => UIElementAutomationPeer.CreatePeerForElement(element).GetName();

    [Fact]
    public void The_line_is_the_samples_spaced_in_order_lowest_at_the_bottom()
    {
        PageRenderer.InPage("Dark", () => Spark(1, 3, 2, 4), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            var drawn = spark.Drawn;
            Assert.Equal(4, drawn.Count);
            Assert.Equal([0, 200 / 3.0, 400 / 3.0, 200], drawn.Select(p => Math.Round(p.X, 6)).ToArray(), (a, b) => Math.Abs(a - b) < 1e-6);
            Assert.Equal(34 - Sparkline.Inset, drawn[0].Y, 6);              // the lowest, 1, at the bottom
            Assert.Equal(Sparkline.Inset, drawn[3].Y, 6);                   // the highest, 4, at the top
            Assert.Equal(34 - Sparkline.Inset - (34 - 2 * Sparkline.Inset) * 2 / 3, drawn[1].Y, 6);
            Assert.True(drawn[2].Y > drawn[1].Y, "3 then 2: the line goes down");
            return 0;
        });
    }

    [Fact]
    public void One_value_throughout_is_a_flat_line_across_the_middle()
    {
        PageRenderer.InPage("Dark", () => Spark(5, 5, 5), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            Assert.All(spark.Drawn, p => Assert.Equal(17, p.Y, 6));
            Assert.Equal(200, spark.Drawn[^1].X, 6);
            return 0;
        });
    }

    [Fact]
    public void It_draws_again_for_new_samples_a_new_size_or_a_new_theme_and_never_otherwise()
    {
        PageRenderer.InPage("Dark", () => Spark(1, 2), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            var draws = spark.Draws;
            PageRenderer.Settle(page);
            PageRenderer.Settle(page);
            Assert.Equal(draws, spark.Draws);                               // nothing changed: nothing drawn

            spark.Samples = [4, 2, 1];
            PageRenderer.Settle(page);
            Assert.True(spark.Draws > draws);
            Assert.Equal(3, spark.Drawn.Count);
            Assert.Equal(Sparkline.Inset, spark.Drawn[0].Y, 6);

            draws = spark.Draws;
            spark.Width = 100;
            PageRenderer.Settle(page);
            Assert.True(spark.Draws > draws);
            Assert.Equal(100, spark.Drawn[^1].X, 6);                        // scaled to the new width

            draws = spark.Draws;
            var red = new SolidColorBrush(Colors.Red);
            page.Resources["B.Accent"] = red;                               // a theme change replaces the token
            PageRenderer.Settle(page);
            Assert.Same(red, spark.Stroke);
            Assert.True(spark.Draws > draws);
            return 0;
        });
    }

    [Fact]
    public void A_screen_reader_hears_the_latest_value_and_its_direction_following_new_samples()
    {
        PageRenderer.InPage("Dark", () => Spark(10.2, 11.5, 12.4), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            var peer = UIElementAutomationPeer.CreatePeerForElement(spark);
            Assert.Equal(("12.4 GB, rising", AutomationControlType.Text), (peer.GetName(), peer.GetAutomationControlType()));
            Assert.Null(peer.GetChildren());
            spark.Samples = [12.4, 11.0];
            Assert.Equal("11.0 GB, falling", peer.GetName());
            spark.Samples = [11.0, 11.05, 11.0];
            Assert.Equal("11.0 GB, steady", peer.GetName());
            spark.Samples = [11.0];
            PageRenderer.Settle(page);
            Assert.Equal("11.0 GB", peer.GetName());                       // one sample: the value, no trend
            Assert.Empty(spark.Drawn);
            Assert.False(spark.ShowsNotAvailable);
            return 0;
        });
    }

    public static TheoryData<double[]?> Unreadable() => new() { (double[]?)null, Array.Empty<double>(), new[] { 1.0, double.NaN }, new[] { 2.0, -1.0 }, new[] { double.PositiveInfinity } };

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Unreadable_samples_are_Not_available_never_a_line_or_what_it_had(double[]? samples)
    {
        PageRenderer.InPage("Dark", () => Spark(10, 12), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            spark.Samples = samples;
            PageRenderer.Settle(page);
            Assert.Equal(Sparkline.NotAvailable, Said(spark));
            Assert.Empty(spark.Drawn);                                      // not the 10 and 12 it had
            Assert.True(spark.ShowsNotAvailable, "Not available is written in place of the line");
            spark.Samples = [3, 4];
            PageRenderer.Settle(page);
            Assert.Equal("4.0 GB, rising", Said(spark));
            Assert.Equal(2, spark.Drawn.Count);
            Assert.False(spark.ShowsNotAvailable);
            return 0;
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void It_strokes_the_accent_and_writes_Not_available_dim(string theme)
    {
        PageRenderer.InPage(theme, () => Spark(1, 2), page =>
        {
            var spark = ShellSupport.Under<Sparkline>(page).Single();
            Assert.Equal(((SolidColorBrush)page.FindResource("B.Accent")).Color, ((SolidColorBrush)spark.Stroke!).Color);
            Assert.Equal(((SolidColorBrush)page.FindResource("B.TextDim")).Color, ((SolidColorBrush)spark.Quiet!).Color);
            return 0;
        });
    }
}
