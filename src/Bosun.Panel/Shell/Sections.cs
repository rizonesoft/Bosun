namespace Bosun.Panel.Shell;

/// <summary>What a rail row is before its page is made: where it sits, what it says, and which section of the plan builds it.</summary>
public sealed record SectionRow(string Key, string Title, string Group, string Summary, string Owner);

/// <summary>
/// The rail, in order: the pages of design/SPEC.md §2 to §16 that the panel has. Adding a page is one row here. Until
/// a page's own section builds it, its row opens a placeholder that names that section.
/// </summary>
public static class Sections
{
    public const string Monitor = "Monitor", Machine = "Machine", App = "App";

    public static IReadOnlyList<SectionRow> Rows { get; } =
    [
        new("overview", "Overview", Monitor, "Memory, the queue and dev instances at a glance, and what needs you.", "D03 T01 §5"),
        new("memory", "Memory", Monitor, "Live headroom, what is using it, and the peaks behind the 64 GB decision.", "D03 T01 §2"),
        new("instances", "Instances", Monitor, "Every dev instance on both sides, by repo.", "D03 T02 §2"),
        new("queue", "Queue", Monitor, "Who holds the build slots, who is waiting, and the limits.", "D03 T02 §3"),
        new("optimise", "Optimise", Machine, "What would make this machine faster, each change previewed, verified and undoable.", "D05 T02 §3"),
        new("settings", "Settings", App, "How the panel starts, looks and notifies.", "D02 T02 §5"),
        new("about", "About", App, "Version, runtime, log folders and licence.", "D07 T01 §1"),
    ];

    /// <summary>
    /// The rail's sections, each with a page of its own: made fresh, so two shells never share a page. A page that
    /// has been built is given by its row's key; every other row opens its placeholder.
    /// </summary>
    public static IReadOnlyList<Section> All(IReadOnlyDictionary<string, PageBase>? built = null)
    {
        if (built is not null && built.Keys.FirstOrDefault(key => Rows.All(row => row.Key != key)) is { } stray)
            throw new ArgumentException($"'{stray}' is not a row of the rail", nameof(built));
        return [.. Rows.Select(row => new Section(row.Key, row.Title, row.Group,
            built is not null && built.TryGetValue(row.Key, out var page) ? page : new PlaceholderPage(row.Title, row.Summary, row.Owner)))];
    }
}
