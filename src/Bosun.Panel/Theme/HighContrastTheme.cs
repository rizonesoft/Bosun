using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Bosun.Panel.Theme;

/// <summary>
/// Whether the panel is drawn in Windows' contrast colours (D02 T03 §10): an inherited attached property that
/// <see cref="ThemeService"/> sets on the window while they are in force, and the renders on their page. The control
/// templates (Theme/Controls.xaml) read it for what a colour can't say: a disabled control is not faded but drawn in
/// the gray text colour, and a selected item takes the highlight text colour on the highlight. It reaches a combo
/// box's drop-down through the box's logical tree.
/// </summary>
public static class HighContrastMode
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(HighContrastMode), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}

/// <summary>
/// The colours of a Windows contrast theme, the ones a contrast theme lets the person choose (Settings, Accessibility,
/// Contrast themes): the window and its text, the highlight and its text, the gray text of what is disabled, and a button's face and text
/// (Windows' ButtonFace and ButtonText, which WPF calls Control and ControlText).
/// </summary>
public sealed record SystemPalette(Color Window, Color WindowText, Color Highlight, Color HighlightText, Color GrayText, Color HotTrack,
    Color ButtonFace, Color ButtonText)
{
    /// <summary>The colours Windows has in force now.</summary>
    public static SystemPalette Current() => new(SystemColors.WindowColor, SystemColors.WindowTextColor, SystemColors.HighlightColor,
        SystemColors.HighlightTextColor, SystemColors.GrayTextColor, SystemColors.HotTrackColor, SystemColors.ControlColor, SystemColors.ControlTextColor);
}

/// <summary>
/// The panel's colour keys in a Windows contrast theme (D02 T03 §10). Built from a <see cref="SystemPalette"/> and the
/// generated theme's own keys, so every <c>C.*</c> and <c>B.*</c> key the views use has a value, and merged over the
/// generated Dark theme, which still gives the measures and the motion. A contrast theme has one text colour and a
/// window colour; Windows' guidance is text on window, the highlight for what is selected, and gray text only for
/// what is disabled. So: every surface, hover and soft tint is the window; every text, line and status colour is the
/// window text (a status still says its word); the accent is the highlight, and what sits on it the highlight text.
/// Shadows are not drawn. The keys of <see cref="OwnKeys"/> (gray text, highlight text, and a button's face and text)
/// are this dictionary's own, for the templates' contrast triggers.
/// </summary>
public static class HighContrastTheme
{
    /// <summary>The keys only this dictionary holds, which the templates' contrast triggers name.</summary>
    public static IReadOnlyList<string> OwnKeys { get; } =
        ["C.GrayText", "B.GrayText", "C.HighlightText", "B.HighlightText", "C.ButtonFace", "B.ButtonFace", "C.ButtonText", "B.ButtonText"];

    /// <summary>Which palette colour a theme colour key takes.</summary>
    public static Color For(string key, SystemPalette palette) => key switch
    {
        "Base" or "Surface" or "Raised" or "Hover" or "DisabledFill" => palette.Window,
        "DisabledText" or "DisabledLine" => palette.GrayText,
        _ when key.EndsWith("Soft", StringComparison.Ordinal) => palette.Window,
        "Accent" or "AccentDim" => palette.Highlight,
        "OnAccent" => palette.HighlightText,
        _ => palette.WindowText,
    };

    /// <summary>
    /// The contrast dictionary for <paramref name="palette"/>: a key for each colour and elevation of
    /// <paramref name="generated"/> (a generated theme, read for its keys only), and the two keys of its own.
    /// </summary>
    public static ResourceDictionary Build(SystemPalette palette, ResourceDictionary generated)
    {
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(generated);
        var made = new ResourceDictionary();
        foreach (var key in generated.Keys.OfType<string>().Order(StringComparer.Ordinal))
        {
            if (key.StartsWith("C.", StringComparison.Ordinal))
            {
                var colour = For(key[2..], palette);
                made[key] = colour;
                made["B." + key[2..]] = Frozen(colour);
            }
            else if (key.StartsWith("Elevation.", StringComparison.Ordinal))
            {
                // No shadow: a contrast theme draws edges with lines, which the borders already are.
                var none = new DropShadowEffect { Opacity = 0, BlurRadius = 0, ShadowDepth = 0 };
                none.Freeze();
                made[key] = none;
            }
        }
        foreach (var (name, colour) in new[] { ("GrayText", palette.GrayText), ("HighlightText", palette.HighlightText), ("ButtonFace", palette.ButtonFace), ("ButtonText", palette.ButtonText) })
        {
            made["C." + name] = colour; // the keys of OwnKeys
            made["B." + name] = Frozen(colour);
        }
        return made;
    }

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }
}
