namespace Bosun.Panel.Shell;

/// <summary>
/// One row of the rail and the page it opens (D02 T02 §4, after Steward's Section). The row shows the title; its
/// glyph comes from the icon map, keyed by <see cref="Key"/>; the line under the title is the page's own summary.
/// </summary>
public sealed record Section(string Key, string Title, string Group, PageBase Page)
{
    /// <summary>The row's name to a screen reader and to type-ahead: its title.</summary>
    public override string ToString() => Title;
}
