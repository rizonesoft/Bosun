namespace Bosun.Core;

/// <summary>Which theme the panel draws in (design/SPEC.md §1).</summary>
public enum ThemeChoice
{
    /// <summary>Light when Windows' apps are light, dark when they are dark, and it follows a change live. The default.</summary>
    FollowWindows,
    Dark,
    Light,
}

/// <summary>
/// The panel's settings (D02 T02 §5, design/SPEC.md §15): what `panel.json` holds. One immutable record: a change is
/// a new record, saved whole. Each setting is added by the section that builds the code that reads it, so nothing
/// here is a setting nothing reads.
/// </summary>
public sealed record PanelSettings
{
    public const double MinActivityPaneHeight = 64, MaxActivityPaneHeight = 400, DefaultActivityPaneHeight = 132;

    private readonly double _activityPaneHeight = DefaultActivityPaneHeight;

    /// <summary>What the panel is with no file: the defaults.</summary>
    public static PanelSettings Defaults { get; } = new();

    /// <summary>Read by the theme service (src/Bosun.Panel/Theme/ThemeService.cs).</summary>
    public ThemeChoice Theme { get; init; } = ThemeChoice.FollowWindows;

    /// <summary>
    /// Whether memory pressure raises a notification (design/SPEC.md §15 and §17: notifications on or off by type,
    /// and memory pressure is the one type). Read by the notifier (src/Bosun.Panel/Tray/Notifier.cs). On by default.
    /// </summary>
    public bool NotifyMemoryPressure { get; init; } = true;

    /// <summary>
    /// Whether the panel has said, the first time its window was closed, that it is still running in the tray
    /// (design/SPEC.md §17). Read by the tray controller, so it is said once ever.
    /// </summary>
    public bool TrayNoticeSaid { get; init; }

    /// <summary>
    /// The Activity pane's height, read by the shell. Always within its limits: a height outside them is brought
    /// inside, and one that is not a number is the default.
    /// </summary>
    public double ActivityPaneHeight
    {
        get => _activityPaneHeight;
        init => _activityPaneHeight = double.IsNaN(value) ? DefaultActivityPaneHeight : Math.Clamp(value, MinActivityPaneHeight, MaxActivityPaneHeight);
    }
}
