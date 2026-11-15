namespace Bosun.Panel.Shell;

/// <summary>
/// A page whose own section has not built it yet: the empty state, saying so and naming the section of the plan that
/// will. It is replaced, row by row, as the pages land.
/// </summary>
public sealed class PlaceholderPage : PageBase
{
    public PlaceholderPage(string title, string summary, string owner) : base(title, summary)
    {
        Owner = string.IsNullOrWhiteSpace(owner) ? throw new ArgumentException("a placeholder names the section that builds its page", nameof(owner)) : owner;
        ShowEmpty($"{title} is not built yet.", $"It arrives with {Owner} of the plan.");
    }

    /// <summary>The section of the plan that builds this page, as the plan writes it ("D03 T01 §5").</summary>
    public string Owner { get; }
}
