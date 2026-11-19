namespace Bosun.Core.Optimise;

/// <summary>How a change or an Undo ended, read from the target after the write and never from the item's own say.</summary>
public enum ChangeStatus
{
    /// <summary>Applied, verified and read again: the target holds the proposal's after value.</summary>
    Changed,

    /// <summary>Undone, verified and read again: the target holds the backed-up value.</summary>
    Restored,

    /// <summary>The target reads the value it held before this operation. Nothing was done, or what was written did not stay.</summary>
    NotChanged,

    /// <summary>The target could not be read, reads neither value, or the item's own verification failed. See <see cref="ChangeResult.Message"/>.</summary>
    CouldNotVerify,
}

/// <summary>
/// The outcome of <see cref="ChangeRunner.Apply"/> or <see cref="ChangeRunner.Undo"/>.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Message">The outcome in words, "Undo is offered" included whenever <see cref="UndoOffered"/>.</param>
/// <param name="Backup">The exact backup of this operation: what reverses it. Null when nothing was written.</param>
/// <param name="Written">True when a write may have happened: false only for a refusal before any write.</param>
/// <param name="ValueNow">What the target read after the operation, when it could be read.</param>
/// <param name="UndoWithheld">True when a write may have happened but an Undo would be refused (Bosun has no evidence that the value the target holds is its own): the message then says where the original value is kept and how to get it back.</param>
public sealed record ChangeResult(ChangeStatus Status, string Message, BackupRef? Backup, bool Written, string? ValueNow, bool UndoWithheld = false)
{
    /// <summary>True whenever a write may have happened, its backup exists and <see cref="ChangeRunner.CanUndo"/> will accept it: the person can get back.</summary>
    public bool UndoOffered => Written && Backup is not null && !UndoWithheld;
}

/// <summary>
/// Whether a change or an Undo can run now, and when it can't, why: one answer for the row and the palette alike.
/// <see cref="NeedsAdministrator"/> tells "ready after an administrator prompt" from a refusal (D05 T02 §6): the
/// change is otherwise eligible, <see cref="Allowed"/> is false only because this runner is not elevated, and
/// <see cref="Ready"/> is true for either.
/// </summary>
public sealed record Eligibility(bool Allowed, string? Reason, bool NeedsAdministrator = false)
{
    public static Eligibility Yes { get; } = new(true, null);

    /// <summary>Eligible, once the person accepts the administrator prompt.</summary>
    public static Eligibility AdministratorPrompt { get; } = new(false, "Needs an administrator prompt", NeedsAdministrator: true);

    public static Eligibility No(string reason) => new(false, reason);

    /// <summary>True when the change can go ahead, now or after the administrator prompt; false for a refusal.</summary>
    public bool Ready => Allowed || NeedsAdministrator;
}

/// <summary>
/// The only way a machine setting is changed (D05 T02 §2): a confirmed proposal, a read-back backup bound to its
/// target, the write, the item's verification, and the target read again; every step reported to the
/// <see cref="IChangeLog"/>. It trusts nothing an item returns after a write: the outcome is what the target reads.
/// </summary>
/// <param name="backups">Where backups are kept.</param>
/// <param name="log">Where each step is said.</param>
/// <param name="elevated">True only in the elevated helper (D05 T02 §6): an item that needs elevation is refused otherwise.</param>
public sealed class ChangeRunner(BackupStore backups, IChangeLog log, bool elevated = false)
{
    /// <summary>What the item would change now. The confirmation the person gives is for this proposal, whole.</summary>
    public Fact<Proposal> Preview(IOptimisation item)
    {
        var proposal = Propose(item);
        if (proposal.IsRead) Say(item, "Preview", ChangeLevel.Info, $"Would change '{proposal.Value!.Before}' to '{proposal.Value.After}'", null);
        else Say(item, "Preview", ChangeLevel.Warn, proposal.Reason!, null);
        return proposal;
    }

    /// <summary>
    /// Whether <paramref name="confirmed"/> can be applied now: reads the target again, so the answer is the one
    /// <see cref="Apply"/> will reach. Null is no preview yet.
    /// </summary>
    public Eligibility CanApply(IOptimisation item, Proposal? confirmed)
    {
        if (confirmed is null) return Eligibility.No("Preview it first");
        var fresh = Propose(item);
        if (!fresh.IsRead) return Eligibility.No($"The setting could not be read: {fresh.Reason}");
        var now = fresh.Value!;
        // Edited: not the proposal the item makes now, whatever was shown. Compared before the before value so an
        // edited value is never mistaken for a setting that changed.
        if (confirmed.Target != item.Target || confirmed.Target != now.Target || confirmed.After != now.After)
            return Eligibility.No("That is not what this item proposes now. Preview it again");
        if (confirmed.Before != now.Before) return Eligibility.No("The setting changed since the preview");
        if (confirmed.Before == confirmed.After) return Eligibility.No("Nothing to change: the setting already holds that value");
        return NeedsAdministrator(item) ?? Eligibility.Yes;
    }

