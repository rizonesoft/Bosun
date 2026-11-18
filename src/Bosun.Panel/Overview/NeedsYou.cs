using Bosun.Panel.Tray;

namespace Bosun.Panel.Overview;

/// <summary>How soon a row of Needs you wants the person: the table lists the most urgent first.</summary>
public enum NeedsSeverity
{
    /// <summary>Act now: memory is under pressure.</summary>
    Urgent,

    /// <summary>Something has stopped telling the panel what it needs to know: a check that can't read.</summary>
    Warning,

    /// <summary>Worth a look when there is time.</summary>
    Notice,
}

/// <summary>
/// One thing that needs the person (D03 T01 §5, design/SPEC.md §2): its state word, what it is, where (the page that
/// owns it, by name), the section key its Open goes to, how urgent it is and when it was first seen. <see cref="Key"/>
/// names it within its provider across refreshes. <see cref="SaidByTray"/> marks a row the tray already says in its own
/// words (its Pressure state, its check problem), which the tray's count of other things does not count again.
/// </summary>
public sealed record NeedsYouRow(string Key, string State, string What, string Where, string Section, NeedsSeverity Severity, DateTimeOffset FirstSeen)
{
    public bool SaidByTray { get; init; }
}

/// <summary>
/// A page's part of Needs you (D03 T01 §5): its rows now, and when they change. A provider reads nothing on the
/// Overview's behalf: it says what its own source, which runs whatever page is shown, already knows.
/// </summary>
public interface INeedsYouProvider
{
    /// <summary>Its rows now, in any order; none when nothing of its own needs the person.</summary>
    IReadOnlyList<NeedsYouRow> Rows { get; }

    /// <summary>Whether it has looked yet: before then, "nothing needs you" is not known for its part.</summary>
    bool HasLooked { get; }

    /// <summary>Raised when <see cref="Rows"/> or <see cref="HasLooked"/> changed. On the UI thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Needs you (D03 T01 §5): every provider's rows gathered in one order, the most urgent first and, within a severity,
/// the oldest first, a row keeping its place and its first-seen time across refreshes for as long as its provider
/// keeps it. It is what the Overview's table lists, what each rail row's badge adds (<see cref="For"/>), and the tray's
/// <see cref="INeedsYouSource"/>: its <see cref="Count"/> is the rows the tray does not already say itself.
/// </summary>
public sealed class NeedsYou : INeedsYouSource
{
    private readonly List<INeedsYouProvider> _providers = [];
    private readonly Dictionary<(INeedsYouProvider, string), (DateTimeOffset FirstSeen, long Order)> _seen = [];
    private IReadOnlyList<NeedsYouRow> _rows = [];
    private long _order;
    private int _count;
    private bool _looked = true; // with no provider, nothing is left to look

    /// <summary>The providers gathered, in the order they were added.</summary>
    public IReadOnlyList<INeedsYouProvider> Providers => _providers;

    /// <summary>Every row, most urgent first, then oldest first, then in the order each was first gathered.</summary>
    public IReadOnlyList<NeedsYouRow> Rows => _rows;

    /// <summary>The rows the tray does not say in its own words: the tray's count of things that need the person.</summary>
    public int Count => _count;

    /// <summary>Whether every provider has looked: only then can an empty table say nothing needs the person.</summary>
    public bool HasLooked => _providers.All(p => p.HasLooked);

    /// <summary>Raised when the rows, the count or <see cref="HasLooked"/> changed. On the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Adds a provider, whose rows join the table, the badges and the tray's count from now on.</summary>
    public void Add(INeedsYouProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (_providers.Contains(provider)) throw new ArgumentException("this provider is already gathered", nameof(provider));
        _providers.Add(provider);
        provider.Changed += Gather;
        Gather();
    }

    /// <summary>How many rows go to a section's page: its rail badge's share of Needs you.</summary>
    public int For(string section) => _rows.Count(row => row.Section == section);

    private void Gather()
    {
        var gathered = new List<(NeedsYouRow Row, long Order)>();
        var present = new HashSet<(INeedsYouProvider, string)>();
        foreach (var provider in _providers)
        {
            foreach (var row in provider.Rows)
            {
                var key = (provider, row.Key);
                if (!present.Add(key)) continue; // a provider's key is one row
                // First seen as the provider first gave it, kept while the row stays: a refresh never moves it.
                if (!_seen.TryGetValue(key, out var seen)) _seen[key] = seen = (row.FirstSeen, ++_order);
                gathered.Add((row with { FirstSeen = seen.FirstSeen }, seen.Order));
            }
        }
        // A row its provider dropped is forgotten: back later, it is new again.
        foreach (var gone in _seen.Keys.Where(key => !present.Contains(key)).ToList()) _seen.Remove(gone);
        var rows = gathered.OrderBy(g => g.Row.Severity).ThenBy(g => g.Row.FirstSeen).ThenBy(g => g.Order).Select(g => g.Row).ToList();
        var count = rows.Count(row => !row.SaidByTray);
        var looked = HasLooked;
        if (rows.SequenceEqual(_rows) && count == _count && looked == _looked) return;
        _rows = rows;
        _count = count;
        _looked = looked;
        Changed?.Invoke();
    }
}
