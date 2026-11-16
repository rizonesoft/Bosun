using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The one icon map and the rail's glyphs (D02 T03 §3, design/SPEC.md §19): each page and shared action has exactly
/// the mockup's kind; every rail row draws its own mapped kind, in the row's colour (the accent when selected, the
/// highlight's text in a contrast theme, both palettes); and the glyph is decorative, never a stop or said alone.
/// </summary>
public class GlyphTests
{
    [Fact]
    public void Each_page_has_the_mockups_glyph()
    {
        var expected = new Dictionary<string, PackIconLucideKind>
        {
            ["overview"] = PackIconLucideKind.LayoutDashboard,
            ["memory"] = PackIconLucideKind.MemoryStick,
            ["instances"] = PackIconLucideKind.Terminal,
            ["queue"] = PackIconLucideKind.ListOrdered,
            ["optimise"] = PackIconLucideKind.Gauge,
            ["settings"] = PackIconLucideKind.Settings,
            ["about"] = PackIconLucideKind.Info,
        };
        Assert.Equal(expected.OrderBy(p => p.Key), Glyphs.Pages.OrderBy(p => p.Key));
        Assert.Equal(Sections.Rows.Select(r => r.Key).Order(), Glyphs.Pages.Keys.Order()); // every rail row, and no other
        Assert.All(Glyphs.Pages.Values, kind => Assert.True(Enum.IsDefined(kind)));
        Assert.Equal("the rail row 'nowhere' has no glyph in Theme/Glyphs.cs", Assert.Throws<KeyNotFoundException>(() => Glyphs.ForPage("nowhere")).Message);
    }

    [Fact]
    public void Each_shared_action_has_the_mockups_glyph()
    {
        var expected = new Dictionary<GlyphAction, PackIconLucideKind>
        {
            [GlyphAction.Refresh] = PackIconLucideKind.RefreshCw,
            [GlyphAction.OpenFolder] = PackIconLucideKind.FolderOpen,
            [GlyphAction.OpenTerminal] = PackIconLucideKind.SquareTerminal,
            [GlyphAction.Pause] = PackIconLucideKind.Pause,
            [GlyphAction.Search] = PackIconLucideKind.Search,
            [GlyphAction.StatusOk] = PackIconLucideKind.CircleCheck,
            [GlyphAction.StatusWarn] = PackIconLucideKind.TriangleAlert,
            [GlyphAction.StatusFail] = PackIconLucideKind.OctagonAlert,
        };
        Assert.Equal(Enum.GetValues<GlyphAction>().Order(), Glyphs.Actions.Keys.Order());
        Assert.Equal(expected.OrderBy(p => p.Key), Glyphs.Actions.OrderBy(p => p.Key));
    }

    static ListBox Rail(Border page) => ShellSupport.Under<ListBox>(page).First(l => l.Name == "Rail");

    static PackIconLucide GlyphOf(ListBoxItem row) => ShellSupport.Under<PackIconLucide>(row).Single();

    static Color Brush(Border page, string key) => ((SolidColorBrush)page.FindResource(key)).Color;

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Every_rail_row_draws_its_mapped_glyph_in_the_rows_colour_which_follows_the_selection(string theme)
    {
        PageRenderer.InPage(theme, () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var rail = Rail(page);
            for (var i = 0; i < rail.Items.Count; i++)
            {
                var row = ShellSupport.Row(rail, i);
                var glyph = GlyphOf(row);
                Assert.Equal(Glyphs.ForPage(((Section)rail.Items[i]).Key), glyph.Kind);
                Assert.Equal((16.0, 16.0), (glyph.ActualWidth, glyph.ActualHeight));
                Assert.Equal(i == 0 ? Brush(page, "B.Accent") : Brush(page, "B.TextDim"), ((SolidColorBrush)glyph.Foreground).Color);
            }
            rail.SelectedIndex = 3; // the selection moves, and the colours with it
            PageRenderer.Settle(page);
            Assert.Equal(Brush(page, "B.Accent"), ((SolidColorBrush)GlyphOf(ShellSupport.Row(rail, 3)).Foreground).Color);
            Assert.Equal(Brush(page, "B.TextDim"), ((SolidColorBrush)GlyphOf(ShellSupport.Row(rail, 0)).Foreground).Color);
            return 0;
        }, 1360, 860);
    }

    public static TheoryData<string> Palettes => ["NightSky", "Desert"];

    [Theory]
    [MemberData(nameof(Palettes))]
    public void In_a_contrast_theme_the_selected_glyph_is_the_highlights_text_colour_never_the_fill_under_it(string palette)
    {
        var colours = palette == "NightSky" ? Helpers.ContrastPalettes.NightSky : Helpers.ContrastPalettes.Desert;
        PageRenderer.InPage("HighContrast", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var row = ShellSupport.Row(Rail(page), 0);
            var glyph = ((SolidColorBrush)GlyphOf(row).Foreground).Color;
            Assert.Equal(Brush(page, "B.HighlightText"), glyph);
            Assert.NotEqual(Brush(page, "B.Accent"), glyph); // the row is filled with the highlight: an accent glyph would vanish
            return 0;
        }, 1360, 860, colours);
    }

    [Fact]
    public void The_glyph_is_decorative_never_a_stop_nor_said_on_its_own()
    {
        PageRenderer.InPage("Dark", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var row = ShellSupport.Row(Rail(page), 0);
            Assert.False(GlyphOf(row).Focusable);
            var peer = UIElementAutomationPeer.CreatePeerForElement(row);
            var children = peer.GetChildren() ?? [];
            // What a reader meets, in the control view: the row's title alone; no image, no glyph, no badge.
            var said = children.Where(c => c.IsControlElement()).Select(c => (c.GetAutomationControlType(), c.GetName())).ToList();
            Assert.Equal([(AutomationControlType.Text, "Overview")], said);
            // The glyph's and the badge's holders are in the raw view only, empty, and in no other view.
            var decorative = children.OfType<FrameworkElementAutomationPeer>().Where(c => c.Owner is Bosun.Panel.Controls.Decorative).ToList();
            Assert.Equal(2, decorative.Count);
            Assert.All(decorative, c => Assert.True(!c.IsControlElement() && !c.IsContentElement() && c.GetChildren() is null));
            return 0;
        }, 1360, 860);
    }
}
