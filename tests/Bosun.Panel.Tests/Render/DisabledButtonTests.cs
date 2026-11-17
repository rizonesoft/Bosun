using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Render;

/// <summary>
/// Deliberate states (D02 T04 §6): a disabled button drawn in its own colours at full strength, never faded; a status
/// pill with a crisp edge in its status colour; the tab strip's count pill on the tabs' text line; and the window's
/// close drawn with the shared caption glyph, its name, tooltip and target kept.
/// </summary>
public class DisabledButtonTests
{
    static double Luminance(Color c)
    {
        static double F(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    /// <summary>The colour a brush shows over <paramref name="under"/>, its alpha blended.</summary>
    static Color Over(Color top, Color under)
    {
        var a = top.A / 255.0;
        return Color.FromRgb((byte)(top.R * a + under.R * (1 - a)), (byte)(top.G * a + under.G * (1 - a)), (byte)(top.B * a + under.B * (1 - a)));
    }

    static double Contrast(Color a, Color b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    static Color Of(Brush brush) => ((SolidColorBrush)brush).Color;

    public static readonly TheoryData<string, string> StylesThemes = new()
    {
        { "Btn", "Dark" }, { "Btn", "Light" }, { "Btn", "HighContrast" },
        { "Btn.Small", "Dark" }, { "Btn.Small", "Light" }, { "Btn.Small", "HighContrast" },
        { "Btn.Primary", "Dark" }, { "Btn.Primary", "Light" }, { "Btn.Primary", "HighContrast" },
        { "Btn.Danger", "Dark" }, { "Btn.Danger", "Light" }, { "Btn.Danger", "HighContrast" },
    };

    [Theory]
    [MemberData(nameof(StylesThemes))]
    public void A_disabled_button_is_drawn_in_its_own_colours_at_full_strength_and_never_reads_as_enabled(string style, string theme)
    {
        PageRenderer.InPage<int>(theme, () =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
            foreach (var enabled in new[] { true, false })
            {
                var button = new Button { Content = "Save", IsEnabled = enabled, Margin = new Thickness(0, 0, 12, 0) };
                button.SetResourceReference(FrameworkElement.StyleProperty, style);
                panel.Children.Add(button);
            }
            return panel;
        }, page =>
        {
            var page0 = Of(page.Background);
            var (on, off) = (ShellSupport.Under<Button>(page).First(b => b.IsEnabled), ShellSupport.Under<Button>(page).First(b => !b.IsEnabled));
            (Color Fill, Color Line, Color Text, double Opacity) Drawn(Button b)
            {
                var root = (FrameworkElement)b.Template.FindName("root", b);
                var bd = (Border)b.Template.FindName("bd", b);
                var content = (ContentPresenter)b.Template.FindName("content", b);
                var fill = Over(Of(bd.Background), page0);
                return (fill, Over(Of(bd.BorderBrush), page0), Over(Of((Brush)content.GetValue(TextElement.ForegroundProperty)), fill), root.Opacity * b.Opacity);
            }
            var (enabledFill, enabledLine, enabledText, _) = Drawn(on);
            var (fill, line, text, opacity) = Drawn(off);
            Assert.Equal(1.0, opacity, 3);                                                                     // never faded
            Assert.True(Contrast(text, fill) >= 3, $"{style} disabled text is {Contrast(text, fill):0.00}:1 on its fill in {theme}");
            // Quieter than its enabled self, as disabled means, but never fainter than the faintest enabled boundary the
            // panel draws, a secondary button's line: it never vanishes.
            // In a contrast theme Windows says what disabled is: its grey text, used as it says (D02 T03 §10).
            if (theme == "HighContrast") Assert.Equal(Of((Brush)page.FindResource("B.GrayText")), line);
            var floor = Contrast(Over(Of((Brush)page.FindResource("B.Line")), page0), page0);
            if (theme != "HighContrast") Assert.True(Contrast(line, page0) >= floor - 0.01,
                $"{style} disabled outline is {Contrast(line, page0):0.00}:1 against the page, under the line's {floor:0.00}:1 in {theme}");
            Assert.NotEqual(enabledText, text);                                                                // never reads as enabled
            if (theme != "HighContrast") Assert.NotEqual(enabledFill, fill);                                   // a contrast theme keeps the window under both
            return 0;
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("HighContrast")]
    public void A_disabled_window_button_keeps_full_strength_its_glyph_in_the_disabled_colour(string theme)
    {
        // The chrome buttons have their own template; the completion review found it still faded to 0.4.
        PageRenderer.InPage<int>(theme, () =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
            foreach (var enabled in new[] { true, false })
            {
                var glyph = new System.Windows.Shapes.Path { Data = Bosun.Panel.Theme.Glyphs.CaptionClose, StrokeThickness = 1 };
                var button = new Button { Content = glyph, IsEnabled = enabled, Width = 46, Height = 32 };
                glyph.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground)) { Source = button });
                button.SetResourceReference(FrameworkElement.StyleProperty, "Btn.Chrome");
                panel.Children.Add(button);
            }
            return panel;
        }, page =>
        {
            var (on, off) = (ShellSupport.Under<Button>(page).First(b => b.IsEnabled), ShellSupport.Under<Button>(page).First(b => !b.IsEnabled));
            var bd = (Border)off.Template.FindName("bd", off);
            Assert.Equal(1.0, bd.Opacity * off.Opacity, 3);
            var want = theme == "HighContrast" ? "B.GrayText" : "B.DisabledText";
            Assert.Equal(Of((Brush)page.FindResource(want)), Of(off.Foreground));
            Assert.NotEqual(Of(on.Foreground), Of(off.Foreground));
            return 0;
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void A_pill_is_a_capsule_with_room_around_its_word(string theme)
    {
        // Its corners are half its height, so its ends are semicircles, not the lens a 999 radius drew; and 3 px above
        // and below its word (D02 T04 §6 completion review).
        PageRenderer.InPage<int>(theme, () => new StackPanel { Margin = new Thickness(24), Children = { new StatusPill { Kind = StatusKind.Ok, Word = "Running" } } }, page =>
        {
            var pill = ShellSupport.Under<StatusPill>(page).Single();
            var border = (Border)VisualTreeHelper.GetChild(pill, 0);
            Assert.Equal(border.ActualHeight / 2, border.CornerRadius.TopLeft, 0.5);
            Assert.True(border.Padding.Top >= 3 && border.Padding.Bottom >= 3, $"{border.Padding}");
            return 0;
        });
        PageRenderer.InPage<int>(theme, () => ControlsGallery.Gallery(), page =>
        {
            var strip = ShellSupport.Under<TabControl>(page).Single();
            var count = (Border)strip.Template.FindName("TrailingPill", strip);
            Assert.Equal(count.ActualHeight / 2, count.CornerRadius.TopLeft, 0.5);
            return 0;
        }, 1360, 860);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void A_status_pill_has_a_1_px_edge_in_its_status_colour_and_no_effect(string theme)
    {
        PageRenderer.InPage<int>(theme, () =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(24) };
            foreach (var kind in Enum.GetValues<StatusKind>()) panel.Children.Add(new StatusPill { Kind = kind, Word = kind.ToString(), Margin = new Thickness(0, 0, 8, 0) });
            return panel;
        }, page =>
        {
            foreach (var pill in ShellSupport.Under<StatusPill>(page))
            {
                var key = pill.Kind == StatusKind.Neutral ? "B.Line" : $"B.{pill.Kind switch { StatusKind.Ok => "Ok", StatusKind.Warn => "Warn", StatusKind.Danger => "Danger", _ => "Info" }}Line";
                Assert.Equal(new Thickness(1), pill.BorderThickness);
                Assert.Equal(Of((Brush)page.FindResource(key)), Of(pill.BorderBrush));
                Assert.Null(pill.Effect);
            }
            return 0;
        });
    }

    [Fact]
    public void The_tab_strip_s_count_sits_as_a_pill_on_the_tabs_text_line()
    {
        PageRenderer.InPage<int>("Dark", () => ControlsGallery.Gallery(), page =>
        {
            var strip = ShellSupport.Under<TabControl>(page).Single();
            var pill = (Border)strip.Template.FindName("TrailingPill", strip);
            var tabText = ShellSupport.Under<TextBlock>(ShellSupport.Under<TabItem>(strip).First()).First(t => t.Text.Length > 0);
            double Middle(FrameworkElement e) => e.TransformToAncestor(page).TransformBounds(new Rect(e.RenderSize)) is var r ? r.Top + r.Height / 2 : 0;
            Assert.True(Math.Abs(Middle(pill) - Middle(tabText)) <= 1, $"the count's middle is {Middle(pill):0.0}, the tab text's {Middle(tabText):0.0}");
            Assert.Equal(Visibility.Visible, pill.Visibility);
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void Both_close_buttons_draw_the_shared_caption_glyph_named_with_its_tooltip_and_a_32_px_target()
    {
        PageRenderer.InPage<int>("Dark", () => ControlsGallery.Gallery(), page =>
        {
            var chrome = ShellSupport.Under<Button>(page).Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Close");
            AssertClose(chrome);
            return 0;
        }, 1360, 860);
        PageRenderer.InPage<int>("Dark", () => new Bosun.Panel.Shell.ShellView { DataContext = Shells.Panel() }, page =>
        {
            AssertClose(ShellSupport.Under<Button>(page).Single(b => b.Name == "CloseButton"));
            return 0;
        }, 1360, 860);

        static void AssertClose(Button button)
        {
            var glyph = ShellSupport.Under<System.Windows.Shapes.Path>(button).Single();
            Assert.Same(Bosun.Panel.Theme.Glyphs.CaptionClose, glyph.Data);
            Assert.Equal("Close", System.Windows.Automation.AutomationProperties.GetName(button));
            Assert.Equal("Close", button.ToolTip);
            Assert.True(button.ActualHeight >= 32, $"the close is {button.ActualHeight} px high");
            Assert.DoesNotContain(PageRenderer.TextsOf(button), t => t.Trim() == "x");
        }
    }
}
