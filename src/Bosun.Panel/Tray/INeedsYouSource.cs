namespace Bosun.Panel.Tray;

/// <summary>
/// What needs the operator, for the tray's Needs you state (D02 T02 §7). The Overview page's Needs you table is the
/// real one (D03 T01 §5).
/// </summary>
public interface INeedsYouSource
{
    /// <summary>How many things need the operator; 0 when nothing does.</summary>
    int Count { get; }

    /// <summary>Raised when <see cref="Count"/> has changed. On the UI thread.</summary>
    event Action? Changed;
}
