using System.Text.RegularExpressions;

namespace Bosun.Core.Optimise;

/// <summary>
/// The one list of optimisation items by id (D05 T02 §6): the panel and the elevated helper both resolve an item
/// from it, and the helper derives the item's target and current proposal from the item itself, never from what a
/// request says. A test registers a disposable item here.
/// </summary>
public sealed partial class OptimisationRegistry
{
    private readonly Dictionary<string, IOptimisation> _items = new(StringComparer.Ordinal);

    public IReadOnlyCollection<IOptimisation> Items => _items.Values;

    /// <summary>Adds an item. An id that is already registered, or that a backup folder can't be named after, is a programming error.</summary>
    public OptimisationRegistry Register(IOptimisation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsValidId(item.Id)) throw new ArgumentException($"'{item.Id}' is not an item id a backup folder can be named after", nameof(item));
        if (!_items.TryAdd(item.Id, item)) throw new ArgumentException($"an item with the id '{item.Id}' is already registered", nameof(item));
        return this;
    }

    /// <summary>The item with this id, or null when there is none (an id that is not a well formed id is none).</summary>
    public IOptimisation? Resolve(string? id) => id is not null && _items.TryGetValue(id, out var item) ? item : null;

    /// <summary>True when <paramref name="id"/> could be an item's id: the form a backup folder is named in.</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id) && !id.EndsWith('.');

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();
}
