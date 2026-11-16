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
            [GlyphAction.StatusInfo] = PackIconLucideKind.CircleDot,
            [GlyphAction.StatusNeutral] = PackIconLucideKind.Pause,
            [GlyphAction.SlotFree] = PackIconLucideKind.CircleDot,
            [GlyphAction.SlotHeld] = PackIconLucideKind.Terminal,
            [GlyphAction.SlotUnknown] = PackIconLucideKind.CircleDashed,
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

    sealed class FakeContrast(SystemPalette palette) : IHighContrast
    {
        public bool On { get; private set; }

        public SystemPalette Palette { get; private set; } = palette;

        public event Action? Changed;

        public void Set(bool on, SystemPalette? palette = null)
        {
            On = on;
            Palette = palette ?? Palette;
            Changed?.Invoke();
        }
    }

    static ResourceDictionary Generated(string theme)
    {
        using var stream = System.IO.File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"));
        return (ResourceDictionary)System.Windows.Markup.XamlReader.Load(stream);
    }

    [Fact]
    public void The_selected_glyph_follows_live_theme_changes_into_and_out_of_both_contrast_palettes()
    {
        // Completion review: one shell, its theme changed live as the person or Windows changes it.
        PageRenderer.InPage("Dark", () => new ShellView { DataContext = Shells.Panel() }, page =>
        {
            var contrast = new FakeContrast(Helpers.ContrastPalettes.NightSky);
            var theme = new ThemeService(page.Resources.MergedDictionaries, new Settings.FakeWindowsTheme(), page.Dispatcher, Generated, contrast);
            HighContrastMode.SetIsOn(page, theme.IsHighContrast);
            theme.EffectiveChanged += () => HighContrastMode.SetIsOn(page, theme.IsHighContrast);
            var rail = Rail(page);
            Color Glyph()
            {
                PageRenderer.Settle(page);
                return ((SolidColorBrush)GlyphOf(ShellSupport.Row(rail, 0)).Foreground).Color;
            }

            theme.Apply(Bosun.Core.ThemeChoice.Light);
            Assert.Equal(Brush(page, "B.Accent"), Glyph());
            theme.Apply(Bosun.Core.ThemeChoice.Dark);
            Assert.Equal(Brush(page, "B.Accent"), Glyph());
            contrast.Set(true); // Windows turns a contrast theme on: Night sky
            Assert.True(StartupSupport.Pump(() => theme.IsHighContrast));
            Assert.Equal(Brush(page, "B.HighlightText"), Glyph());
            Assert.Equal(Color.FromRgb(43, 43, 43), Glyph()); // Night sky's highlight text
            contrast.Set(true, Helpers.ContrastPalettes.Desert); // another contrast theme chosen
            Assert.True(StartupSupport.Pump(() => Brush(page, "B.HighlightText") == Color.FromRgb(255, 245, 227)));
            Assert.Equal(Color.FromRgb(255, 245, 227), Glyph()); // Desert's
            contrast.Set(false); // and off again: the person's choice back
            Assert.True(StartupSupport.Pump(() => !theme.IsHighContrast));
            Assert.Equal(Brush(page, "B.Accent"), Glyph());
            return 0;
        }, 1360, 860);
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
