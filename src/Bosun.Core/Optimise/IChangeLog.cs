namespace Bosun.Core.Optimise;

public enum ChangeLevel
{
    Info,
    Warn,
    Fail,
}

/// <summary>One step of a change as the log is told it.</summary>
/// <param name="ItemId">The item's id.</param>
/// <param name="Title">The item's title, for the person.</param>
/// <param name="Step">What was done: Refused, Backup, Write, Verify or Result.</param>
/// <param name="Target">The exact setting.</param>
/// <param name="Level">Info for a step that went as intended, Warn for a refusal that changed nothing, Fail for a failure.</param>
/// <param name="Outcome">What happened, in words.</param>
/// <param name="Backup">The name of the backup of this change, when there is one.</param>
public sealed record ChangeStep(string ItemId, string Title, string Step, string Target, ChangeLevel Level, string Outcome, string? Backup);

/// <summary>
/// Where <see cref="ChangeRunner"/> reports each step. <c>Bosun.Core</c> has no Activity pane: the panel adapts this to
/// its own (<c>ActivityChangeLog</c>).
/// </summary>
public interface IChangeLog
{
    void Step(ChangeStep step);
}
