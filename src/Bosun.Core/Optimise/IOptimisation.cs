namespace Bosun.Core.Optimise;

/// <summary>
/// What one change would do, as the person is shown it and confirms it: the exact target, its value as read now and
/// the value it would hold. Apply is bound to this whole record (D05 T02 §2), so a change is never applied to a
/// setting, or with a value, the person did not see.
/// </summary>
/// <param name="Target">The exact setting. For a startup entry its hive, view and name, so same-named entries differ.</param>
/// <param name="Before">The value read when the preview was made.</param>
/// <param name="After">The value the change would write.</param>
public sealed record Proposal(string Target, string Before, string After);

/// <summary>The target no longer reads the value a write expected, so nothing was written: a newer value is never overwritten.</summary>
public sealed class ChangeConflictException(string message) : Exception(message);

/// <summary>
/// One machine change the panel can make, undo and verify. An item is one setting at one exact target. It does the
/// raw work only: <see cref="ChangeRunner"/> owns the order (confirm, back up, apply, verify, read again) and never
/// trusts what an item says about itself.
/// </summary>
public interface IOptimisation
{
    /// <summary>A stable name for the item, a letter or digit first then letters, digits, dots, dashes and underscores: its backups live in a folder of this name.</summary>
    string Id { get; }

    string Title { get; }

    /// <summary>The exact setting this item changes (see <see cref="Proposal.Target"/>); a backup for another one is never restored.</summary>
    string Target { get; }

    /// <summary>True when only an administrator can change it: <see cref="ChangeRunner"/> then refuses unless it runs elevated.</summary>
    bool NeedsElevation { get; }

    /// <summary>The target's value now, or why it could not be read.</summary>
    Fact<string> Read();

    /// <summary>What the item would change, its before value as read now; or why it can't propose one.</summary>
    Fact<Proposal> Propose();

    /// <summary>
    /// Writes <paramref name="after"/> only if the target still reads <paramref name="expectedBefore"/>, checked as
    /// close to the write as the platform allows; else throws <see cref="ChangeConflictException"/> without writing.
    /// Throws when it fails; it may have written before it threw (never before a <see cref="ChangeConflictException"/>).
    /// </summary>
    void Apply(string expectedBefore, string after);

    /// <summary>Reads the setting back: the value it holds now, for the runner to compare with <see cref="Proposal.After"/>.</summary>
    Fact<string> Verify(Proposal proposal);

    /// <summary>
    /// Writes a backed-up <paramref name="value"/> only if the target still reads <paramref name="expectedCurrent"/>,
    /// checked as close to the write as the platform allows; else throws <see cref="ChangeConflictException"/> without
    /// writing. Throws when it fails; it may have written before it threw (never before a <see cref="ChangeConflictException"/>).
    /// </summary>
    void Restore(string expectedCurrent, string value);
}
