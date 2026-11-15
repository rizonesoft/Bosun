namespace Bosun.Panel.Tray;

/// <summary>The queue in a few words, for the tray's menu (D02 T02 §7). The Queue page supplies the real one (D03 T02 §3).</summary>
public interface IQueueSummary
{
    /// <summary>The queue in words ("1 running, 2 waiting"), or null when it can't be read.</summary>
    string? Words { get; }
}
