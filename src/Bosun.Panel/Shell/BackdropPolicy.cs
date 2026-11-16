namespace Bosun.Panel.Shell;

/// <summary>What the window's chrome sits on (design/SPEC.md §1 and §19, Backdrop).</summary>
public enum BackdropKind
{
    /// <summary>Windows' Mica under the title bar and rail, composed by Windows outside the process.</summary>
    Mica,

    /// <summary>The theme's solid <c>Base</c>, the same layout, where Mica is unavailable or unwanted.</summary>
    Solid,
}

/// <summary>
/// When the panel's chrome is Mica (D02 T03 §2, design/SPEC.md §19): only on Windows 11 build 22621 or later, which
/// has the system backdrop types, with the person's transparency effects on, and never in a contrast theme, whose
/// colours must be the only ones on screen. Anything else is solid <c>Base</c>.
/// </summary>
public static class BackdropPolicy
{
    /// <summary>The first build with <c>DWMWA_SYSTEMBACKDROP_TYPE</c> (Windows 11 22H2).</summary>
    public const int FirstMicaBuild = 22621;

    public static BackdropKind Choose(int build, bool transparency, bool highContrast) =>
        build >= FirstMicaBuild && transparency && !highContrast ? BackdropKind.Mica : BackdropKind.Solid;
}
