using System.IO;
using System.Text;
using Bosun.Panel.Settings;
using Bosun.Panel.Tests.Helpers;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Theme;

/// <summary>
/// The contrast preview (D02 T03 §11): a Windows <c>.theme</c> file read as the palette Windows would give, every bad
/// file refused by name, and the panel's choice of contrast source, which never fails a start.
/// </summary>
public sealed class ThemeFileTests : IDisposable
{
    static readonly string Themes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Ease of Access Themes");

    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("bosun-theme-");

    public void Dispose() => _dir.Delete(recursive: true);

    string Copy(string name)
    {
        var to = Path.Combine(_dir.FullName, name);
        File.Copy(Path.Combine(Themes, name), to);
        return to;
    }

    string Write(string name, string text)
    {
        var to = Path.Combine(_dir.FullName, name);
        File.WriteAllText(to, text, Encoding.Latin1);
        return to;
    }

    static string Section(string? drop = null, string? replace = null, string? with = null)
    {
        var lines = new[] { "Window=0 0 0", "WindowText=255 255 255", "Hilight=214 180 253", "HilightText=43 43 43", "GrayText=166 166 166",
            "HotTrackingColor=128 128 255", "ButtonFace=0 0 0", "ButtonText=255 238 50" }
            .Where(l => drop is null || !l.StartsWith(drop + "=", StringComparison.Ordinal))
            .Select(l => replace is not null && l.StartsWith(replace + "=", StringComparison.Ordinal) ? $"{replace}={with}" : l);
        return "[Theme]\r\nDisplayName=Test\r\n\r\n[Control Panel\\Colors]\r\n" + string.Join("\r\n", lines) + "\r\n\r\n[Other]\r\nWindow=1 2 3\r\n";
    }

    [Theory]
    [InlineData("hc2.theme", "NightSky")]
    [InlineData("hcwhite.theme", "Desert")]
    public void A_contrast_theme_file_reads_as_the_palette_Windows_gives(string file, string palette) =>
        Assert.Equal(ContrastPalettes.Both.Single(p => p.Name == palette).Palette, ThemeFile.Read(Copy(file)));

    [Fact]
    public void Only_its_colours_section_counts_and_a_UTF16_file_reads_the_same()
    {
        var latin = ThemeFile.Read(Write("a.theme", Section()));
        Assert.Equal(ContrastPalettes.NightSky, latin);                                        // [Other]'s Window is not read
        var wide = Path.Combine(_dir.FullName, "wide.theme");
        File.WriteAllText(wide, Section(), Encoding.Unicode);
        Assert.Equal(latin, ThemeFile.Read(wide));
    }

    [Theory]
    [InlineData("Window")]
    [InlineData("WindowText")]
    [InlineData("Hilight")]
    [InlineData("HilightText")]
    [InlineData("GrayText")]
    [InlineData("HotTrackingColor")]
    [InlineData("ButtonFace")]
    [InlineData("ButtonText")]
    public void A_file_missing_a_colour_is_refused_naming_it(string key)
    {
        var refused = Assert.Throws<ThemeFileException>(() => ThemeFile.Read(Write("missing.theme", Section(drop: key))));
        Assert.Equal($"missing.theme has no {key} colour in its [Control Panel\\Colors] section", refused.Message);
    }

    [Theory]
    [InlineData("0 0", "not three numbers")]
    [InlineData("a b c", "not three numbers")]
    [InlineData("-1 0 0", "not three numbers")]
    [InlineData("0 0 256", "past 255")]
    public void A_value_that_is_not_a_colour_is_refused_naming_it(string value, string why)
    {
        var refused = Assert.Throws<ThemeFileException>(() => ThemeFile.Read(Write("bad.theme", Section(replace: "Hilight", with: value))));
        Assert.StartsWith("bad.theme's Hilight", refused.Message, StringComparison.Ordinal);
        Assert.Contains(why, refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_over_64_KB_is_refused_before_it_is_read()
    {
        var big = Write("big.theme", Section() + new string(';', ThemeFile.MaxBytes));
        var refused = Assert.Throws<ThemeFileException>(() => ThemeFile.Read(big));
        Assert.Contains("more than a theme file's 65,536", refused.Message, StringComparison.Ordinal);
    }

    sealed class Windows : IHighContrast
    {
        public bool On => false;

        public SystemPalette Palette => ContrastPalettes.Desert;

        public event Action? Changed
        {
            add { }
            remove { }
        }
    }

    [Fact]
    public void The_panel_takes_a_preview_it_can_read_and_otherwise_Windows_own_saying_why()
    {
        var windows = new Windows();
        var none = PreviewContrast.Choose(null, () => windows);
        Assert.Same(windows, none.Contrast);
        Assert.Null(none.Line);

        var good = PreviewContrast.Choose(Copy("hc2.theme"), () => windows);
        var preview = Assert.IsType<PreviewContrast>(good.Contrast);
        Assert.True(preview.On);
        Assert.Equal(ContrastPalettes.NightSky, preview.Palette);
        Assert.Equal("Contrast preview from hc2.theme (BOSUN_PANEL_CONTRAST): the panel draws in its colours, whatever Windows is in", good.Line);
        Assert.False(good.Failed);

        foreach (var bad in new[] { Path.Combine(_dir.FullName, "gone.theme"), Write("damaged.theme", Section(drop: "Hilight")),
                     Write("huge.theme", Section() + new string(';', ThemeFile.MaxBytes)), _dir.FullName + "\\\0" })
        {
            var fell = PreviewContrast.Choose(bad, () => windows);
            Assert.Same(windows, fell.Contrast);                                               // never a failed start
            Assert.True(fell.Failed);
            Assert.StartsWith("Contrast preview not used (BOSUN_PANEL_CONTRAST): ", fell.Line, StringComparison.Ordinal);
            Assert.EndsWith("; Windows' own contrast setting is used", fell.Line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_file_that_is_not_read_in_time_falls_back_without_holding_the_start()
    {
        // Round 1: a file on a share that had gone away held the start with no window and no tray.
        var stalled = new ManualResetEventSlim();
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var chosen = PreviewContrast.Choose(@"\\gone\themes\hc2.theme", () => new Windows(), _ =>
            {
                stalled.Wait();
                return ContrastPalettes.NightSky;
            }, TimeSpan.FromMilliseconds(200));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the start waited {clock.Elapsed}");
            Assert.IsType<Windows>(chosen.Contrast);
            Assert.True(chosen.Failed);
            Assert.Equal("Contrast preview not used (BOSUN_PANEL_CONTRAST): hc2.theme was not read within 0.2 s; Windows' own contrast setting is used", chosen.Line);
        }
        finally
        {
            stalled.Set();                                                                       // let the stalled read end
        }
    }
}
