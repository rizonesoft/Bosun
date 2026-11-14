using System.Globalization;
using System.Xml.Linq;

namespace Bosun.Panel.Tests;

/// <summary>
/// Whether every colour the panel draws words in stays readable on every surface it is drawn on (D02 T01 §2, after
/// Steward's ThemeContrastTests). 4.5:1 is the WCAG minimum for text under 18 pt, which is all of the panel: captions
/// are 11 px.
/// </summary>
static class ThemeContrast
{
    public const double Minimum = 4.5;

    /// <summary>The colours the panel writes words in, and the surfaces they are written on.</summary>
    public static readonly string[] TextRoles = ["Text", "TextDim", "TextMute", "Accent", "Ok", "Warn", "Danger", "Info"];
    public static readonly string[] Surfaces = ["Base", "Surface", "Raised"];

    /// <summary>The colours of a generated theme file, by token name ("Base", not "C.Base").</summary>
    public static Dictionary<string, string> Load(string xamlPath)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(xamlPath).Root!
            .Elements()
            .Where(e => e.Name.LocalName == "Color")
            .ToDictionary(e => ((string)e.Attribute(x + "Key")!)["C.".Length..], e => e.Value.Trim());
    }

    /// <summary>Every failing pair, named with its ratio; empty when the theme is readable.</summary>
    public static List<string> Failures(IReadOnlyDictionary<string, string> colours, double minimum = Minimum)
    {
        var failures = new List<string>();
        (double A, double R, double G, double B)? Colour(string role)
        {
            if (!colours.TryGetValue(role, out var hex)) { failures.Add($"{role} is missing from the theme"); return null; }
            if (Parse(hex) is not { } c) { failures.Add($"{role} {hex} is not #RRGGBB or #AARRGGBB"); return null; }
            return c;
        }
        void Check(string text, string surface)
        {
            if (Colour(text) is not { } fg || Colour(surface) is not { } bg) return;
            // Text can't be judged against a surface that shows what is behind it.
            if (bg.A < 1) { failures.Add($"{surface} {colours[surface]} is translucent, so text on it can't be judged"); return; }
            var ratio = Ratio(fg, bg);
            if (ratio < minimum) failures.Add($"{text} {colours[text]} on {surface} {colours[surface]} is {ratio.ToString("F2", CultureInfo.InvariantCulture)}:1, under {minimum.ToString(CultureInfo.InvariantCulture)}:1");
        }
        foreach (var text in TextRoles)
            foreach (var surface in Surfaces)
                Check(text, surface);
        Check("OnAccent", "Accent");  // Btn.Primary's label
        Check("Base", "Danger");      // the close button's glyph while the pointer is on it (Btn.Chrome, D02 T01 §4)
        // TextMute exists to sit below TextDim, and TextDim below Text: raising one for contrast must not collapse them.
        if (Colour("Text") is { } t && Colour("TextDim") is { } d && Colour("TextMute") is { } m && Colour("Base") is { } b && b.A >= 1)
        {
            if (!(Ratio(m, b) < Ratio(d, b))) failures.Add("TextMute is not dimmer than TextDim on Base");
            if (!(Ratio(d, b) < Ratio(t, b))) failures.Add("TextDim is not dimmer than Text on Base");
        }
        return failures.Distinct().ToList();
    }

    /// <summary>The WCAG 2 contrast ratio of a colour drawn over an opaque one; alpha in the foreground is composited first.</summary>
    public static double Ratio(string foreground, string background) =>
        Parse(foreground) is { } f && Parse(background) is { } b ? Ratio(f, b) : throw new FormatException($"{foreground} or {background} is not a colour");

    static double Ratio((double A, double R, double G, double B) fg, (double A, double R, double G, double B) bg)
    {
        // As WPF draws it: the text's colour blended over the surface by its alpha, in sRGB.
        double Blend(double f, double b) => fg.A * f + (1 - fg.A) * b;
        var a = Luminance(Blend(fg.R, bg.R), Blend(fg.G, bg.G), Blend(fg.B, bg.B));
        var c = Luminance(bg.R, bg.G, bg.B);
        return (Math.Max(a, c) + 0.05) / (Math.Min(a, c) + 0.05);
    }

    // WCAG 2 relative luminance of sRGB channels in 0..1.
    static double Luminance(double r, double g, double b)
    {
        static double Linear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    static (double A, double R, double G, double B)? Parse(string hex)
    {
        if (hex.Length is not (7 or 9) || hex[0] != '#') return null;
        if (!uint.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n)) return null;
        double alpha = hex.Length == 9 ? (n >> 24) / 255.0 : 1;  // #AARRGGBB, alpha first, as WPF reads it
        return (alpha, ((n >> 16) & 0xFF) / 255.0, ((n >> 8) & 0xFF) / 255.0, (n & 0xFF) / 255.0);
    }
}
