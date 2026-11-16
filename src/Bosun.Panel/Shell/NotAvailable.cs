namespace Bosun.Panel.Shell;

/// <summary>
/// A value that could not be read, with why (design/SPEC.md §1: an unreadable value is "Not available", never 0). A
/// page gives one where it would give the value; the theme draws it as "Not available" with the reason under it
/// (Theme/Controls.xaml). A null is drawn as "Not available" too, with "It could not be read.", but a page that knows
/// why gives this.
/// </summary>
public sealed record NotAvailable
{
    public NotAvailable(string reason)
    {
        Reason = string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException("a value that can't be read says why", nameof(reason)) : reason.Trim();
    }

    public string Reason { get; }

    public override string ToString() => $"Not available: {Reason}";
}
