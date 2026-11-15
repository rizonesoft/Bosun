using System.Collections.ObjectModel;
using Bosun.Panel.Mvvm;

namespace Bosun.Panel.Shell;

/// <summary>
/// The panel's record of what it did (D02 T02 §4, design/SPEC.md §1): every line starts with the word INFO, WARN or
/// FAIL, and every panel action writes one. It is also the only place a command is made, so no action can be added
/// that does not write its line: <see cref="Command"/> is the way to make one.
/// </summary>
public sealed class ActivityLog(UiLogSink sink)
{
    public ObservableCollection<LogEntry> Entries => sink.Entries;

    public void Info(string message) => sink.Append(LogSeverity.Info, message);

    public void Warn(string message) => sink.Append(LogSeverity.Warn, message);

    public void Fail(string message) => sink.Append(LogSeverity.Fail, message);

    /// <summary>
    /// Adds a line that carries its own word: "WARN the queue file is damaged". A line that does not start with
    /// INFO, WARN or FAIL and a space is refused, with nothing added.
    /// </summary>
    public void Write(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        foreach (var severity in Enum.GetValues<LogSeverity>())
        {
            var word = new LogEntry(default, severity, "x").Tag + " ";
            if (!line.StartsWith(word, StringComparison.Ordinal)) continue;
            sink.Append(severity, line[word.Length..]);
            return;
        }
        throw new ArgumentException("an activity line starts with INFO, WARN or FAIL and a space", nameof(line));
    }

    /// <summary>
    /// A command for a panel action. When the action is done it writes an INFO line naming it; when it failed, or
    /// threw, a FAIL line with the reason; and it can run again either way.
    /// </summary>
    /// <param name="name">What the action is, as the line will say it: "Save queue limits".</param>
    /// <param name="action">The action, returning how it ended.</param>
    public AsyncCommand Command(string name, Func<Task<ActionOutcome>> action, Func<bool>? canExecute = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a command's line needs the action's name", nameof(name));
        return new AsyncCommand(async () => Record(name, await action()), thrown => Fail($"{name}: {thrown.Message}"), canExecute);
    }

    /// <summary>Writes the line for an action that ended: INFO when done, FAIL with the reason when it failed.</summary>
    public void Record(string name, ActionOutcome outcome)
    {
        if (!outcome.Succeeded) Fail($"{name}: {outcome.Reason}");
        else Info(outcome.Reason is null ? name : $"{name}: {outcome.Reason}");
    }
}
