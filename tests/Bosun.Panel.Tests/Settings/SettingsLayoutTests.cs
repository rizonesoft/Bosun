using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Settings;

/// <summary>
/// Settings in cards on one spacing scale (D02 T04 §3): each section heading ranked above its field labels and in the
/// text colour, General and Notifications two cards the scale's XL apart, each holding its own controls at the form's
/// readable width; and the Memory page's cards, now the shared Card, 16 px inside on every side.
/// </summary>
public class SettingsLayoutTests
{
    static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => ShellSupport.Under<T>(root).Single(e => e.Name == name);

    static Rect Box(FrameworkElement e, Visual root) => e.TransformToAncestor(root).TransformBounds(new Rect(e.RenderSize));

    static bool Inside(FrameworkElement child, Border card)
    {
        for (DependencyObject? node = child; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, card)) return true;
        return false;
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Headings_rank_above_their_labels_in_the_text_colour(string theme)
    {
        PageRenderer.InPage(theme, () => new Bosun.Panel.Settings.SettingsView { DataContext = new SettingsFixture().Open().Page }, page =>
        {
            var label = Named<TextBlock>(page, "ThemeLabel");
            foreach (var name in new[] { "GeneralHeading", "NotificationsHeading" })
            {
                var heading = Named<TextBlock>(page, name);
                Assert.True(heading.FontSize > label.FontSize, $"{name} is {heading.FontSize} px, not above the label's {label.FontSize}");
                Assert.Equal(((SolidColorBrush)page.FindResource("B.Text")).Color, ((SolidColorBrush)heading.Foreground).Color);
                Assert.Equal(System.Windows.Automation.AutomationHeadingLevel.Level2, System.Windows.Automation.AutomationProperties.GetHeadingLevel(heading));
            }
            return 0;
        }, 900, 760);
    }

    [Fact]
    public void General_and_Notifications_are_two_cards_the_scale_s_XL_apart_each_with_its_own_controls()
    {
        PageRenderer.InPage("Dark", () => new Bosun.Panel.Settings.SettingsView { DataContext = new SettingsFixture().Open().Page }, page =>
        {
            var general = Named<Border>(page, "GeneralCard");
            var notifications = Named<Border>(page, "NotificationsCard");
            Assert.Same(page.FindResource("Card"), general.Style);
            Assert.Same(page.FindResource("Card"), notifications.Style);
            foreach (var name in new[] { "ThemeBox", "StartWithWindows", "PressureFloor", "GeneralHeading" })
                Assert.True(Inside(Named<FrameworkElement>(page, name), general), $"{name} is not in the General card");
            foreach (var name in new[] { "NotifyMemoryPressure", "NotificationsHeading" })
                Assert.True(Inside(Named<FrameworkElement>(page, name), notifications), $"{name} is not in the Notifications card");
            var gap = Box(notifications, page).Top - Box(general, page).Bottom;
            Assert.Equal((double)page.FindResource("S.XL"), gap, 0.5);
            return 0;
        }, 900, 760);
    }

    [Fact]
    public void What_a_person_needs_to_know_is_on_the_card_not_only_on_hover()
    {
        PageRenderer.InPage("Dark", () => new Bosun.Panel.Settings.SettingsView { DataContext = new SettingsFixture().Open().Page }, page =>
        {
            var texts = PageRenderer.TextsOf(page);
            Assert.Contains("Applies when saved, with no restart.", texts);
            Assert.Contains("Written to the Activity pane either way.", texts);
            Assert.True(Inside(Named<TextBlock>(page, "NotifyHint"), Named<Border>(page, "NotificationsCard")));
            return 0;
        }, 900, 760);
    }

    [Theory]
    [InlineData(1360, true)]
    [InlineData(420, false)]
    public void A_card_is_the_form_s_width_at_most_and_narrower_in_a_narrow_window(int width, bool wide)
    {
        PageRenderer.InPage("Dark", () => new Bosun.Panel.Settings.SettingsView { DataContext = new SettingsFixture().Open().Page }, page =>
        {
            var form = (double)page.FindResource("Size.Form");
            foreach (var name in new[] { "GeneralCard", "NotificationsCard" })
            {
                var card = Named<Border>(page, name);
                if (wide) Assert.Equal(form, card.ActualWidth, 0.5);
                else Assert.True(card.ActualWidth < form, $"{name} is {card.ActualWidth} px in a {width} px window");
            }
            return 0;
        }, width, 760);
    }

    [Fact]
    public void Each_Memory_card_holds_its_content_16_px_inside_on_every_side()
    {
        PageRenderer.InPage("Dark", () =>
        {
            var (view, _) = Memory.MemoryPageTests.Rendered(new Memory.MemoryPageTests.Source());
            return view;
        }, page =>
        {
            PageRenderer.Settle(page);
            // Measured where it is drawn: each card's content starts its border and the scale's L inside it, on every side.
            var inset = (double)page.FindResource("S.L");
            var cards = ShellSupport.Under<Border>(page).Where(b => ReferenceEquals(b.Style, page.FindResource("Card"))).ToList();
            Assert.Equal(6, cards.Count); // the chart, three cards, By repo and Decision
            foreach (var card in cards)
            {
                var content = (FrameworkElement)card.Child;
                var at = content.TransformToAncestor(card).Transform(new Point());
                var edge = card.BorderThickness.Left + inset;
                Assert.Equal(edge, at.X, 0.5);
                Assert.Equal(edge, at.Y, 0.5);
                Assert.Equal(edge, card.ActualWidth - at.X - content.ActualWidth, 0.5);
                Assert.Equal(edge, card.ActualHeight - at.Y - content.ActualHeight, 0.5);
            }
            return 0;
        }, 1360, 1100);
    }
}