    /// <summary>
    /// Whether the change <paramref name="backup"/> belongs to can be undone now. Null is no change to undo. The
    /// backup is read (well formed, this item, this target) and the target must still hold what the change wrote.
    /// </summary>
    public Eligibility CanUndo(IOptimisation item, BackupRef? backup) => CheckUndo(item, backup, out _, out _);

    /// <summary>Changes the setting to <paramref name="confirmed"/>, or refuses and changes nothing.</summary>
    public ChangeResult Apply(IOptimisation item, Proposal confirmed)
    {
        var can = CanApply(item, confirmed);
        if (!can.Allowed) return Refuse(item, confirmed?.Target ?? item.Target, can.Reason!);

        BackupRef backup;
        try
        {
            backup = backups.Write(item.Id, BackupStore.Apply, confirmed);
        }
        catch (BackupException e)
        {
            return Refuse(item, confirmed.Target, $"Not changed: {e.Message}");
        }
        Say(item, "Backup", ChangeLevel.Info, "Written and read back", backup.Name, confirmed.Target);

        // The target is read again as close to the write as the runner can: a value that moved since the person
        // confirmed it (while the backup was being kept) is a newer value, and nothing is written over it.
        var beforeWrite = ReadGuarded(item);
        if (!beforeWrite.IsRead || beforeWrite.Value != confirmed.Before)
            return Refuse(item, confirmed.Target, ChangedWhileApplying(beforeWrite));

        Exception? error = null;
        try
        {
            item.Apply(confirmed.Before, confirmed.After);
        }
        catch (ChangeConflictException)
        {
            return Refuse(item, confirmed.Target, ChangedWhileApplying(null));
        }
        catch (Exception e)
        {
            error = e;
            Say(item, "Write", ChangeLevel.Fail, $"The write reported an error: {e.Message}", backup.Name, confirmed.Target);
        }
        Wrote(item, backup, confirmed.Target, error is null ? "Apply returned" : null);
        return Finish(item, confirmed, backup, error, ChangeStatus.Changed, "changed");
    }

    /// <summary>
    /// Restores the value <paramref name="backup"/> holds, the backup of the change being reversed, never "the
    /// newest". The Undo is itself backed up first, so it can be undone. When <paramref name="confirmedRestore"/> is given
    /// it is the value the person was shown that this Undo restores, compared with the very backup record read here for
    /// the restore: any other value is refused with nothing written.
    /// </summary>
    public ChangeResult Undo(IOptimisation item, BackupRef backup, string? confirmedRestore = null)
    {
        var can = CheckUndo(item, backup, out var record, out var validated, confirmedRestore);
        if (!can.Allowed) return Refuse(item, item.Target, can.Reason!);
        var current = validated!; // the value CheckUndo read and accepted: never read again unguarded

        var undo = new Proposal(item.Target, current, record!.Before);
        BackupRef undoBackup;
        try
        {
            undoBackup = backups.Write(item.Id, BackupStore.Undo, undo);
        }
        catch (BackupException e)
        {
            return Refuse(item, item.Target, $"Not restored: {e.Message}");
        }
        Say(item, "Backup", ChangeLevel.Info, "Written and read back", undoBackup.Name, item.Target);

        var beforeWrite = ReadGuarded(item);
        if (!beforeWrite.IsRead || beforeWrite.Value != current)
            return Refuse(item, item.Target, ChangedWhileApplying(beforeWrite, "restored"));

        Exception? error = null;
        try
        {
            item.Restore(current, record.Before);
        }
        catch (ChangeConflictException)
        {
            return Refuse(item, item.Target, ChangedWhileApplying(null, "restored"));
        }
        catch (Exception e)
        {
            error = e;
            Say(item, "Write", ChangeLevel.Fail, $"The restore reported an error: {e.Message}", undoBackup.Name, item.Target);
        }
        Wrote(item, undoBackup, item.Target, error is null ? "Restore returned" : null);
        return Finish(item, undo, undoBackup, error, ChangeStatus.Restored, "restored");
    }

