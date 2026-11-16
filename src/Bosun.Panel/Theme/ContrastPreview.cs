using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;

namespace Bosun.Panel.Theme;

/// <summary>A Windows <c>.theme</c> file that can't be used as a contrast theme, and why.</summary>
public sealed class ThemeFileException(string message) : Exception(message);

/// <summary>
/// A Windows contrast theme read from its <c>.theme</c> file (D02 T03 §11): the colours of its
/// <c>[Control Panel\Colors]</c> section that the panel's contrast theme takes, the palette Windows would give.
/// </summary>
public static class ThemeFile
{
    /// <summary>No contrast theme Windows ships is near this; a larger file is not one.</summary>
    public const int MaxBytes = 64 * 1024;

    private const string Section = @"[Control Panel\Colors]";

    /// <summary>The keys read, in the order <see cref="SystemPalette"/> takes them.</summary>
    public static IReadOnlyList<string> Keys { get; } = ["Window", "WindowText", "Hilight", "HilightText", "GrayText", "HotTrackingColor", "ButtonFace", "ButtonText"];

    /// <summary>Reads a <c>.theme</c> file's contrast colours, refusing by name a file too large, a colour missing or a value not three numbers from 0 to 255.</summary>
    public static SystemPalette Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new ThemeFileException($"{path} does not exist");
        if (info.Length > MaxBytes)
            throw new ThemeFileException(string.Create(CultureInfo.InvariantCulture, $"{info.Name} is {info.Length:N0} bytes, more than a theme file's {MaxBytes:N0}"));
        return Parse(Decode(File.ReadAllBytes(path)), info.Name);
    }

    /// <summary>The colours from a theme file's text; <paramref name="name"/> is said in a refusal.</summary>
    public static SystemPalette Parse(string text, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inSection = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inSection = string.Equals(line, Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.IndexOf('=') is not (> 0 and var at)) continue;
            values[line[..at].Trim()] = line[(at + 1)..].Trim();
        }
        var colours = Keys.Select(key => values.TryGetValue(key, out var value)
            ? Colour(key, value, name)
            : throw new ThemeFileException($"{name} has no {key} colour in its {Section} section")).ToArray();
        return new SystemPalette(colours[0], colours[1], colours[2], colours[3], colours[4], colours[5], colours[6], colours[7]);
    }

    private static Color Colour(string key, string value, string name)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new ThemeFileException($"{name}'s {key} is '{value}', not three numbers");
        var rgb = new byte[3];
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                throw new ThemeFileException($"{name}'s {key} is '{value}', not three numbers");
            if (n > 255) throw new ThemeFileException($"{name}'s {key} has {n}, past 255");
            rgb[i] = (byte)n;
        }
        return Color.FromRgb(rgb[0], rgb[1], rgb[2]);
    }

    /// <summary>A theme file is UTF-16 when it says so with its byte mark, UTF-8 with its mark, else Windows' Latin.</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..]) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes is [0xEF, 0xBB, 0xBF, ..]) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        return Encoding.Latin1.GetString(bytes);
    }
}

/// <summary>
/// A contrast theme previewed from a <c>.theme</c> file (D02 T03 §11): always on, in that file's colours, whatever
/// Windows is in, so a test or a designer sees a contrast theme without changing the desktop.
/// </summary>
public sealed class PreviewContrast(SystemPalette palette, string file) : IHighContrast
{
    /// <summary>The variable that names the file.</summary>
    public const string Variable = "BOSUN_PANEL_CONTRAST";

    public bool On => true;

    public SystemPalette Palette { get; } = palette;

    /// <summary>The file's name, as the Settings page and the Activity pane say it.</summary>
    public string File { get; } = file;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    /// <summary>
    /// The contrast source for the panel: the preview when <paramref name="variable"/> names a file it can read, else
    /// Windows' own. What was chosen, or why a file was not used, is the line for the Activity pane.
    /// </summary>
    public static (IHighContrast Contrast, string? Line, bool Failed) Choose(string? variable, Func<IHighContrast> windows)
    {
        if (string.IsNullOrWhiteSpace(variable)) return (windows(), null, false);
        try
        {
            var palette = ThemeFile.Read(variable.Trim());
            var name = Path.GetFileName(variable.Trim());
            return (new PreviewContrast(palette, name), $"Contrast preview from {name} ({Variable}): the panel draws in its colours, whatever Windows is in", false);
        }
        catch (Exception e) when (e is ThemeFileException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (windows(), $"Contrast preview not used ({Variable}): {e.Message}; Windows' own contrast setting is used", true);
        }
    }
}
