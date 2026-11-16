using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;
using Bosun.Panel.Controls;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Bosun.Panel.Theme;
using MahApps.Metro.IconPacks;
using Xunit;

namespace Bosun.Panel.Tests.Controls;

/// <summary>
/// The status pill (D02 T03 §4, design/SPEC.md §19 Status pill): each kind's glyph from the icon map, its word in the
/// state's colour on that state's soft tint (the pairs ThemeContrastTests holds at 4.5:1 on Base, Surface and Raised),
/// Windows' colours in contrast; the word as its name, a blank word "Unknown"; a pill that changes while shown changes
/// all of it; and its glyph decorative.
/// </summary>
public class StatusPillTests
{
    public static TheoryData<string, StatusKind> Kinds()
    {
        var data = new TheoryData<string, StatusKind>();
        foreach (var theme in new[] { "Dark", "Light", "HighContrast" })
            foreach (var kind in Enum.GetValues<StatusKind>())
                data.Add(theme, kind);
        return data;
    }

    static (string Word, string Tint) Roles(StatusKind kind) => kind switch
    {
        StatusKind.Ok => ("B.Ok", "B.OkSoft"),
        StatusKind.Warn => ("B.Warn", "B.WarnSoft"),
        StatusKind.Danger => ("B.Danger", "B.DangerSoft"),
        StatusKind.Info => ("B.Info", "B.InfoSoft"),
        _ => ("B.TextDim", "B.Raised"),
    };

    static PackIconLucideKind Glyph(StatusKind kind) => kind switch
    {
        StatusKind.Ok => Glyphs.For(GlyphAction.StatusOk),
        StatusKind.Warn => Glyphs.For(GlyphAction.StatusWarn),
        StatusKind.Danger => Glyphs.For(GlyphAction.StatusFail),
        StatusKind.Info => Glyphs.For(GlyphAction.StatusInfo),
        _ => Glyphs.For(GlyphAction.StatusNeutral),
    };

    static Color Paint(object? brush) => ((SolidColorBrush)brush!).Color;

    static Color Brush(System.Windows.FrameworkElement at, string key) => ((SolidColorBrush)at.FindResource(key)).Color;

    static (PackIconLucide Glyph, TextBlock Word, Border Pill) Parts(StatusPill pill) =>
        (ShellSupport.Under<PackIconLucide>(pill).Single(), ShellSupport.Under<TextBlock>(pill).Single(), ShellSupport.Under<Border>(pill).First());

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Each_kind_draws_its_glyph_and_word_in_its_colours(string theme, StatusKind kind)
    {
        PageRenderer.InPage(theme, () => new StatusPill { Kind = kind, Word = "Running" }, page =>
        {
            var pill = ShellSupport.Under<StatusPill>(page).Single();
            var (glyph, word, border) = Parts(pill);
            Assert.Equal(Glyph(kind), glyph.Kind);
            Assert.Equal("Running", word.Text);
            if (theme == "HighContrast")
            {
                // Windows' colours alone: an outline and the window's text, never a tint.
                Assert.Equal(Brush(page, "B.Text"), Paint(word.Foreground));
                Assert.Equal(Brush(page, "B.Text"), Paint(border.BorderBrush));
                Assert.Equal(Colors.Transparent, Paint(border.Background));
            }
            else
            {
                var (wordRole, tintRole) = Roles(kind);
                Assert.Equal(Brush(page, wordRole), Paint(word.Foreground));
                Assert.Equal(Brush(page, wordRole), Paint(glyph.Foreground));
                Assert.Equal(Brush(page, tintRole), Paint(border.Background));
            }
            return 0;
        });
    }

    [Fact]
    public void Its_word_is_its_name_and_a_blank_word_is_Unknown_on_screen_and_said()
    {
        PageRenderer.InPage("Dark", () => new StatusPill { Kind = StatusKind.Ok, Word = "Ready" }, page =>
        {
            var pill = ShellSupport.Under<StatusPill>(page).Single();
            var peer = UIElementAutomationPeer.CreatePeerForElement(pill);
            Assert.Equal(("Ready", AutomationControlType.Text), (peer.GetName(), peer.GetAutomationControlType()));
            foreach (var blank in new[] { "", "  ", null })
            {
                pill.Word = blank!;
                PageRenderer.Settle(page);
                Assert.Equal((StatusPill.NoWord, StatusPill.NoWord), (Parts(pill).Word.Text, peer.GetName()));
            }
            return 0;
        });
    }

    [Fact]
    public void A_pill_that_changes_while_shown_changes_its_glyph_tint_word_and_name()
    {
        PageRenderer.InPage("Dark", () => new StatusPill { Kind = StatusKind.Ok, Word = "Running" }, page =>
        {
            var pill = ShellSupport.Under<StatusPill>(page).Single();
            var peer = UIElementAutomationPeer.CreatePeerForElement(pill);
            foreach (var (kind, word, shown) in new[] { (StatusKind.Warn, "Stale", "Stale"), (StatusKind.Danger, "", StatusPill.NoWord), (StatusKind.Info, "Waiting", "Waiting"), (StatusKind.Neutral, "Idle", "Idle") })
            {
                pill.Kind = kind;
                pill.Word = word;
                PageRenderer.Settle(page);
                var (glyph, text, border) = Parts(pill);
                Assert.Equal((Glyph(kind), shown, shown), (glyph.Kind, text.Text, peer.GetName()));
                Assert.Equal(Brush(page, Roles(kind).Tint), Paint(border.Background));
                Assert.Equal(Brush(page, Roles(kind).Word), Paint(text.Foreground));
            }
            return 0;
        });
    }

    [Fact]
    public void It_is_never_focused_and_its_glyph_is_not_said()
    {
        PageRenderer.InPage("Dark", () => new StatusPill { Kind = StatusKind.Warn, Word = "Stale" }, page =>
        {
            var pill = ShellSupport.Under<StatusPill>(page).Single();
            Assert.False(pill.Focusable);
            Assert.False(Parts(pill).Glyph.Focusable);
            Assert.Null(UIElementAutomationPeer.CreatePeerForElement(pill).GetChildren()); // said once, by its word
            return 0;
        });
    }
}
