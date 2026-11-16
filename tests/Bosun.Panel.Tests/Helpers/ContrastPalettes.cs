using System.Windows.Media;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Tests.Helpers;

/// <summary>
/// Two of Windows 11's contrast themes as they ship (<c>C:\Windows\Resources\Ease of Access Themes\</c>, read
/// 2026-11-16), so a test or a picture in contrast is the same on every machine whatever its own colours are.
/// </summary>
static class ContrastPalettes
{
    /// <summary>Night sky (<c>hc2.theme</c>): white text on black, a lilac highlight, yellow button text. Dark.</summary>
    public static SystemPalette NightSky { get; } = new(Rgb(0, 0, 0), Rgb(255, 255, 255), Rgb(214, 180, 253), Rgb(43, 43, 43), Rgb(166, 166, 166), Rgb(128, 128, 255), Rgb(0, 0, 0), Rgb(255, 238, 50));

    /// <summary>Desert (<c>hcwhite.theme</c>): dark text on cream, a rust highlight. Light.</summary>
    public static SystemPalette Desert { get; } = new(Rgb(255, 250, 239), Rgb(61, 61, 61), Rgb(144, 57, 9), Rgb(255, 245, 227), Rgb(103, 103, 103), Rgb(28, 94, 117), Rgb(255, 250, 239), Rgb(32, 32, 32));

    public static IReadOnlyList<(string Name, SystemPalette Palette)> Both { get; } = [("NightSky", NightSky), ("Desert", Desert)];

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

/// <summary>Windows' contrast setting held on, in Night sky, for a picture of what the panel does while it is.</summary>
sealed class ContrastOn : IHighContrast
{
    public bool On => true;

    public SystemPalette Palette => ContrastPalettes.NightSky;

    public event Action? Changed { add { } remove { } }
}
