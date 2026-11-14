using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// Both generated themes are readable (D02 T01 §2), and the check that says so can fail: a lowered token, an
/// alpha-only change, a translucent surface and a missing role are each named.
/// </summary>
public class ThemeContrastTests
{
    static Dictionary<string, string> Theme(string file) => ThemeContrast.Load(TestPaths.GeneratedTheme(file));

    [Theory]
    [InlineData("Dark.xaml")]
    [InlineData("Light.xaml")]
    public void Every_text_role_reads_at_4_5_to_1_or_better_on_every_surface(string file)
    {
        var colours = Theme(file);
        // The roles are really there to be judged: an empty theme has no failing pair either.
        foreach (var role in ThemeContrast.TextRoles.Concat(ThemeContrast.Surfaces).Append("OnAccent"))
            Assert.True(colours.ContainsKey(role), $"{file} has no {role}");
        var failures = ThemeContrast.Failures(colours);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    [Fact]
    public void A_lowered_token_fails_and_is_named()
    {
        var dark = Theme("Dark.xaml");
        dark["TextMute"] = "#5A6270";
        var failures = ThemeContrast.Failures(dark);
        Assert.Contains(failures, f => f.StartsWith("TextMute #5A6270 on Raised ", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, f => f.StartsWith("Text ", StringComparison.Ordinal));

        var light = Theme("Light.xaml");
        light["Accent"] = "#5B8DEF";
        failures = ThemeContrast.Failures(light);
        Assert.Contains(failures, f => f.StartsWith("Accent #5B8DEF on Base ", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.StartsWith("OnAccent ", StringComparison.Ordinal) && f.Contains(" on Accent #5B8DEF ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_close_buttons_glyph_reads_on_its_danger_hover()
    {
        // Btn.Chrome draws B.Base on B.Danger under the pointer (D02 T01 §4): a danger moved toward the base loses it.
        var dark = Theme("Dark.xaml");
        dark["Danger"] = "#5A2A26";
        Assert.Contains(ThemeContrast.Failures(dark), f => f.StartsWith("Base ", StringComparison.Ordinal) && f.Contains(" on Danger #5A2A26 ", StringComparison.Ordinal));
        foreach (var file in new[] { "Dark.xaml", "Light.xaml" })
            Assert.DoesNotContain(ThemeContrast.Failures(Theme(file)), f => f.Contains(" on Danger ", StringComparison.Ordinal));
    }

    [Fact]
    public void An_alpha_only_change_fails_because_text_is_composited_over_its_surface()
    {
        var dark = Theme("Dark.xaml");
        var rgb = dark["Text"].TrimStart('#');
        dark["Text"] = "#FF" + rgb;  // opaque, written with its alpha: the same colour
        Assert.Empty(ThemeContrast.Failures(dark));
        dark["Text"] = "#40" + rgb;  // the same RGB at a quarter strength
        var failures = ThemeContrast.Failures(dark);
        foreach (var surface in ThemeContrast.Surfaces)
            Assert.Contains(failures, f => f.StartsWith($"Text #40{rgb} on {surface} ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_translucent_surface_and_a_missing_role_are_failures_not_passes()
    {
        var dark = Theme("Dark.xaml");
        dark["Base"] = "#80" + dark["Base"].TrimStart('#');
        Assert.Contains(ThemeContrast.Failures(dark), f => f.StartsWith("Base ", StringComparison.Ordinal) && f.Contains("translucent", StringComparison.Ordinal));

        var light = Theme("Light.xaml");
        light.Remove("Warn");
        Assert.Contains("Warn is missing from the theme", ThemeContrast.Failures(light));
        light["Warn"] = "amber";
        Assert.Contains(ThemeContrast.Failures(light), f => f.StartsWith("Warn amber is not ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_text_levels_stay_apart()
    {
        var dark = Theme("Dark.xaml");
        dark["TextMute"] = dark["TextDim"];
        Assert.Contains("TextMute is not dimmer than TextDim on Base", ThemeContrast.Failures(dark));
        var light = Theme("Light.xaml");
        light["TextDim"] = light["Text"];
        Assert.Contains("TextDim is not dimmer than Text on Base", ThemeContrast.Failures(light));
    }

    [Fact]
    public void The_ratio_is_the_wcag_one()
    {
        Assert.Equal(21, ThemeContrast.Ratio("#FFFFFF", "#000000"), 6);
        Assert.Equal(1, ThemeContrast.Ratio("#808080", "#808080"), 6);
        Assert.Equal(ThemeContrast.Ratio("#777777", "#FFFFFF"), ThemeContrast.Ratio("#FFFFFF", "#777777"), 6);
        Assert.Equal(4.48, ThemeContrast.Ratio("#777777", "#FFFFFF"), 2);  // the textbook near miss
        Assert.Equal(1, ThemeContrast.Ratio("#00FFFFFF", "#123456"), 6);   // fully transparent text is its surface
    }
}
