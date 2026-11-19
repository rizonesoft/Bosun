using System.Globalization;

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
/// Where the panel's window was when it was last hidden or closed (D02 T03 §12): its normal bounds in screen pixels,
/// the DPI of the screen they were on then, and whether it restores maximised. Fitted to the screens there are before
/// it is used (src/Bosun.Panel/Shell/WindowPlacement.cs).
/// </summary>
public sealed record WindowPlace(int Left, int Top, int Width, int Height, int Dpi, bool Maximised)
{
    public const int MinDpi = 48, MaxDpi = 960;

    /// <summary>What is wrong with it as a stored place, or null.</summary>
    public string? Fault() =>
        Width <= 0 || Height <= 0 ? "its window size is not more than zero"
        : Dpi is < MinDpi or > MaxDpi ? $"its window DPI is not from {MinDpi} to {MaxDpi}"
        : null;
}

/// <summary>
/// The panel's settings (D02 T02 §5, design/SPEC.md §15): what `panel.json` holds. One immutable record: a change is
/// a new record, saved whole. Each setting is added by the section that builds the code that reads it, so nothing
/// here is a setting nothing reads.
/// </summary>
public sealed record PanelSettings
{
    public const double MinActivityPaneHeight = 64, MaxActivityPaneHeight = 400, DefaultActivityPaneHeight = 132;

    /// <summary>The pressure alert threshold's limits and default, in MB of Windows' available memory (D03 T01 §4).</summary>
    public const long MinPressureFloorMb = 512, MaxPressureFloorMb = 65536, DefaultPressureFloorMb = 2048;

    /// <summary>The job limit to apply under pressure, in MB (D05 T02 §5): at least 1,024, and (in this file) at most a terabyte; the guest's own total is the tighter limit, known only where the guest is read.</summary>
    public const long MinJobLimitMb = 1024, MaxJobLimitMb = 1_048_576, DefaultJobLimitMb = 4096;

    private readonly double _activityPaneHeight = DefaultActivityPaneHeight;
    private readonly long _pressureFloorMb = DefaultPressureFloorMb;

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
    /// Where the window was last (D02 T03 §12), or null when it has never been placed: read by the window as it opens,
    /// so it opens there again, and kept each time it is hidden or closed.
    /// </summary>
    public WindowPlace? Window { get; init; }

    /// <summary>
    /// The pressure alert threshold (design/SPEC.md §15): a memory pressure episode starts when Windows' available memory
    /// falls under it. Read by the pressure check (src/Bosun.Panel/Tray/PressureCheck.cs) at every check, so a saved
    /// change takes effect with no restart. Always within its limits: a value outside them is brought inside.
    /// </summary>
    public long PressureFloorMb
    {
        get => _pressureFloorMb;
        init => _pressureFloorMb = Math.Clamp(value, MinPressureFloorMb, MaxPressureFloorMb);
    }

    /// <summary>
    /// Whether Bosun acts by itself under memory pressure (D05 T02 §5): when Windows' available memory falls under
    /// <see cref="PressureFloorMb"/>, the WSL logger lowers the memory limit of the queued jobs to
    /// <see cref="JobLimitMb"/>, and restores it after. Off by default: nothing acts until the owner turns it on. The
    /// logger can't read this file, so the panel writes it for the logger as <c>pressure-policy.json</c>
    /// (src/Bosun.Core/Memory/PressurePolicy.cs).
    /// </summary>
    public bool AutoThrottleJobs { get; init; }

    /// <summary>
    /// The memory limit queued jobs are held to under pressure, in MB. Not brought inside its limits like the other
    /// numbers: a value outside them is refused, with why, by <see cref="Fault"/> and the store's save.
    /// </summary>
    public long JobLimitMb { get; init; } = DefaultJobLimitMb;

    /// <summary>What is wrong with a job limit, or null. With <paramref name="guestTotalMb"/> known, one over the guest's total is wrong too.</summary>
    public static string? JobLimitFault(long limitMb, long? guestTotalMb = null) =>
        limitMb < MinJobLimitMb ? string.Create(CultureInfo.InvariantCulture, $"the job limit {limitMb:N0} MB is under the least allowed, {MinJobLimitMb:N0} MB")
        : limitMb > MaxJobLimitMb ? string.Create(CultureInfo.InvariantCulture, $"the job limit {limitMb:N0} MB is over the most allowed, {MaxJobLimitMb:N0} MB")
        : guestTotalMb is { } total && limitMb > total ? string.Create(CultureInfo.InvariantCulture, $"the job limit {limitMb:N0} MB is over the guest's total memory, {total:N0} MB")
        : null;

    /// <summary>What is wrong with these settings as something to save, or null.</summary>
    public string? Fault() => JobLimitFault(JobLimitMb);

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
