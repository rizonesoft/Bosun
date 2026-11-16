namespace Bosun.Core;

/// <summary>How a name matches what was typed, best first.</summary>
public enum CommandMatch
{
    /// <summary>The name starts with it.</summary>
    Prefix,

    /// <summary>A later word of the name starts with it.</summary>
    WordStart,

    /// <summary>It is somewhere inside the name.</summary>
    Substring,

    /// <summary>It is not in the name.</summary>
    None,
}

/// <summary>
/// The command palette's search (D02 T03 §8, design/SPEC.md §19 Command palette): names are ranked by how what was
/// typed meets them, a prefix of the whole name over the start of a later word over anywhere inside, ignoring case,
/// and names that don't hold it are left out. Equal matches keep the order they were given in, so the palette's own
/// order (pages, then the page's commands, then the panel's) breaks ties. Nothing typed lists everything in order.
/// </summary>
public static class CommandIndex
{
    /// <summary>How <paramref name="name"/> matches <paramref name="query"/>, ignoring case and the query's outer spaces.</summary>
    public static CommandMatch MatchOf(string name, string query)
    {
        ArgumentNullException.ThrowIfNull(name);
        var q = (query ?? "").Trim();
        if (q.Length == 0 || name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return CommandMatch.Prefix;
        var at = name.IndexOf(q, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return CommandMatch.None;
        for (; at >= 0; at = name.IndexOf(q, at + 1, StringComparison.OrdinalIgnoreCase))
            if (!char.IsLetterOrDigit(name[at - 1])) return CommandMatch.WordStart;
        return CommandMatch.Substring;
    }

    /// <summary>The items whose name holds the query, best match first, ties in the order given.</summary>
    public static IReadOnlyList<T> Rank<T>(IEnumerable<T> items, Func<T, string> nameOf, string query)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(nameOf);
        return items.Select((item, index) => (item, index, match: MatchOf(nameOf(item), query)))
            .Where(x => x.match != CommandMatch.None)
            .OrderBy(x => x.match).ThenBy(x => x.index)
            .Select(x => x.item).ToList();
    }
}
