namespace Bosun.Core.Optimise;

/// <summary>One account's panel data folder, as the helper's host resolves it for that account's SID.</summary>
public sealed record AccountFolder(string Sid, string DataFolder);

/// <summary>
/// What the elevated helper stands on (D05 T02 §6), apart so a test can satisfy it without a real elevation or a
/// second Windows account: whether this process's token is elevated, who it runs as, and the data folder of every
/// account whose panel may have started it. The Windows form is in the panel.
/// </summary>
public interface IHelperStanding
{
    /// <summary>True only when this process's token is elevated: <see cref="ChangeRunner"/>'s elevated mode is never enabled by the command line.</summary>
    bool IsElevated { get; }

    /// <summary>The SID this process runs as: the initiating user's own, or another administrator's.</summary>
    string CurrentSid { get; }

    /// <summary>
    /// Each account's own panel data folder, found by the account and never by a request: its profile's
    /// <c>AppData\Local\Bosun</c>. The helper accepts only a request found in one of these.
    /// </summary>
    IReadOnlyList<AccountFolder> Accounts();
}

/// <summary>
/// The elevated helper's whole work (D05 T02 §6): headless, one confirmed change or Undo, then exit. It checks it really
/// is elevated, finds the initiating panel's data folder by the request's invocation and SID (never a path a request
/// names) and writes only there, derives the target and proposal from the <see cref="OptimisationRegistry"/>, refuses
/// any mismatch with what the person confirmed before it writes, and records each step in its result file as it
/// happens, each record replacing the last whole.
///
/// Exit codes: 0 done (Changed or Restored), 1 done but not changed or not verified (the result says why), 2 refused
/// (the result says why), 3 faulted, 4 not the fixed command, 5 no request found in an account's folder (or more than
/// one), 6 not elevated. For 3 to 6 nothing is written unless the result file could be (a fault after the request was
/// found is recorded in it).
/// </summary>
public static class ElevatedHelper
{
    public const int ExitDone = 0, ExitNotChanged = 1, ExitRefused = 2, ExitFaulted = 3, ExitUsage = 4, ExitNoRequest = 5, ExitNotElevated = 6;

    /// <summary>True when the command line asks for helper mode: its first argument is <c>--optimise</c>.</summary>
    public static bool IsHelper(IReadOnlyList<string> args) => args.Count > 0 && args[0] == HelperCommand.Switch;

    /// <summary>What an exit code means, for the panel to say when there is no result to read.</summary>
    public static string ExitMeaning(int code) => code switch
    {
        ExitDone => "it finished",
        ExitNotChanged => "it finished without changing the setting",
        ExitRefused => "it refused the request",
        ExitFaulted => "it failed",
        ExitUsage => "it was not given the fixed command",
        ExitNoRequest => "it found no request of this invocation in the starting user's data folder",
        ExitNotElevated => "it was not elevated, so it did nothing",
        _ => "it ended",
    };

    /// <summary>Parses the fixed command: <c>--optimise apply|undo &lt;item id&gt; &lt;invocation id&gt;</c>, nothing more.</summary>
    public static HelperCommand? Parse(IReadOnlyList<string> args) =>
        args.Count == 4 && args[0] == HelperCommand.Switch && args[1] is HelperFiles.Apply or HelperFiles.Undo && OptimisationRegistry.IsValidId(args[2]) && HelperFiles.IsInvocation(args[3])
            ? new HelperCommand(args[1], args[2], args[3])
            : null;

    public static int Run(IReadOnlyList<string> args, IHelperStanding standing, OptimisationRegistry registry, PanelSettingsFiles? files = null, Func<DateTimeOffset>? clock = null)
    {
        if (Parse(args) is not { } command) return ExitUsage;
        // First, before anything is read or written: the command line alone never makes this an elevated process.
        if (!standing.IsElevated) return ExitNotElevated;

        // The request is looked for only in the data folder of an account the host knows, by invocation id.
        var holders = standing.Accounts().Where(a => new HelperFiles(a.DataFolder, files).HasRequest(command.Invocation)).ToList();
        if (holders.Count != 1) return ExitNoRequest;
        var account = holders[0];
        var helperFiles = new HelperFiles(account.DataFolder, files);
        var recorder = new Recorder(helperFiles, command);
        try
        {
            return Execute(command, account, standing, registry, helperFiles, recorder, files, clock);
        }
        catch (Exception e)
        {
            // The helper's own failure path (D05 T02 §6, gap 7): whatever went wrong is written to its result and the
            // exit code says so. There is no dialog, restart or forward to a running panel, and nothing escapes.
            try
            {
                recorder.Fault($"{e.GetType().Name}: {e.Message}");
            }
            catch (HelperProtocolException)
            {
                // The result could not be kept either: the exit code is all that is left, and the panel says so.
            }
            return ExitFaulted;
        }
    }

