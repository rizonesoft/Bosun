using System.Globalization;
using Bosun.Core;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Tray;

/// <summary>The kinds of notification the panel raises (design/SPEC.md §17: memory pressure).</summary>
public enum NotificationType
{
    MemoryPressure,
}

/// <summary>Where a notification is shown: the tray icon's own, which Windows shows as a toast. False when it could not be shown.</summary>
public interface INotificationSink
{
    bool Notify(string title, string text);
}

/// <summary>
/// The one way a notification is raised (D02 T02 §7, design/SPEC.md §15 and §17). A type that is turned off in
/// Settings raises nothing; a pause of one hour, from the tray's menu, raises nothing until it is up. Every
/// notification, shown or not, is a line in the Activity pane, so one that was not seen loses nothing.
/// </summary>
public sealed class Notifier(PanelSettingsStore settings, ActivityLog log, Func<DateTime>? clock = null)
{
    public static readonly TimeSpan PauseLength = TimeSpan.FromHours(1);

    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.Now);
    private DateTime? _pausedUntil;

    /// <summary>Where notifications are shown; null until there is a tray icon, and then nothing can be shown.</summary>
    public INotificationSink? Sink { get; set; }

    /// <summary>When the pause ends; null when notifications are not paused, or the pause has run out.</summary>
    public DateTime? PausedUntil => _pausedUntil is { } until && until > _clock() ? until : null;

    /// <summary>Pauses notifications for an hour from now.</summary>
    public void PauseForAnHour() => _pausedUntil = _clock() + PauseLength;

    public void Resume() => _pausedUntil = null;

    /// <summary>Whether a type is turned on in Settings.</summary>
    public bool IsOn(NotificationType type) => type switch
    {
        NotificationType.MemoryPressure => settings.Current.NotifyMemoryPressure,
        _ => true,
    };

    /// <summary>
    /// Raises a notification of a type. True when it was shown. Turned off, paused or unshowable, it is not shown,
    /// and the Activity pane's line says which.
    /// </summary>
    public bool Raise(NotificationType type, string title, string text)
    {
        string? why = null;
        if (!IsOn(type)) why = $"{Name(type)} notifications are turned off";
        else if (PausedUntil is { } until) why = string.Create(CultureInfo.InvariantCulture, $"notifications are paused until {until:HH:mm}");
        else if (!Show(title, text)) why = "Windows did not show it";

        log.Warn(why is null ? $"{title}: {text}" : $"{title}: {text} (not notified: {why})");
        return why is null;
    }

    /// <summary>
    /// Says something the person must be told whatever their notification settings: not an alert about the machine,
    /// an explanation of what the panel itself just did. True when it was shown.
    /// </summary>
    public bool Explain(string title, string text)
    {
        var shown = Show(title, text);
        if (shown) log.Info($"{title}: {text}");
        return shown;
    }

    private bool Show(string title, string text) => Sink is { } sink && sink.Notify(title, text);

    private static string Name(NotificationType type) => type switch
    {
        NotificationType.MemoryPressure => "memory pressure",
        _ => type.ToString(),
    };
}
