namespace Bosun.Core.Optimise;

/// <summary>
/// The shared path for a change or an Undo from the row or the palette (D05 T02 §6). An action that needs no
/// administrator goes straight through <see cref="ChangeRunner"/>. One whose eligibility is
/// <see cref="Eligibility.NeedsAdministrator"/> is written down as a request, handed to <see cref="IElevation"/>
/// exactly once, and tracked off the caller's thread until the helper exits; only then is its result read, and only a
/// whole result bound to this invocation is accepted. A stale or unconfirmed action never reaches the prompt. When the
/// helper is lost or its result can't be trusted, what happened is said, with recovery only where D05 T02 §2's Undo
/// rules allow it: by the exact backup the helper's own result names, with the target read again.
/// </summary>
/// <param name="runner">The panel's own runner: never elevated, so an item that needs an administrator answers <see cref="Eligibility.AdministratorPrompt"/>.</param>
/// <param name="backups">The panel's backups, which the helper writes into too.</param>
/// <param name="log">Where each step is said, the helper's own steps included once it has exited.</param>
/// <param name="elevation">Starts the helper.</param>
/// <param name="files">The panel's helper request and result files.</param>
/// <param name="initiatorSid">The SID of the account this panel runs as.</param>
/// <param name="dataFolder">This panel's data folder.</param>
public sealed class ChangeCoordinator(ChangeRunner runner, BackupStore backups, IChangeLog log, IElevation elevation, HelperFiles files, string initiatorSid, string dataFolder)
{
    public Eligibility CanApply(IOptimisation item, Proposal? confirmed) => runner.CanApply(item, confirmed);

    public Eligibility CanUndo(IOptimisation item, BackupRef? backup) => runner.CanUndo(item, backup);

    /// <summary>Applies the confirmed proposal: directly, or through the administrator prompt and the helper.</summary>
    public async Task<ChangeResult> ApplyAsync(IOptimisation item, Proposal? confirmed, CancellationToken cancellationToken = default)
    {
        var can = runner.CanApply(item, confirmed);
        if (!can.NeedsAdministrator) return runner.Apply(item, confirmed!); // runs, or refuses and says why: never prompts
        var request = new HelperRequest(HelperFiles.NewInvocation(), HelperFiles.Apply, item.Id, initiatorSid, dataFolder, item.Target, confirmed, null, null);
        return await ElevatedAsync(item, request, cancellationToken);
    }

    /// <summary>
    /// Undoes the change <paramref name="backup"/> belongs to. <paramref name="confirmedRestoreValue"/> is the value the
    /// person was shown that the Undo restores: the helper refuses a backup that holds any other.
    /// </summary>
    public async Task<ChangeResult> UndoAsync(IOptimisation item, BackupRef? backup, string confirmedRestoreValue, CancellationToken cancellationToken = default)
    {
        var can = runner.CanUndo(item, backup);
        if (!can.NeedsAdministrator)
        {
            // The confirmed restore value is checked by the runner against the backup record it reads for the restore.
            return runner.Undo(item, backup!, confirmedRestoreValue);
        }
        var request = new HelperRequest(HelperFiles.NewInvocation(), HelperFiles.Undo, item.Id, initiatorSid, dataFolder, item.Target, null, backup!.Name, confirmedRestoreValue);
        return await ElevatedAsync(item, request, cancellationToken);
    }

