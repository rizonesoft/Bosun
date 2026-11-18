namespace Bosun.Panel.Shell;

/// <summary>
/// A page whose own section has not built it yet: the empty state, saying it is coming and what to do meanwhile, in
/// words for the person using the panel (D02 T04 §9). The page's purpose is its summary, which the frame shows above
/// it. It is replaced, row by row, as the pages land.
/// </summary>
public sealed class PlaceholderPage : PageBase
{
    /// <summary>What a placeholder suggests meanwhile.</summary>
    public const string NextStep = "Choose another page from the list on the left; this one arrives in a later version.";

    public PlaceholderPage(string title, string summary, string owner) : base(title, summary)
    {
        Owner = string.IsNullOrWhiteSpace(owner) ? throw new ArgumentException("a placeholder names the section that builds its page", nameof(owner)) : owner;
        ShowEmpty($"{title} is coming soon.", NextStep);
    }

    /// <summary>
    /// The section of the plan that builds this page, as the plan writes it ("D03 T01 §5"): kept in the model, where
    /// the tests tie each rail row to a section that exists, and never shown.
    /// </summary>
    public string Owner { get; }
}
