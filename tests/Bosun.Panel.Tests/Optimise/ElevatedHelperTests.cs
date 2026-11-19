using System.IO;
using System.Security.Principal;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Optimise;
using Bosun.Panel.Optimise;
using Bosun.Panel.Shell;
using Bosun.Panel.Startup;
using Xunit;

namespace Bosun.Panel.Tests.Optimise;

/// <summary>
/// The elevated change helper (D05 T02 §6) as the real executable: <c>Bosun.Panel.exe --optimise ...</c> started as a
/// child process through <see cref="ProcessRunner"/> (no window, a time limit, the whole process tree ended at the
/// limit), against a disposable item the test registration puts in the executable (see <see cref="PanelOptimisations"/>:
/// it exists only under <c>BOSUN_PANEL_TEST_ITEM</c>), in a data folder of the test's own (<c>BOSUN_PANEL_DATA</c>).
/// A real UAC prompt is not run here (D99 T01 §5), and nothing in the executable stands in for the token: the tests that
/// need the helper to act run only when this test process really is elevated (CI's Windows runners are) and are skipped
/// with that reason otherwise; the refusal of an unelevated run is proven whenever the process is not elevated. A second Windows account
/// cannot be made here, so the account rule is proven in Bosun.Core.Tests through two account folders, and here by the
/// helper's refusal of a redirected folder, a folder no account owns and a request of another account.
/// </summary>
public sealed class ElevatedHelperTests : IDisposable
{
    private static readonly ProcessRunner Runner = new();
    private string Target => "test:disposable-file:" + Path.GetFullPath(_value);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bosun-panel-helper-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _value;

