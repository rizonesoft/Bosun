using MahApps.Metro.IconPacks;

namespace Bosun.Panel.Theme;

/// <summary>An action the panel shows a glyph for, wherever it appears.</summary>
public enum GlyphAction
{
    Refresh,
    OpenFolder,
    OpenTerminal,
    Pause,
    Search,
    StatusOk,
    StatusWarn,
    StatusFail,
    StatusInfo,
    StatusNeutral,
    SlotFree,
    SlotHeld,
    SlotUnknown,
    Save,
    Discard,
    Startup,
    Exit,
    Page,
    WindowsMemory,
    WslMemory,
    Builds,
    Table,
    Chart,
}

/// <summary>
/// The one icon map (D02 T03 §3, design/SPEC.md §19: Lucide, through one icon map): each rail row's glyph by its
/// section's key, and each shared action's, as the mockup (design/mockups/control-panel.html) chose them. A view never
/// names a Lucide kind itself (tests/Bosun.Panel.Tests/XamlRulesTests.cs), so every glyph stays consistent, and a kind
/// the pack does not have fails a test rather than drawing nothing.
/// </summary>
public static class Glyphs
{
    /// <summary>The rail rows' glyphs, by section key (src/Bosun.Panel/Shell/Sections.cs).</summary>
    public static IReadOnlyDictionary<string, PackIconLucideKind> Pages { get; } = new Dictionary<string, PackIconLucideKind>
    {
        ["overview"] = PackIconLucideKind.LayoutDashboard,
        ["memory"] = PackIconLucideKind.MemoryStick,
        ["instances"] = PackIconLucideKind.Terminal,
        ["queue"] = PackIconLucideKind.ListOrdered,
        ["optimise"] = PackIconLucideKind.Gauge,
        ["settings"] = PackIconLucideKind.Settings,
        ["about"] = PackIconLucideKind.Info,
    };

    /// <summary>The shared actions' glyphs.</summary>
    public static IReadOnlyDictionary<GlyphAction, PackIconLucideKind> Actions { get; } = new Dictionary<GlyphAction, PackIconLucideKind>
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
        [GlyphAction.StatusNeutral] = PackIconLucideKind.Pause, // the mockup's idle state
        // A queue slot (D02 T03 §6), as the mockup's tiles draw it: free a dot, held a terminal; unknown dashed.
        [GlyphAction.SlotFree] = PackIconLucideKind.CircleDot,
        [GlyphAction.SlotHeld] = PackIconLucideKind.Terminal,
        [GlyphAction.SlotUnknown] = PackIconLucideKind.CircleDashed,
        // The command palette's rows (D02 T03 §8): a page's own commands and the panel's.
        [GlyphAction.Save] = PackIconLucideKind.Save,
        [GlyphAction.Discard] = PackIconLucideKind.Undo2,
        [GlyphAction.Startup] = PackIconLucideKind.Power,
        [GlyphAction.Exit] = PackIconLucideKind.LogOut,
        [GlyphAction.Page] = PackIconLucideKind.ArrowRight,
        // The Memory page's cards (D03 T01 §2), as the mockup draws them.
        [GlyphAction.WindowsMemory] = PackIconLucideKind.Cpu,
        [GlyphAction.WslMemory] = PackIconLucideKind.Layers,
        [GlyphAction.Builds] = PackIconLucideKind.ListOrdered,
        // The history card's Show as table and Show as chart (D03 T01 §3).
        [GlyphAction.Table] = PackIconLucideKind.Table,
        [GlyphAction.Chart] = PackIconLucideKind.ChartLine,
    };

    /// <summary>A rail row's glyph. A row with none is a fault of this map, said by its key.</summary>
    public static PackIconLucideKind ForPage(string key) =>
        Pages.TryGetValue(key, out var kind) ? kind : throw new KeyNotFoundException($"the rail row '{key}' has no glyph in Theme/Glyphs.cs");

    public static PackIconLucideKind For(GlyphAction action) => Actions[action];

    /// <summary>The Memory page's card glyphs, for a view to name with x:Static.</summary>
    public static PackIconLucideKind WindowsMemory => Actions[GlyphAction.WindowsMemory];

    public static PackIconLucideKind WslMemory => Actions[GlyphAction.WslMemory];

    public static PackIconLucideKind Builds => Actions[GlyphAction.Builds];
}
