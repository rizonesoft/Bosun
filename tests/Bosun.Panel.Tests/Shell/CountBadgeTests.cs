using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Bosun.Panel.Controls;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// A rail row's count (D02 T03 §3, design/SPEC.md §19 Count badge): shown only while its page has something waiting,
/// following the page live, and said in the row's name ("Queue, 3 waiting") once, the badge itself never said again.
/// </summary>
public class CountBadgeTests
{
    [Fact]
    public void One_row_through_0_3_1_and_0_shows_and_says_its_count_each_time()
    {
        PageRenderer.InPage("Dark", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var rail = ShellSupport.Under<ListBox>(page).First(l => l.Name == "Rail");
            var queue = (Section)rail.Items.Cast<Section>().Single(s => s.Key == "queue");
            ListBoxItem Row() => ShellSupport.Row(rail, rail.Items.IndexOf(queue));
            Decorative Badge() => ShellSupport.Under<Decorative>(Row()).Single(d => d.Name == "Badge");
            string Said() => UIElementAutomationPeer.CreatePeerForElement(Row()).GetName();
            string? Shown() => Badge().Visibility == Visibility.Visible ? PageRenderer.TextsOf(Badge()).Single() : null;

            foreach (var (count, name, badge) in new[] { (0, "Queue", (string?)null), (3, "Queue, 3 waiting", "3"), (1, "Queue, 1 waiting", "1"), (0, "Queue", null) })
            {
                queue.Page.Waiting = count;
                PageRenderer.Settle(page);
                Assert.Equal((name, badge), (Said(), Shown()));
            }
            queue.Page.Waiting = -2; // never below none
            Assert.Equal(0, queue.Page.Waiting);
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void In_a_contrast_theme_a_badge_is_its_rows_colour_never_white_on_the_highlight()
    {
        // Completion review: the selected row's badge read white on the highlight's fill.
        PageRenderer.InPage("HighContrast", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var rail = ShellSupport.Under<ListBox>(page).First(l => l.Name == "Rail");
            foreach (var section in rail.Items.Cast<Section>()) section.Page.Waiting = 4;
            PageRenderer.Settle(page);
            foreach (var index in new[] { 0, 1 }) // selected, and not
            {
                var row = ShellSupport.Row(rail, index);
                var pill = ShellSupport.Under<Border>(ShellSupport.Under<Decorative>(row).Single(d => d.Name == "Badge")).First();
                var text = ShellSupport.Under<TextBlock>(pill).Single();
                var rowColour = ((System.Windows.Media.SolidColorBrush)row.Foreground).Color;
                Assert.Equal(rowColour, ((System.Windows.Media.SolidColorBrush)text.Foreground).Color);
                Assert.Equal(rowColour, ((System.Windows.Media.SolidColorBrush)pill.BorderBrush).Color);
            }
            return 0;
        }, 1360, 860);
    }

    [Fact]
    public void The_row_name_reads_as_the_title_and_the_count_in_words() =>
        Assert.Equal(("Queue", "Queue, 1 waiting", "Queue, 12 waiting"), (RailRowName.Of("Queue", 0), RailRowName.Of("Queue", 1), RailRowName.Of("Queue", 12)));
}