    private Eligibility CheckUndo(IOptimisation item, BackupRef? backup, out Backup? record, out string? validated, string? confirmedRestore = null)
    {
        record = null;
        validated = null;
        if (backup is null) return Eligibility.No("Nothing to undo");
        if (backup.ItemId != item.Id) return Eligibility.No("That backup belongs to another item");
        try
        {
            record = backups.Read(backup);
        }
        catch (BackupException e)
        {
            return Eligibility.No($"The backup can't be trusted: {e.Message}");
        }
        if (record.Target != item.Target) return Eligibility.No("That backup is for another setting");
        if (confirmedRestore is not null && record.Before != confirmedRestore)
            return Eligibility.No($"Not restored: this backup restores '{record.Before}', not the '{confirmedRestore}' that was confirmed");
        var now = ReadGuarded(item);
        if (!now.IsRead) return Eligibility.No($"The setting could not be read: {now.Reason}");
        // Already holding the value this backup would restore: a prepared backup, or a write that did not stay.
        if (now.Value == record.Proposal.Before && now.Value != record.Proposal.After) return Eligibility.No("The setting already holds the value this backup would restore");
        // The target must still hold what the change wrote, or what a partly failed write was recorded as leaving
        // (never a value the person or another program set since): anything else is a newer value an Undo would destroy.
        if (now.Value != record.Proposal.After && now.Value != backups.ReadOutcome(record))
            return Eligibility.No("The setting changed since this change, so undoing it would overwrite a newer value");
        validated = now.Value;
        return NeedsAdministrator(item) ?? Eligibility.Yes;
    }

    private Eligibility? NeedsAdministrator(IOptimisation item) =>
        item.NeedsElevation && !elevated ? Eligibility.AdministratorPrompt : null;

    /// <summary>
    /// After a write that may have happened: ask the item to verify, read the target again, and say what is true.
    /// The target's own value decides, never the item's return or its silence.
    /// </summary>
    private ChangeResult Finish(IOptimisation item, Proposal done, BackupRef backup, Exception? error, ChangeStatus success, string verb)
    {
        Fact<string> verified;
        try
        {
            verified = item.Verify(done);
        }
        catch (Exception e)
        {
            verified = Fact<string>.Unavailable($"verification failed: {e.Message}");
        }
        var now = ReadGuarded(item);
        var verifiedOk = verified.IsRead && verified.Value == done.After;

        // A value that is neither the before nor the after is Bosun's own partial write only when the item's Verify, run
        // right after the write, read that same value. Anything else (Verify read the after value and the target then
        // moved, or Verify read nothing useful) could be another program's edit: no outcome is recorded for it, so Undo
        // refuses to overwrite it.
        var third = now.IsRead && now.Value != done.After && now.Value != done.Before;
        var changedAgain = third && verifiedOk;
        var noEvidence = third && !changedAgain && !(verified.IsRead && verified.Value == now.Value);
        var outcomeLost = third && !changedAgain && !noEvidence && !KeepOutcome(item, backup, now.Value!);
        // Nothing was changed: the setting still reads the value its backup holds, so there is nothing to undo (round 11).
        var unchanged = !(now.IsRead && now.Value == done.After) && now.IsRead && now.Value == done.Before;
        var withheld = changedAgain || noEvidence || outcomeLost || unchanged;
        var original = success == ChangeStatus.Changed ? "original value" : "value before this undo";

        var holdsNew = now.IsRead && now.Value == done.After;
        ChangeStatus status;
        string words;
        if (error is null && verifiedOk && holdsNew)
        {
            status = success;
            words = $"{char.ToUpperInvariant(verb[0])}{verb[1..]}: '{done.Before}' to '{done.After}', verified by reading it back";
        }
        else if (holdsNew)
        {
            status = ChangeStatus.CouldNotVerify;
            var why = error is not null ? "the operation reported an error" : verified.IsRead ? $"its verification read '{verified.Value}'" : $"verification failed ({verified.Reason})";
            words = $"Could not be verified: it now reads '{done.After}' but {why}";
        }
        else if (now.IsRead && now.Value == done.Before)
        {
            status = ChangeStatus.NotChanged;
            words = $"Not {verb}: it still reads '{done.Before}'" + (error is not null ? $" ({error.Message})" : "");
        }
        else if (changedAgain)
        {
            status = ChangeStatus.CouldNotVerify;
            words = $"{char.ToUpperInvariant(verb[0])}{verb[1..]}: '{done.Before}' to '{done.After}', then changed again since: now '{now.Value}'";
        }
        else
        {
            status = ChangeStatus.CouldNotVerify;
            words = now.IsRead ? $"Could not be verified: it reads '{now.Value}', neither '{done.Before}' nor '{done.After}'" : $"Could not be verified: it could not be read afterwards ({now.Reason})";
        }

        var recovery = changedAgain
            ? $"Undo is not offered, because it would overwrite the newer value '{now.Value}'; the {original} '{done.Before}' is kept in backup {backup.Name}"
            : noEvidence
                ? $"Bosun cannot tell its own partial write from a later edit, so Undo is not offered; the {original} '{done.Before}' is kept in backup {backup.Name}; restore it by hand"
                : outcomeLost
                    ? $"Bosun could not record what the write left; the {original} '{done.Before}' is kept in backup {backup.Name}; restore it by hand or apply again once the setting is read"
                    : unchanged
                        ? $"There is nothing to undo; the backup {backup.Name} is kept"
                        : $"Undo is offered from backup {backup.Name}";
        var message = $"{words}. {recovery}.";
        Say(item, "Verify", status == success ? ChangeLevel.Info : ChangeLevel.Fail, verifiedOk ? "The item read the new value back" : "The item's own verification did not confirm it", backup.Name, done.Target);
        Say(item, "Result", status == success ? ChangeLevel.Info : status == ChangeStatus.NotChanged ? ChangeLevel.Warn : ChangeLevel.Fail, message, backup.Name, done.Target);
        return new ChangeResult(status, message, backup, Written: true, now.IsRead ? now.Value : null, UndoWithheld: withheld);
    }