    public ElevatedHelperTests()
    {
        _data = Path.Combine(_root, "data");
        _value = Path.Combine(_root, "value.txt");
        Directory.CreateDirectory(_data);
        File.WriteAllText(_value, "original");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;

    private static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>The helper only acts under a really elevated token, so a test that needs it to act can only run in an elevated process.</summary>
    private static void RequireElevated() =>
        Assert.SkipUnless(Elevated, "This process is not elevated, and the helper only acts under a really elevated token (the executable has no override); it runs where the process is elevated, as on CI's Windows runners.");

    private Dictionary<string, string?> Env(string? mode = null, string? instance = null) => new()
    {
        [PanelPaths.Variable] = _data,
        [PanelOptimisations.TestItemVariable] = _value,
        [PanelOptimisations.TestModeVariable] = mode,
        [InstanceName.Variable] = instance,
    };

    private static Task<ProcessResult> Exe(IReadOnlyList<string> arguments, Dictionary<string, string?> environment, CancellationToken cancel) =>
        Runner.RunAsync(new ProcessRequest(StartupSupport.Built("Bosun.Panel", "Bosun.Panel.exe"), arguments) { Environment = environment, Timeout = TimeSpan.FromSeconds(30) }, cancel);

    private HelperFiles Files => new(_data);

    private HelperRequest Apply(string invocation, Proposal? proposal = null, string? folder = null, string? sid = null) =>
        new(invocation, "apply", TestFileItem.ItemId, sid ?? Sid, folder ?? _data, Target, proposal ?? new Proposal(Target, "original", "applied"), null, null);

    private HelperRequest Undo(string invocation, string backup, string restore) =>
        new(invocation, "undo", TestFileItem.ItemId, Sid, _data, Target, null, backup, restore);

    private static string[] Command(string operation, string invocation, string itemId = TestFileItem.ItemId) => ["--optimise", operation, itemId, invocation];

    private HelperResult ResultOf(string invocation, string operation)
    {
        var read = Files.ReadResult(invocation, operation, TestFileItem.ItemId);
        Assert.True(read.Problem is null, read.Problem);
        return read.Result!;
    }

    /// <summary>The names of the backups themselves (not their applied markers or temporary files), oldest first.</summary>
    private string[] BackupFiles()
    {
        var folder = new BackupStore(_data).Folder(TestFileItem.ItemId);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).Where(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d{8}T\d{9}Z-(apply|undo)\.json$")).Order(StringComparer.Ordinal)];
    }

    /// <summary>The helper as the panel starts it: the real executable, to the end, through the shared change path.</summary>
    private sealed class ExeElevation(Func<Dictionary<string, string?>> environment, CancellationToken cancel) : IElevation
    {
        public int Calls;
        public ProcessResult? Last;

        public async Task<ElevationStart> StartAsync(HelperCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            Last = await Exe(command.Arguments, environment(), cancel);
            return Last.Outcome == ProcessOutcome.Exited ? ElevationStart.Running(new Exited(Last.ExitCode!.Value)) : ElevationStart.Failed($"the helper did not finish: {Last.Outcome}");
        }
    }

    private sealed class Exited(int code) : IElevatedProcess
    {
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(code);

        public void Dispose()
        {
        }
    }

    private sealed class RecordingLog : IChangeLog
    {
        public List<ChangeStep> Steps { get; } = [];

        public void Step(ChangeStep step) => Steps.Add(step);
    }

    private ChangeCoordinator Coordinator(ExeElevation elevation, IChangeLog log) =>
        new(new ChangeRunner(new BackupStore(_data), log), new BackupStore(_data), log, elevation, Files, Sid, _data);

    private static TestFileItem PanelItem(string path) => new(path);

    // ---- the registry and the test seam ----

    [Fact]
    public void A_proposal_for_one_test_file_can_never_be_applied_to_another_of_the_same_text()
    {
        // Round 8: both files' target was "test:disposable-file", so a proposal confirmed for one applied to the other.
        var other = Path.Combine(_root, "other.txt");
        File.WriteAllText(_value, "same");
        File.WriteAllText(other, "same");
        var first = PanelOptimisations.Registry(name => name == PanelOptimisations.TestItemVariable ? _value : null).Resolve(TestFileItem.ItemId)!;
        var second = PanelOptimisations.Registry(name => name == PanelOptimisations.TestItemVariable ? other : null).Resolve(TestFileItem.ItemId)!;
        var confirmed = first.Propose().Value!;
        Assert.NotEqual(first.Target, second.Target);
        var can = new ChangeRunner(new BackupStore(_data), new NullChangeLog(), elevated: true).CanApply(second, confirmed);
        Assert.False(can.Allowed);
        Assert.Equal("same", File.ReadAllText(other));
    }

    private sealed class NullChangeLog : IChangeLog
    {
        public void Step(ChangeStep step) { }
    }

    [Fact]
    public void The_test_registration_exists_only_under_its_variable_and_no_variable_says_elevated()
    {
        Assert.Empty(PanelOptimisations.Registry(_ => null).Items);
        Assert.Empty(PanelOptimisations.Registry(name => name == "BOSUN_PANEL_TEST_ELEVATED" ? "1" : null).Items);
        var registered = PanelOptimisations.Registry(name => name == PanelOptimisations.TestItemVariable ? _value : null);
        Assert.Equal([TestFileItem.ItemId], registered.Items.Select(i => i.Id));

        // Elevation is the process token's, whatever any variable says, the test registration included.
        Assert.Equal(Elevated, new WindowsHelperStanding(_ => null).IsElevated);
        Assert.Equal(Elevated, new WindowsHelperStanding(name => name switch { PanelOptimisations.TestItemVariable => _value, "BOSUN_PANEL_TEST_ELEVATED" => "1", _ => null }).IsElevated);
        Assert.Equal(Elevated, new WindowsHelperStanding(name => name switch { PanelOptimisations.TestItemVariable => _value, "BOSUN_PANEL_TEST_ELEVATED" => "0", _ => null }).IsElevated);
    }

    [Fact]
    public void The_helpers_account_folders_are_each_accounts_own_profile_data_folder()
    {
        var standing = new WindowsHelperStanding(name => name == PanelPaths.Variable ? _data : null);
        Assert.Equal([new AccountFolder(Sid, _data)], standing.Accounts());
        Assert.Equal(Sid, standing.CurrentSid);

        var folders = WindowsHelperStanding.FromProfiles([
            ("S-1-5-21-1-2-3-1001", @"C:\Users\one"),
            ("S-1-5-18", @"C:\WINDOWS\system32\config\systemprofile"),
            ("S-1-5-21-1-2-3-1002", "relative"),
            ("S-1-12-1-1-2-3-4", @"C:\Users\work"),
        ]);
        Assert.Equal([new AccountFolder("S-1-5-21-1-2-3-1001", @"C:\Users\one\AppData\Local\Bosun", @"C:\Users\one"), new AccountFolder("S-1-12-1-1-2-3-4", @"C:\Users\work\AppData\Local\Bosun", @"C:\Users\work")], folders);
    }

    [Fact]
    public void The_windows_elevation_reads_error_1223_as_a_declined_prompt()
    {
        Assert.Equal(ElevationStart.Declined, WindowsElevation.Classify(1223));
        var failed = WindowsElevation.Classify(2);
        Assert.Equal(ElevationStartKind.Failed, failed.Kind);
        Assert.False(string.IsNullOrEmpty(failed.Reason));
        Assert.Equal(["--optimise", "apply", "item.one", new string('a', 32)], new HelperCommand("apply", "item.one", new string('a', 32)).Arguments);
    }

    // ---- the real executable ----

    [Fact]
    public async Task The_real_executable_applies_undoes_and_undoes_that_undo_once_each_with_no_window()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;
        var applyId = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(applyId));

