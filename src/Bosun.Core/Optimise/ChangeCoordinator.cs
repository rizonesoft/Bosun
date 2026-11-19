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
        if (!can.NeedsAdministrator) return runner.Undo(item, backup!);
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
                return Refused(item, $"Not {verb}: you declined the administrator prompt");
            case ElevationStartKind.Failed:
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
        return Conclude(item, request, exitCode);
    }

    /// <summary>Cancelled before the helper started: the request is of no use to anyone, so it is removed, and nothing was written.</summary>
    private ChangeResult CancelledBeforePrompt(IOptimisation item, HelperRequest request)
    {
        files.DeleteRequest(request.Invocation);
        const string message = "Not changed: cancelled before the administrator prompt opened";
        Say(item, "Cancelled", ChangeLevel.Warn, "Cancelled before the prompt opened", null);
        return new ChangeResult(ChangeStatus.NotChanged, message, null, Written: false, null);
    }

    /// <summary>The helper has exited: read its result and say what happened.</summary>
    private ChangeResult Conclude(IOptimisation item, HelperRequest request, int exitCode)
    {
        var verb = request.Operation == HelperFiles.Apply ? "changed" : "restored";
        var read = files.ReadResult(request.Invocation, request.Operation, request.ItemId);
        var result = read.Result;
        // A result that disagrees with the operation or with how the helper ended is not the helper's word: it is read
        // as an unreadable one, so the target is read again and recovery follows D05 T02 §2's rules.
        if (result is { Complete: true } && !ElevatedHelper.IsConsistent(result, request.Operation, exitCode))
        {
            result = null;
            read = new ResultRead(null, $"the result does not fit the {request.Operation} or the exit code {exitCode}");
        }
        // Whatever the helper recorded is said in the order it happened, whole or not.
        if (result is not null)
            foreach (var step in result.Steps) Say(item, step.Step, step.Level, step.Outcome, step.Backup);

        if (result is { Final: { } final })
        {
            var mapped = new ChangeResult(final.Status, final.Message, final.Backup is null ? null : new BackupRef(item.Id, final.Backup), final.Written, final.ValueNow, final.UndoWithheld);
            // The helper's word is checked against the target: a change that says it holds the new value must read it.
            var expected = request.Operation == HelperFiles.Apply ? request.Proposal?.After : null;
            if (final.Status is ChangeStatus.Changed && expected is not null && ReadNow(item) is { } now && now != expected)
            {
                var message = $"Could not be verified: the helper reported the change, but the setting now reads '{now}', not '{expected}'. {Recovery(item, final.Backup, out var backup, out var withheld)}";
                Say(item, "Result", ChangeLevel.Fail, message, final.Backup);
                return new ChangeResult(ChangeStatus.CouldNotVerify, message, backup, Written: true, now, withheld);
            }
            return mapped;
        }
        if (result?.Refusal is { } refusal)
            return new ChangeResult(ChangeStatus.NotChanged, $"Not {verb}: the administrator helper refused: {refusal}", null, Written: false, null);
        if (result?.Fault is { } fault)
            return Lost(item, result, $"The administrator helper failed: {fault}", exitCode);

        var why = read.Problem is not null ? $"{read.Problem}; {ElevatedHelper.ExitMeaning(exitCode)}" : $"it stopped part way; {ElevatedHelper.ExitMeaning(exitCode)}";
        return Lost(item, result, $"The administrator helper ended without a whole result ({why}; exit code {exitCode})", exitCode);
    }

    /// <summary>
    /// The helper did not finish its work: say so, and offer Undo only for the backup its own steps name, when D05 T02
    /// §2's rules accept it with the target read again.
    /// </summary>
    private ChangeResult Lost(IOptimisation item, HelperResult? partial, string said, int exitCode)
    {
        var name = partial?.LastBackup;
        var recovery = Recovery(item, name, out var backup, out var withheld);
        var now = ReadNow(item);
        ChangeStatus status;
        bool written;
        if (name is null)
        {
            // No backup was made, so no write was: unless nothing is known of the helper at all, and then it is not ruled out.
            written = partial is null && exitCode is not (ElevatedHelper.ExitUsage or ElevatedHelper.ExitNoRequest or ElevatedHelper.ExitNotElevated);
            status = written ? ChangeStatus.CouldNotVerify : ChangeStatus.NotChanged;
        }
        else
        {
            var record = TryRead(new BackupRef(item.Id, name));
            written = record is not null && (backups.IsApplied(record) || now != record.Proposal.Before);
            status = record is null || written ? ChangeStatus.CouldNotVerify : ChangeStatus.NotChanged;
        }
        var message = $"{(status == ChangeStatus.NotChanged ? "Not changed: " : "Could not be verified: ")}{said}. {recovery}";
        Say(item, "Result", status == ChangeStatus.NotChanged ? ChangeLevel.Warn : ChangeLevel.Fail, message, name);
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
