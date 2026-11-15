using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;

namespace Bosun.Panel.Shell;

/// <summary>How a line of the Activity pane reads: each has its word, so the state is never told by colour alone.</summary>
public enum LogSeverity
{
    Info,
    Warn,
    Fail,
}

/// <summary>One line of the Activity pane.</summary>
public sealed record LogEntry(DateTime At, LogSeverity Severity, string Message)
{
    public string Time => At.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>The word the line starts with (design/SPEC.md §1): INFO, WARN or FAIL.</summary>
    public string Tag => Severity switch
    {
        LogSeverity.Fail => "FAIL",
        LogSeverity.Warn => "WARN",
        _ => "INFO",
    };
}

/// <summary>
/// The lines the window's Activity pane shows (D02 T02 §2, after Steward's UiLogSink): a collection a list binds to,
/// appended to from any thread and changed only on the UI thread, holding the newest <see cref="MaxEntries"/>.
///
/// A message is one line of at most <see cref="MaxMessageLength"/> characters: a tool's own message is quoted into
/// it, and a tool can say anything.
/// </summary>
public sealed class UiLogSink(Dispatcher dispatcher, Func<DateTime>? clock = null)
{
    /// <summary>design/SPEC.md §1: 500 entries at most. A log that grows without bound stops scrolling smoothly, and nobody reads the ten-thousandth line.</summary>
    public const int MaxEntries = 500;

    public const int MaxMessageLength = 2000;

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>Adds a line, from any thread. The time is when it was said, not when the UI thread got to it.</summary>
    public void Append(LogSeverity severity, string message)
    {
        var line = OneLine(message);
        if (line.Length == 0) throw new ArgumentException("a log line needs words", nameof(message));
        var entry = new LogEntry((clock ?? (() => DateTime.Now))(), severity, line);
        if (dispatcher.CheckAccess()) Add(entry);
        // Posted, not awaited: whoever logs carries on, and what goes wrong on the UI thread is the UI thread's to report.
        else dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Add(entry));
    }

    private void Add(LogEntry entry)
    {
        Entries.Add(entry);
        while (Entries.Count > MaxEntries) Entries.RemoveAt(0);
    }

    /// <summary>The message as one line: line breaks and tabs become single spaces, and a long one is cut with an ellipsis.</summary>
    public static string OneLine(string message)
    {
        var words = string.Join(' ', (message ?? "").Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return words.Length <= MaxMessageLength ? words : words[..(MaxMessageLength - 1)] + "…";
    }
}