        var applied = await Exe(Command("apply", applyId), Env(), cancel);

        Assert.Equal(ProcessOutcome.Exited, applied.Outcome); // a dialog would hold it until the limit
        Assert.Equal(ElevatedHelper.ExitDone, applied.ExitCode);
        Assert.Equal("applied", File.ReadAllText(_value));
        var result = ResultOf(applyId, "apply");
        Assert.Equal(["Started", "Backup", "Write returned", "Verify", "Result"], result.Steps.Select(s => s.Step));
        Assert.Equal(ChangeStatus.Changed, result.Final!.Status);
        Assert.Single(BackupFiles()); // one change, one backup, written once
        Assert.False(Directory.Exists(PanelPaths.Crash(_data)));

        var undoId = HelperFiles.NewInvocation();
        Files.WriteRequest(Undo(undoId, result.Final.Backup!, "original"));
        var undone = await Exe(Command("undo", undoId), Env(), cancel);
        Assert.Equal(ElevatedHelper.ExitDone, undone.ExitCode);
        Assert.Equal("original", File.ReadAllText(_value));
        var undoResult = ResultOf(undoId, "undo");
        Assert.Equal(ChangeStatus.Restored, undoResult.Final!.Status);
        Assert.NotEqual(result.Final.Backup, undoResult.Final.Backup);

        var againId = HelperFiles.NewInvocation();
        Files.WriteRequest(Undo(againId, undoResult.Final.Backup!, "applied"));
        Assert.Equal(ElevatedHelper.ExitDone, (await Exe(Command("undo", againId), Env(), cancel)).ExitCode);
        Assert.Equal("applied", File.ReadAllText(_value));
        Assert.Equal(2, BackupFiles().Count(f => f.EndsWith("-undo.json", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task The_real_executable_refuses_every_mismatch_and_writes_nothing()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;

        // An in-range substituted value: not the proposal the item makes.
        var substituted = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(substituted, new Proposal(Target, "original", "evil")));
        Assert.Equal(ElevatedHelper.ExitNotChanged, (await Exe(Command("apply", substituted), Env(), cancel)).ExitCode);

        // A changed proposal: the setting did not read this when the person confirmed it.
        var changed = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(changed, new Proposal(Target, "something else", "applied")));
        Assert.Equal(ElevatedHelper.ExitNotChanged, (await Exe(Command("apply", changed), Env(), cancel)).ExitCode);

        // An unknown id.
        var unknown = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(unknown) with { ItemId = "no.such" });
        Assert.Equal(ElevatedHelper.ExitRefused, (await Exe(Command("apply", unknown, "no.such"), Env(), cancel)).ExitCode);

