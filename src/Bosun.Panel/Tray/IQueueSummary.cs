namespace Bosun.Panel.Tray;

/// <summary>The queue in a few words, for the tray's menu (D02 T02 §7). The Queue page supplies the real one (D03 T02 §3, <c>QueueSummary</c>).</summary>
public interface IQueueSummary
{
    /// <summary>The queue in words ("1 running, 2 waiting"), or null when it can't be read.</summary>
    string? Words { get; }

    /// <summary>
    /// The tray's menu is opening: the summary may read the queue once, never on a timer (design/SPEC.md §1). The menu
    /// shows <see cref="Words"/> as they are now; a read this starts gives the words the next opening shows.
    /// </summary>
    void MenuOpening();
}
