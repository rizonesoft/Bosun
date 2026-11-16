using System.Globalization;
using Bosun.Core;
using Bosun.Panel.Shell;

namespace Bosun.Panel.Tray;

/// <summary>How a notification ended.</summary>
public enum Delivery
{
    Shown,

    /// <summary>The person turned this kind off.</summary>
    Off,

    /// <summary>The person paused notifications for an hour.</summary>
    Paused,

    /// <summary>Windows did not show it (no icon to raise it from, or the shell refused).</summary>
    Refused,
}

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

    /// <summary>Pauses notifications for an hour from now. An action like any other: it is a line in the Activity pane.</summary>
    public void PauseForAnHour()
    {
        var until = _clock() + PauseLength;
        _pausedUntil = until;
        log.Info(string.Create(CultureInfo.InvariantCulture, $"Pause notifications until {until:HH:mm}"));
    }

    /// <summary>Ends the pause, and says so in the Activity pane.</summary>
    public void Resume()
    {
        _pausedUntil = null;
        log.Info("Resume notifications");
    }

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
    public bool Raise(NotificationType type, string title, string text) => Deliver(type, title, text) == Delivery.Shown;

    /// <summary>
    /// As <see cref="Raise"/>, saying which way it ended: shown, turned off, paused, or refused by Windows (the one a
    /// caller may try again, D03 T01 §4). <paramref name="quietly"/> writes no line for a try that was refused again,
    /// so a retry every check does not fill the Activity pane; a shown one is always written.
    /// </summary>
    public Delivery Deliver(NotificationType type, string title, string text, bool quietly = false)
    {
        string? why = null;
        var delivery = Delivery.Shown;
        if (!IsOn(type)) (why, delivery) = ($"{Name(type)} notifications are turned off", Delivery.Off);
        else if (PausedUntil is { } until) (why, delivery) = (string.Create(CultureInfo.InvariantCulture, $"notifications are paused until {until:HH:mm}"), Delivery.Paused);
        else if (!Show(title, text)) (why, delivery) = ("Windows did not show it", Delivery.Refused);

        if (why is null) log.Warn($"{title}: {text}");
        else if (!quietly) log.Warn($"{title}: {text} (not notified: {why})");
        return delivery;
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