        // A mismatched operation: the command line says undo, the request is an apply.
        var wrongOperation = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(wrongOperation));
        Assert.Equal(ElevatedHelper.ExitRefused, (await Exe(Command("undo", wrongOperation), Env(), cancel)).ExitCode);

        // Not the fixed command: refused before anything is read.
        Assert.Equal(ElevatedHelper.ExitUsage, (await Exe(["--optimise", "apply", TestFileItem.ItemId], Env(), cancel)).ExitCode);
        Assert.Equal(ElevatedHelper.ExitUsage, (await Exe(["--optimise", "apply", "..\\x", substituted], Env(), cancel)).ExitCode);

        Assert.Equal("original", File.ReadAllText(_value));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task An_unelevated_run_is_refused_and_does_nothing()
    {
        Assert.SkipWhen(Elevated, "This process is elevated, so a helper it starts is elevated too and the refusal of an unelevated one can't be shown here; it runs wherever the process is not elevated.");
        var invocation = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(invocation));

        // The executable decides from its own token alone: no variable makes it act.
        var env = Env();
        env["BOSUN_PANEL_TEST_ELEVATED"] = "1";
        var run = await Exe(Command("apply", invocation), env, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessOutcome.Exited, run.Outcome);
        Assert.Equal(ElevatedHelper.ExitNotElevated, run.ExitCode);
        Assert.Equal("original", File.ReadAllText(_value));
        Assert.False(File.Exists(Files.ResultPath(invocation)));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task A_redirected_folder_an_unrelated_one_and_another_accounts_request_are_refused()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;

        // Redirected: the request names a folder that is not this account's own. Nothing is written there.
        var elsewhere = Path.Combine(_root, "redirected");
        var redirected = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(redirected, folder: elsewhere));
        Assert.Equal(ElevatedHelper.ExitRefused, (await Exe(Command("apply", redirected), Env(), cancel)).ExitCode);
        Assert.False(Directory.Exists(elsewhere));
        Assert.Contains("data folder", ResultOf(redirected, "apply").Refusal);

        // Another account's request: it names a SID that is not the account whose folder it was found in.
        var foreign = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(foreign, sid: "S-1-5-21-1-2-3-9999"));
        Assert.Equal(ElevatedHelper.ExitRefused, (await Exe(Command("apply", foreign), Env(), cancel)).ExitCode);
        Assert.Contains("another account", ResultOf(foreign, "apply").Refusal);

        // Unrelated: a request in a folder no account of the helper owns is never looked at.
        var unrelated = Path.Combine(_root, "unrelated");
        var lost = HelperFiles.NewInvocation();
        new HelperFiles(unrelated).WriteRequest(Apply(lost, folder: unrelated));
        Assert.Equal(ElevatedHelper.ExitNoRequest, (await Exe(Command("apply", lost), Env(), cancel)).ExitCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(unrelated, "helper"), "*.result.json"));

        Assert.Equal("original", File.ReadAllText(_value));
        Assert.Empty(BackupFiles());
    }

    [Fact]
    public async Task A_fault_or_a_failing_item_with_a_panel_already_running_shows_no_dialog_and_does_not_reach_it()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;
        var name = StartupSupport.Name();
        var activations = new int[1];
        using var running = SingleInstance.Start(name, activated: () =>
        {
            Interlocked.Increment(ref activations[0]);
            return Task.CompletedTask;
        });

        // Its title faults: nothing the item handles. The fault is in the result, the exit code says so.
        var fault = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(fault));
        var faulted = await Exe(Command("apply", fault), Env("explode", instance: name), cancel);
        Assert.Equal(ProcessOutcome.Exited, faulted.Outcome); // no dialog held it
        Assert.Equal(ElevatedHelper.ExitFaulted, faulted.ExitCode);
        Assert.Contains("the test item's title faults", ResultOf(fault, "apply").Fault);

        // Its Apply throws: the failure is the item's, so it is a result, and the setting is as it was.
        var throws = HelperFiles.NewInvocation();
        Files.WriteRequest(Apply(throws));
        var thrown = await Exe(Command("apply", throws), Env("apply-throws", instance: name), cancel);
        Assert.Equal(ElevatedHelper.ExitNotChanged, thrown.ExitCode);
        var result = ResultOf(throws, "apply");
        Assert.Equal(ChangeStatus.NotChanged, result.Final!.Status);
        Assert.Contains("the test item refuses to write", result.Steps.Single(s => s.Step == "Write").Outcome);
        Assert.Equal("original", File.ReadAllText(_value));

        // No restart, no forward to the running panel, no crash report.
        Assert.Equal(0, activations[0]);
        Assert.False(Directory.Exists(PanelPaths.Crash(_data)));
    }

    // ---- recovery after a lost helper (the real executable, killed at each point) ----

    /// <summary>Waits for a change on a UI thread the way the panel does: its dispatcher is pumped, so the lines posted to it land.</summary>
    private static T OnUi<T>(Func<Task<T>> work)
    {
        var task = work();
        Assert.True(StartupSupport.Pump(() => task.IsCompleted, TimeSpan.FromSeconds(90)), "the change did not finish");
        StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(100)); // the lines posted to the UI thread
        return task.GetAwaiter().GetResult();
    }

    /// <summary>An older change already in the backups: a prepared backup that a lost helper must never be mistaken for.</summary>
    private BackupRef OlderBackup() => new BackupStore(_data).Write(TestFileItem.ItemId, BackupStore.Apply, new Proposal(Target, "older before", "original"));

    private async Task<ChangeResult> LostHelper(string mode)
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;
        var elevation = new ExeElevation(() => Env(mode), cancel);
        var item = PanelItem(_value);
        var result = await Coordinator(elevation, new RecordingLog()).ApplyAsync(item, item.Propose().Value, cancel);
        Assert.Equal(9, elevation.Last!.ExitCode); // it ended abruptly, with no result of its own
        return result;
    }

    [Fact]
    public async Task A_helper_killed_before_its_backup_offers_no_undo_with_an_older_backup_present()
    {
        var older = OlderBackup();

        var result = await LostHelper("die-before-backup");

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.False(result.UndoOffered);
        Assert.Contains("No backup was recorded for this change, so no Undo is offered", result.Message);
        Assert.Equal("original", File.ReadAllText(_value));
        Assert.Equal([older.Name], BackupFiles());
    }

    [Fact]
    public async Task A_helper_killed_after_its_backup_and_before_the_write_says_nothing_was_written()
    {
        var older = OlderBackup();

        var result = await LostHelper("die-before-write");

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.False(result.UndoOffered);
        Assert.NotEqual(older.Name, result.Backup!.Name);
        Assert.Contains("Undo is not offered: The setting already holds the value this backup would restore", result.Message);
        Assert.Equal("original", File.ReadAllText(_value));
        Assert.Equal(2, BackupFiles().Length);
    }

    [Fact]
    public async Task A_helper_killed_after_the_write_offers_undo_for_its_own_backup_and_the_undo_works()
    {
        RequireElevated();
        var older = OlderBackup();

        var result = await LostHelper("die-after-write");

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.True(result.Written);
        Assert.True(result.UndoOffered);
        Assert.NotEqual(older.Name, result.Backup!.Name);
        Assert.Contains($"Undo is offered from backup {result.Backup.Name}", result.Message);
        Assert.Equal("applied", File.ReadAllText(_value));

        // And the Undo it offers is the one that works: through the helper again, by that exact backup.
        var cancel = TestContext.Current.CancellationToken;
        var item = PanelItem(_value);
        var undone = await Coordinator(new ExeElevation(() => Env(), cancel), new RecordingLog()).UndoAsync(item, result.Backup, "original", cancel);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("original", File.ReadAllText(_value));
    }

    // ---- the panel reads and says the result ----

    [Fact]
    public void A_successful_apply_and_undo_reach_the_activity_pane_step_by_step()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;
        var (applied, undone, lines, activations) = Sta.Run(() =>
        {
            var activity = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
            var elevation = new ExeElevation(() => Env(), cancel);
            var coordinator = Coordinator(elevation, new ActivityChangeLog(activity));
            var item = PanelItem(_value);
            var first = OnUi(() => coordinator.ApplyAsync(item, item.Propose().Value, cancel));
            var second = OnUi(() => coordinator.UndoAsync(item, first.Backup, "original", cancel));
            StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(100));
            return (first, second, activity.Entries.Select(e => e.Tag + " " + e.Message).ToArray(), elevation.Calls);
        });

        Assert.Equal(2, activations);
        Assert.Equal(ChangeStatus.Changed, applied.Status);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("original", File.ReadAllText(_value));
        // The step is the text between the title and the target's bracket.
        var named = lines.Select(l => l[(l.IndexOf(", ", StringComparison.Ordinal) + 2)..l.IndexOf(" (", StringComparison.Ordinal)]).ToArray();
        Assert.Equal(["Administrator", "Administrator", "Started", "Backup", "Write returned", "Verify", "Result",
                      "Administrator", "Administrator", "Started", "Backup", "Write returned", "Verify", "Result"], named);
        Assert.Equal($"INFO Test setting, Administrator ({Target}): Waiting for the administrator prompt", lines[0]);
        Assert.Equal($"INFO Test setting, Administrator ({Target}): Applying", lines[1]);
        Assert.All(lines, l => Assert.StartsWith("INFO ", l));
    }

    [Fact]
    public void A_failed_verify_comes_back_with_its_undo_offered_and_a_fault_comes_back_as_a_fail_line()
    {
        RequireElevated();
        var cancel = TestContext.Current.CancellationToken;
        var (verify, verifyLines, valueAfterVerify, faulted, faultLines) = Sta.Run(() =>
        {
            var activity = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
            var item = PanelItem(_value);
            var verifyRun = OnUi(() => Coordinator(new ExeElevation(() => Env("verify-fails"), cancel), new ActivityChangeLog(activity)).ApplyAsync(item, item.Propose().Value, cancel));
            var verifyOutput = activity.Entries.Select(e => e.Tag + " " + e.Message).ToArray();
            var valueAfterVerify = File.ReadAllText(_value);

            File.WriteAllText(_value, "original");
            var other = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
            var faultRun = OnUi(() => Coordinator(new ExeElevation(() => Env("explode"), cancel), new ActivityChangeLog(other)).ApplyAsync(item, item.Propose().Value, cancel));
            return (verifyRun, verifyOutput, valueAfterVerify, faultRun, other.Entries.Select(e => e.Tag + " " + e.Message).ToArray());
        });

        Assert.Equal(ChangeStatus.CouldNotVerify, verify.Status);
        Assert.True(verify.UndoOffered);
        Assert.Equal("applied", valueAfterVerify); // it was written; only its read-back disagreed
        Assert.StartsWith("FAIL Test setting, Verify", verifyLines.Single(l => l.Contains(", Verify (")));
        Assert.StartsWith($"FAIL Test setting, Result ({Target}): Could not be verified: it now reads 'applied' but its verification read 'unexpected'. Undo is offered from backup " + verify.Backup!.Name, verifyLines[^1]);

        Assert.Equal(ChangeStatus.NotChanged, faulted.Status); // it faulted before any backup: nothing was written
        Assert.Contains("The administrator helper failed: InvalidOperationException: the test item's title faults", faulted.Message);
        Assert.StartsWith("WARN Test setting", faultLines[^1]);
    }
}