    private async Task<ChangeResult> ElevatedAsync(IOptimisation item, HelperRequest request, CancellationToken cancellationToken)
    {
        var verb = request.Operation == HelperFiles.Apply ? "changed" : "restored";
        try
        {
            files.WriteRequest(request);
        }
        catch (HelperProtocolException e)
        {
            return Refused(item, $"Not {verb}: {e.Message}");
        }

        if (cancellationToken.IsCancellationRequested) return CancelledBeforePrompt(item, request);

        // What the target holds before the helper can touch it: the value an unchanged target still reads afterwards.
        var origin = request.Proposal?.Before ?? ReadNow(item);
        Say(item, "Administrator", ChangeLevel.Info, "Waiting for the administrator prompt", null);
        ElevationStart start;
        try
        {
            start = await elevation.StartAsync(new HelperCommand(request.Operation, request.ItemId, request.Invocation), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return CancelledBeforePrompt(item, request);
        }
        switch (start.Kind)
        {
            case ElevationStartKind.Declined:
                files.DeleteRequest(request.Invocation);   // no helper started: the request is of no use (round 2)
                return Refused(item, $"Not {verb}: you declined the administrator prompt");
            case ElevationStartKind.Failed:
                files.DeleteRequest(request.Invocation);
                return Refused(item, $"Not {verb}: the administrator helper could not be started: {start.Reason}");
        }

        using var process = start.Process!;
        Say(item, "Administrator", ChangeLevel.Info, "Applying", null);
        int exitCode;
        try
        {
            exitCode = await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            var stopped = "Stopped waiting for the administrator helper: it may still be running, and its result was not read";
            Say(item, "Result", ChangeLevel.Fail, stopped, null);
            return new ChangeResult(ChangeStatus.CouldNotVerify, stopped, null, Written: true, null);
        }
        return Conclude(item, request, exitCode, origin);
    }

    /// <summary>Cancelled before the helper started: the request is of no use to anyone, so it is removed, and nothing was written.</summary>
    private ChangeResult CancelledBeforePrompt(IOptimisation item, HelperRequest request)
    {
        files.DeleteRequest(request.Invocation);
        const string message = "Not changed: cancelled before the administrator prompt opened";
        Say(item, "Cancelled", ChangeLevel.Warn, "Cancelled before the prompt opened", null);
        return new ChangeResult(ChangeStatus.NotChanged, message, null, Written: false, null);
    }

    /// <summary>
    /// The helper has exited: say what it recorded, then decide the outcome from what this panel can check itself. The
    /// helper's result is advisory. The target is read now, and a backup the result or its steps name is read from this
    /// panel's own <see cref="BackupStore"/> and must be bound to this change (an Apply's backup holds exactly the
    /// confirmed proposal, an Undo's holds the restore of the confirmed value). Changed or Restored needs both the target
    /// holding the confirmed value and such a backup; the target still holding the value it had when the helper was asked
    /// is NotChanged; anything else is CouldNotVerify, with recovery only from a validated backup. What the helper reports
    /// can make this more cautious, never less.
    /// </summary>
    private ChangeResult Conclude(IOptimisation item, HelperRequest request, int exitCode, string? origin)
    {
        var verb = request.Operation == HelperFiles.Apply ? "changed" : "restored";
        var read = files.ReadResult(request.Invocation, request.Operation, request.ItemId);
        var result = read.Result;
        // A result that disagrees with the operation or with how the helper ended is not the helper's word: it is read
        // as an unreadable one.
        if (result is { Complete: true } && !ElevatedHelper.IsConsistent(result, request.Operation, exitCode))
        {
            result = null;
            read = new ResultRead(null, $"the result does not fit the {request.Operation} or the exit code {exitCode}");
        }
        // Whatever the helper recorded is said in the order it happened, whole or not.
        if (result is not null)
            foreach (var step in result.Steps) Say(item, step.Step, step.Level, step.Outcome, step.Backup);

        var apply = request.Operation == HelperFiles.Apply;
        var expected = (apply ? request.Proposal?.After : request.RestoreValue) ?? "";
        var now = ReadNow(item);
        var bound = BoundBackup(item, request, result, origin);
        var untouched = origin is not null && now == origin;

        if (result is { Final: { } final })
        {
            var success = apply ? ChangeStatus.Changed : ChangeStatus.Restored;
            var told = apply ? "the change" : "the restore";
            if (final.Status == success)
            {
                if (now == expected && bound is not null)
                    return new ChangeResult(success, final.Message, bound.Ref, Written: true, now, final.UndoWithheld || !runner.CanUndo(item, bound.Ref).Ready);
                if (untouched)
                {
                    var unchanged = $"Not {verb}: the helper reported {told}, but the setting still reads '{now}', not the '{expected}' that was confirmed";
                    Say(item, "Result", ChangeLevel.Warn, unchanged, null);
                    return new ChangeResult(ChangeStatus.NotChanged, unchanged, null, Written: false, now);
                }
                var why = now == expected
                    ? $"the setting reads '{expected}' but no backup of this change is in the panel's backups"
                    : $"'{expected}' was confirmed and the setting now reads '{now ?? "nothing readable"}'";
                return Unverified(item, $"the helper reported {told}, but {why}", bound, now);
            }
            if (final.Status == ChangeStatus.NotChanged && untouched)
                return new ChangeResult(ChangeStatus.NotChanged, final.Message, bound is not null && bound.Name == final.Backup ? bound.Ref : null, final.Written, now);
            if (final.Status == ChangeStatus.CouldNotVerify)
            {
                var withheld = final.UndoWithheld || (bound is not null && !runner.CanUndo(item, bound.Ref).Ready);
                return new ChangeResult(ChangeStatus.CouldNotVerify, final.Message, bound?.Ref, Written: final.Written || !untouched, now, withheld);
            }
            // NotChanged, but the setting is not what it was: a failed verification reported as nothing happening.
            return Unverified(item, $"the helper reported nothing {verb}, but the setting reads {Describe(now)}", bound, now);
        }
        if (result?.Refusal is { } refusal)
        {
            var refused = $"Not {verb}: the administrator helper refused: {refusal}";
            if (untouched) return new ChangeResult(ChangeStatus.NotChanged, refused, null, Written: false, null);
            return Unverified(item, $"the administrator helper refused ({refusal}), but the setting reads {Describe(now)}", bound, now);
        }
        if (result?.Fault is { } fault)
            return Lost(item, result, $"The administrator helper failed: {fault}", exitCode, bound, now, origin);

        var problem = read.Problem is not null ? $"{read.Problem}; {ElevatedHelper.ExitMeaning(exitCode)}" : $"it stopped part way; {ElevatedHelper.ExitMeaning(exitCode)}";
        return Lost(item, result, $"The administrator helper ended without a whole result ({problem}; exit code {exitCode})", exitCode, bound, now, origin);
    }

    /// <summary>The helper's word could not be confirmed: CouldNotVerify, with recovery only from a backup this panel validated.</summary>
    private ChangeResult Unverified(IOptimisation item, string said, Backup? bound, string? now)
    {
        var message = $"Could not be verified: {said}. {Recovery(item, bound?.Name, out var backup, out var withheld)}";
        Say(item, "Result", ChangeLevel.Fail, message, bound?.Name);
        return new ChangeResult(ChangeStatus.CouldNotVerify, message, backup, Written: true, now, withheld);
    }

    /// <summary>
    /// The backup the result or its steps name that this panel's own store holds and that belongs to this change, or
    /// null. An Apply's holds exactly the confirmed proposal. An Undo's is the helper's own backup of the Undo: it
    /// restores the confirmed value from the value the target held when the helper was asked, and is not the backup
    /// being undone. A name the store can't read, or one of another change, is not a backup of this one.
    /// </summary>
    private Backup? BoundBackup(IOptimisation item, HelperRequest request, HelperResult? result, string? origin)
    {
        if (result is null) return null;
        var names = new List<string>();
        if (result.Final?.Backup is { } finalName) names.Add(finalName);
        names.AddRange(result.Steps.Where(s => s.Backup is not null).Select(s => s.Backup!).Reverse());
        foreach (var name in names.Distinct())
        {
            if (TryRead(new BackupRef(item.Id, name)) is not { } record || record.ItemId != item.Id || record.Target != item.Target) continue;
            var bound = request.Operation == HelperFiles.Apply
                ? record.Operation == BackupStore.Apply && record.Proposal == request.Proposal
                : record.Operation == BackupStore.Undo && name != request.BackupName && record.Proposal.Target == request.Target
                    && record.Proposal.After == request.RestoreValue && (origin is null || record.Proposal.Before == origin);
            if (bound) return record;
        }
        return null;
    }

    /// <summary>
    /// The helper did not finish its work: say so, and offer Undo only for the validated backup its own steps name, when
    /// D05 T02 §2's rules accept it with the target read again. A target that no longer reads what it did when the
    /// helper was asked is not "nothing written".
    /// </summary>
    private ChangeResult Lost(IOptimisation item, HelperResult? partial, string said, int exitCode, Backup? bound, string? now, string? origin)
    {
        var recovery = Recovery(item, bound?.Name, out var backup, out var withheld);
        var untouched = origin is not null && now == origin;
        ChangeStatus status;
        bool written;
        if (bound is null)
        {
            // No validated backup: a name the helper gave that the store does not hold rules nothing out, and neither does
            // a target that moved. With no name and an untouched target, no write was made, unless nothing is known of the
            // helper at all.
            written = !untouched || partial?.LastBackup is not null || (partial is null && exitCode is not (ElevatedHelper.ExitUsage or ElevatedHelper.ExitNoRequest or ElevatedHelper.ExitNotElevated));
            status = written ? ChangeStatus.CouldNotVerify : ChangeStatus.NotChanged;
        }
        else
        {
            written = backups.IsApplied(bound) || now != bound.Proposal.Before;
            status = written ? ChangeStatus.CouldNotVerify : ChangeStatus.NotChanged;
        }
        var message = $"{(status == ChangeStatus.NotChanged ? "Not changed: " : "Could not be verified: ")}{said}. {recovery}";
        Say(item, "Result", status == ChangeStatus.NotChanged ? ChangeLevel.Warn : ChangeLevel.Fail, message, bound?.Name);
        return new ChangeResult(status, message, backup, written, now, written && withheld);
    }

    /// <summary>What can be done about the backup the helper named: Undo only when it is eligible by §2's rules, else the reason.</summary>
    private string Recovery(IOptimisation item, string? backupName, out BackupRef? backup, out bool withheld)
    {
        backup = null;
        withheld = false;
        if (backupName is null) return "No backup was recorded for this change, so no Undo is offered; the setting reads " + Describe(ReadNow(item));
        backup = new BackupRef(item.Id, backupName);
        var can = runner.CanUndo(item, backup);
        if (can.Ready) return $"Undo is offered from backup {backupName}";
        withheld = true;
        return $"Undo is not offered: {can.Reason}; the setting reads {Describe(ReadNow(item))}, and the backup is {backupName}";
    }

    private static string Describe(string? value) => value is null ? "unknown (it could not be read)" : $"'{value}'";

    private static string? ReadNow(IOptimisation item)
    {
        try
        {
            var read = item.Read();
            return read.IsRead ? read.Value : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // an unreadable target is "unknown" in the words; the item's own reads never throw into the panel
        }
    }

    private Backup? TryRead(BackupRef reference)
    {
        try
        {
            return backups.Read(reference);
        }
        catch (BackupException)
        {
            return null;
        }
    }

    private ChangeResult Refused(IOptimisation item, string message)
    {
        Say(item, "Refused", ChangeLevel.Warn, message, null);
        return new ChangeResult(ChangeStatus.NotChanged, message, null, Written: false, null);
    }

    private void Say(IOptimisation item, string step, ChangeLevel level, string outcome, string? backup) =>
        log.Step(new ChangeStep(item.Id, item.Title, step, item.Target, level, outcome, backup));
}