    private static int Execute(HelperCommand command, AccountFolder account, IHelperStanding standing, OptimisationRegistry registry, HelperFiles helperFiles, Recorder recorder, PanelSettingsFiles? files, Func<DateTimeOffset>? clock)
    {
        HelperRequest request;
        try
        {
            request = helperFiles.ReadRequest(command.Invocation);
        }
        catch (HelperProtocolException e)
        {
            return recorder.Refuse($"The request can't be trusted: {e.Message}");
        }
        recorder.Step("Started", ChangeLevel.Info, $"Helper running as {standing.CurrentSid} for the panel of {account.Sid}");

        // The request must be this command's, and this account's.
        if (request.Invocation != command.Invocation) return recorder.Refuse("The request is for another invocation");
        if (request.Operation != command.Operation) return recorder.Refuse($"The request is for '{request.Operation}', not '{command.Operation}'");
        if (request.ItemId != command.ItemId) return recorder.Refuse($"The request is for item '{request.ItemId}', not '{command.ItemId}'");
        if (!string.Equals(request.InitiatorSid, account.Sid, StringComparison.OrdinalIgnoreCase))
            return recorder.Refuse("The request was found in the data folder of another account than the one it names");
        if (!SameFolder(request.DataFolder, account.DataFolder))
            return recorder.Refuse("The request names a data folder that is not its account's own: nothing is written there");

        var item = registry.Resolve(command.ItemId);
        if (item is null) return recorder.Refuse($"'{command.ItemId}' is not an item this helper can change");
        if (request.Target != item.Target) return recorder.Refuse("The request is for another setting than this item changes");

        // Everything is kept in the resolved account's folder, whoever this process runs as.
        var runner = new ChangeRunner(new BackupStore(account.DataFolder, files, clock), recorder, elevated: true);
        ChangeResult result;
        if (command.Operation == HelperFiles.Apply)
        {
            if (request.Proposal is null) return recorder.Refuse("The request holds no confirmed proposal");
            result = runner.Apply(item, request.Proposal);
        }
        else
        {
            if (request.BackupName is null || request.RestoreValue is null) return recorder.Refuse("The request holds no confirmed backup and restore value");
            var reference = new BackupRef(item.Id, request.BackupName);
            Backup record;
            try
            {
                record = new BackupStore(account.DataFolder, files, clock).Read(reference);
            }
            catch (BackupException e)
            {
                return recorder.Refuse($"The confirmed backup can't be trusted: {e.Message}");
            }
            if (record.Target != item.Target) return recorder.Refuse("The confirmed backup is for another setting");
            if (record.Before != request.RestoreValue) return recorder.Refuse($"The value this backup restores is '{record.Before}', not the '{request.RestoreValue}' that was confirmed");
            result = runner.Undo(item, reference);
        }
        recorder.Finish(result);
        return result.Status is ChangeStatus.Changed or ChangeStatus.Restored ? ExitDone : ExitNotChanged;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Keeps the result file: every step is added and the whole file written again, so it is always whole.</summary>
    private sealed class Recorder(HelperFiles files, HelperCommand command) : IChangeLog
    {
        private readonly List<HelperStep> _steps = [];

        public void Step(ChangeStep step)
        {
            _steps.Add(new HelperStep(step.Step, step.Level, step.Outcome, step.Backup));
            Write(null, null, null);
        }

        public void Step(string step, ChangeLevel level, string outcome)
        {
            _steps.Add(new HelperStep(step, level, outcome, null));
            Write(null, null, null);
        }

        public int Refuse(string reason)
        {
            Step("Refused", ChangeLevel.Warn, reason);
            Write(null, reason, null);
            return ExitRefused;
        }

        public void Finish(ChangeResult result) =>
            Write(new HelperFinal(result.Status, result.Message, result.Backup?.Name, result.Written, result.ValueNow, result.UndoWithheld), null, null);

        public void Fault(string fault) => Write(null, null, fault);

        private void Write(HelperFinal? final, string? refusal, string? fault) =>
            files.WriteResult(new HelperResult(command.Invocation, command.Operation, command.ItemId, [.. _steps], final, refusal, fault));
    }
}