    /// <summary>
    /// The item's Apply or Restore has run (<paramref name="returned"/> says what, when it returned instead of throwing):
    /// say so before verification starts, and keep the durable applied marker a restarted process reads.
    /// </summary>
    private void Wrote(IOptimisation item, BackupRef backup, string target, string? returned)
    {
        if (returned is not null) Say(item, "Write returned", ChangeLevel.Info, returned, backup.Name, target);
        try
        {
            backups.MarkApplied(backups.Read(backup));
        }
        catch (BackupException e)
        {
            Say(item, "Applied marker", ChangeLevel.Fail, $"Could not record that the write ran: {e.Message}", backup.Name, target);
        }
    }

    /// <summary>
    /// A write left the target on a value that is neither the before nor the after (a partial write), and the item's own
    /// Verify read that value. It is kept beside the backup so an Undo can tell it from a later edit. False when it could not be kept.
    /// </summary>
    private bool KeepOutcome(IOptimisation item, BackupRef backup, string observed)
    {
        try
        {
            backups.WriteOutcome(backups.Read(backup), observed);
            Say(item, "Outcome", ChangeLevel.Info, $"Recorded that the setting read '{observed}' afterwards", backup.Name);
            return true;
        }
        catch (BackupException e)
        {
            Say(item, "Outcome", ChangeLevel.Fail, $"The value the setting was left on could not be recorded: {e.Message}", backup.Name);
            return false;
        }
    }

    private static string ChangedWhileApplying(Fact<string>? read, string verb = "changed") =>
        read is { IsRead: false }
            ? $"Not {verb}: the setting could not be read just before the write ({read.Reason}): nothing was written"
            : $"Not {verb}: The setting changed while it was being applied: nothing was written";

    /// <summary>Every read goes through here: a read that fails is a reason and a log line, never an exception that escapes.</summary>
    private Fact<string> ReadGuarded(IOptimisation item)
    {
        try
        {
            return item.Read();
        }
        catch (Exception e)
        {
            Say(item, "Read", ChangeLevel.Warn, $"The setting could not be read: {e.Message}", null);
            return Fact<string>.Unavailable(e.Message);
        }
    }

    private ChangeResult Refuse(IOptimisation item, string target, string reason)
    {
        var message = reason.StartsWith("Not ", StringComparison.Ordinal) ? reason : $"Not changed: {reason}";
        Say(item, "Refused", ChangeLevel.Warn, message, null, target);
        return new ChangeResult(ChangeStatus.NotChanged, message, null, Written: false, null);
    }

    private static Fact<Proposal> Propose(IOptimisation item)
    {
        try
        {
            return item.Propose();
        }
        catch (Exception e)
        {
            return Fact<Proposal>.Unavailable(e.Message);
        }
    }

    private void Say(IOptimisation item, string step, ChangeLevel level, string outcome, string? backup, string? target = null) =>
        log.Step(new ChangeStep(item.Id, item.Title, step, target ?? item.Target, level, outcome, backup));
}
