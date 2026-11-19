using System.Runtime.InteropServices;
using Bosun.Core.Optimise;
using Xunit;

namespace Bosun.Core.Tests.Optimise;

/// <summary>
/// The elevated change helper (D05 T02 §6), through the real helper engine, the real request and result files and the
/// shared change path, in this process: a registry that a test registers a disposable item in, one eligibility answer
/// with the administrator prompt in it, the helper's own standing (elevation, the initiating account's folder), the
/// request bound to what the person confirmed, the steps recorded before any write, and the panel reading only a
/// whole result bound to its invocation. The real executable is driven in the panel's own ElevatedHelperTests.
/// Two Windows accounts cannot be made here: the account rule is tested through <see cref="IHelperStanding"/> with two
/// account folders, the helper running as one and the request coming from the other.
/// </summary>
public sealed class ElevatedHelperTests : IDisposable
{
    private const string UserSid = "S-1-5-21-1000-1000-1000-1001";
    private const string AdminSid = "S-1-5-21-1000-1000-1000-1002";

    private readonly List<string> _folders = [];
    private readonly string _user;
    private readonly string _admin;

    public ElevatedHelperTests()
    {
        _user = Folder("user");
        _admin = Folder("admin");
    }

    public void Dispose()
    {
        foreach (var folder in _folders)
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    /// <summary>A name under the temp folder that nothing is made at: a folder only exists once something is written in it.</summary>
    private string Folder(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bosun-helper-{name}-" + Guid.NewGuid().ToString("N"));
        _folders.Add(path);
        return path;
    }

    private sealed class Item(string id = "disposable.item", string target = "disposable:Setting", bool elevated = true) : IOptimisation
    {
        public string Value = "1";
        public string After = "2";
        public bool Explode;
        public int Writes;
        public int Reads;
        public int UnavailableReads;

        public string Id => id;
        public string Title => Explode ? throw new InvalidOperationException("the title faults") : "Disposable setting";
        public string Target => target;
        public bool NeedsElevation => elevated;

        public Fact<string> Read()
        {
            Reads++;
            if (UnavailableReads > 0)
            {
                UnavailableReads--;
                return Fact<string>.Unavailable("busy");
            }
            return Fact<string>.Read(Value);
        }

        public Fact<Proposal> Propose() => Fact<Proposal>.Read(new Proposal(target, Value, After));

        public void Apply(string expectedBefore, string after) => Write(expectedBefore, after);

        public Fact<string> Verify(Proposal proposal) => Fact<string>.Read(Value);

        public void Restore(string expectedCurrent, string value) => Write(expectedCurrent, value);

        private void Write(string expected, string value)
        {
            if (Value != expected) throw new ChangeConflictException($"it reads '{Value}', not '{expected}'");
            Writes++;
            Value = value;
        }
    }

    private sealed class RecordingLog : IChangeLog
    {
        public List<ChangeStep> Steps { get; } = [];

        public void Step(ChangeStep step) => Steps.Add(step);
    }

    private sealed class Standing(bool elevated, string sid, params AccountFolder[] accounts) : IHelperStanding
    {
        public bool IsElevated => elevated;
        public string CurrentSid => sid;
        public IReadOnlyList<AccountFolder> Accounts() => accounts;
    }

    private sealed class Exited(int code) : IElevatedProcess
    {
        public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => Task.FromResult(code);

        public void Dispose()
        {
        }
    }

    /// <summary>An elevation that starts nothing real: it counts its calls and runs what the test gives it as the helper.</summary>
    private sealed class FakeElevation(Func<HelperCommand, int> helper) : IElevation
    {
        public int Calls;
        public ElevationStart? Fixed;
        public List<HelperCommand> Commands { get; } = [];

        public Task<ElevationStart> StartAsync(HelperCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            Commands.Add(command);
            return Task.FromResult(Fixed ?? ElevationStart.Running(new Exited(helper(command))));
        }
    }

    private static string Invocation() => HelperFiles.NewInvocation();

    private OptimisationRegistry RegistryOf(Item item) => new OptimisationRegistry().Register(item);

    private HelperRequest ApplyRequest(Item item, string invocation, string? sid = null, string? folder = null, Proposal? proposal = null) =>
        new(invocation, HelperFiles.Apply, item.Id, sid ?? UserSid, folder ?? _user, item.Target, proposal ?? item.Propose().Value, null, null);

    private HelperRequest UndoRequest(Item item, string invocation, string backup, string restore) =>
        new(invocation, HelperFiles.Undo, item.Id, UserSid, _user, item.Target, null, backup, restore);

    private static string[] Args(string operation, string itemId, string invocation) => [HelperCommand.Switch, operation, itemId, invocation];

    private Standing AdminStanding() => new(true, AdminSid, new AccountFolder(UserSid, _user), new AccountFolder(AdminSid, _admin));

    /// <summary>Writes the request into the user's folder, runs the helper as the administrator account, and gives its exit code.</summary>
    private int RunHelper(Item item, HelperRequest request, string? operation = null, string? itemId = null, IHelperStanding? standing = null, PanelSettingsFiles? files = null)
    {
        new HelperFiles(_user).WriteRequest(request);
        return ElevatedHelper.Run(Args(operation ?? request.Operation, itemId ?? request.ItemId, request.Invocation), standing ?? AdminStanding(), RegistryOf(item), files);
    }

    private HelperResult ResultOf(HelperRequest request)
    {
        var read = new HelperFiles(_user).ReadResult(request.Invocation, request.Operation, request.ItemId);
        Assert.True(read.Problem is null, read.Problem);
        return read.Result!;
    }

    private ChangeCoordinator Coordinator(Item item, FakeElevation elevation, RecordingLog log, out BackupStore store)
    {
        store = new BackupStore(_user);
        return new ChangeCoordinator(new ChangeRunner(store, log), store, log, elevation, new HelperFiles(_user), UserSid, _user);
    }

    /// <summary>An elevation whose helper is the real engine, running as the administrator account.</summary>
    private FakeElevation RealHelper(Item item) =>
        new(command => ElevatedHelper.Run(command.Arguments, AdminStanding(), RegistryOf(item)));

    // ---- the registry and eligibility ----

    [Fact]
    public void The_registry_resolves_a_registered_item_and_refuses_an_unknown_id()
    {
        var item = new Item();
        var registry = RegistryOf(item);

        Assert.Same(item, registry.Resolve("disposable.item"));
        Assert.Null(registry.Resolve("nothing.here"));
        Assert.Null(registry.Resolve(null));
        // The target and the proposal come from the item the registry holds.
        Assert.Equal(new Proposal("disposable:Setting", "1", "2"), registry.Resolve("disposable.item")!.Propose().Value);
        Assert.Throws<ArgumentException>(() => registry.Register(new Item()));
        Assert.Throws<ArgumentException>(() => registry.Register(new Item(id: "../escape")));
    }

    [Fact]
    public void An_elevated_action_needs_an_administrator_and_a_stale_one_is_a_refusal()
    {
        var item = new Item();
        var runner = new ChangeRunner(new BackupStore(_user), new RecordingLog());
        var shown = item.Propose().Value!;

        var ready = runner.CanApply(item, shown);
        Assert.True(ready.NeedsAdministrator);
        Assert.False(ready.Allowed);
        Assert.True(ready.Ready);
        Assert.Equal("Needs an administrator prompt", ready.Reason);

        item.Value = "7"; // the setting changed since the preview: a refusal, never a prompt
        var stale = runner.CanApply(item, shown);
        Assert.False(stale.NeedsAdministrator);
        Assert.False(stale.Ready);
        Assert.Equal("The setting changed since the preview", stale.Reason);
        Assert.False(runner.CanApply(item, null).NeedsAdministrator);

        // An item that needs no administrator is simply allowed; and the helper's own elevated runner allows an elevated one.
        Assert.Equal(Eligibility.Yes, runner.CanApply(new Item(elevated: false), new Item(elevated: false).Propose().Value));
        Assert.Equal(Eligibility.Yes, new ChangeRunner(new BackupStore(_user), new RecordingLog(), elevated: true).CanApply(new Item(), new Item().Propose().Value));
    }

    // ---- the shared path prompts once ----

    [Fact]
    public async Task A_confirmed_eligible_action_prompts_exactly_once_and_a_stale_or_unconfirmed_one_never()
    {
        var item = new Item();
        var log = new RecordingLog();
        var elevation = RealHelper(item);
        var coordinator = Coordinator(item, elevation, log, out _);
        var shown = item.Propose().Value!;

        var done = await coordinator.ApplyAsync(item, shown, TestContext.Current.CancellationToken);

        Assert.Equal(1, elevation.Calls);
        Assert.Equal(ChangeStatus.Changed, done.Status);
        Assert.Equal("2", item.Value);
        Assert.Equal(new HelperCommand("apply", "disposable.item", elevation.Commands[0].Invocation), elevation.Commands[0]);

        // Stale: the setting moved since the preview. Unconfirmed: no preview at all. Neither prompts.
        item.Value = "5";
        var stale = await coordinator.ApplyAsync(item, shown, TestContext.Current.CancellationToken);
        var unconfirmed = await coordinator.ApplyAsync(item, null, TestContext.Current.CancellationToken);
        Assert.Equal(1, elevation.Calls);
        Assert.Equal("Not changed: The setting changed since the preview", stale.Message);
        Assert.Equal("Not changed: Preview it first", unconfirmed.Message);
        Assert.Equal("5", item.Value);

        // An item that needs no administrator never calls it.
        var plain = new Item(id: "plain.item", elevated: false);
        var plainElevation = RealHelper(plain);
        var changed = await Coordinator(plain, plainElevation, log, out _).ApplyAsync(plain, plain.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal(0, plainElevation.Calls);
        Assert.Equal(ChangeStatus.Changed, changed.Status);
    }

    [Fact]
    public async Task A_stale_or_unconfirmed_undo_never_prompts_and_a_confirmed_one_prompts_once()
    {
        var item = new Item();
        var log = new RecordingLog();
        var elevation = RealHelper(item);
        var coordinator = Coordinator(item, elevation, log, out _);
        var applied = await coordinator.ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal(1, elevation.Calls);

        Assert.Equal("Not changed: Nothing to undo", (await coordinator.UndoAsync(item, null, "1", TestContext.Current.CancellationToken)).Message);
        item.Value = "9"; // moved since: undoing would overwrite a newer value
        var stale = await coordinator.UndoAsync(item, applied.Backup, "1", TestContext.Current.CancellationToken);
        Assert.StartsWith("Not changed: The setting changed since this change", stale.Message);
        Assert.Equal(1, elevation.Calls);

        item.Value = "2";
        var undone = await coordinator.UndoAsync(item, applied.Backup, "1", TestContext.Current.CancellationToken);
        Assert.Equal(2, elevation.Calls);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public async Task A_direct_undo_restores_only_the_backup_holding_the_confirmed_value()
    {
        // Round 2: the direct path ignored the confirmed value, so with two eligible backups (1 to 3 and 2 to 3) the
        // second one restored 2 although 1 was confirmed.
        var item = new Item(id: "plain.item", elevated: false);
        var log = new RecordingLog();
        var coordinator = Coordinator(item, new FakeElevation(_ => 0), log, out _);
        item.After = "3";
        item.Value = "1";
        var first = await coordinator.ApplyAsync(item, item.Propose().Value!, TestContext.Current.CancellationToken);
        item.Value = "2";
        var second = await coordinator.ApplyAsync(item, item.Propose().Value!, TestContext.Current.CancellationToken);
        Assert.Equal("3", item.Value);

        var refused = await coordinator.UndoAsync(item, second.Backup, "1", TestContext.Current.CancellationToken);
        Assert.False(refused.Written);
        Assert.Contains("restores '2', not the '1' that was confirmed", refused.Message);
        Assert.Equal("3", item.Value);

        var undone = await coordinator.UndoAsync(item, first.Backup, "1", TestContext.Current.CancellationToken);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public async Task A_direct_undo_checks_the_confirmed_value_against_the_backup_the_runner_restores_from_even_when_the_first_read_failed()
    {
        // Round 3: the eligibility read reported the setting unavailable, so the direct path skipped the confirmed-value
        // check, and the runner (whose own read then succeeded) restored the backup that held another value.
        var item = new Item(id: "plain.item", elevated: false);
        var coordinator = Coordinator(item, new FakeElevation(_ => 0), new RecordingLog(), out _);
        item.After = "3";
        item.Value = "1";
        await coordinator.ApplyAsync(item, item.Propose().Value!, TestContext.Current.CancellationToken);
        item.Value = "2";
        var second = await coordinator.ApplyAsync(item, item.Propose().Value!, TestContext.Current.CancellationToken);
        Assert.Equal("3", item.Value);
        var writes = item.Writes;

        item.UnavailableReads = 1;
        var refused = await coordinator.UndoAsync(item, second.Backup, "1", TestContext.Current.CancellationToken);

        Assert.False(refused.Written);
        Assert.Contains("restores '2', not the '1' that was confirmed", refused.Message);
        Assert.Equal("3", item.Value);
        Assert.Equal(writes, item.Writes);
    }

    [Fact]
    public async Task A_declined_or_failed_start_leaves_no_request_behind()
    {
        // Round 2: each declined prompt left another request, holding the proposal, in the user's data folder.
        var item = new Item();
        var log = new RecordingLog();
        foreach (var start in new[] { ElevationStart.Declined, ElevationStart.Failed("the file is gone") })
        {
            await Coordinator(item, new FakeElevation(_ => 0) { Fixed = start }, log, out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
            Assert.Empty(Directory.Exists(new HelperFiles(_user).Folder) ? Directory.GetFiles(new HelperFiles(_user).Folder, "*.request.json") : []);
        }
    }

    [Fact]
    public async Task A_stale_confirmed_restore_value_is_refused_with_no_administrator_prompt()
    {
        // Round 8: the helper caught a stale confirmation only after the prompt; the panel can refuse it before.
        var item = new Item();
        var log = new RecordingLog();
        var elevation = RealHelper(item);
        var coordinator = Coordinator(item, elevation, log, out _);
        var applied = await coordinator.ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal(1, elevation.Calls);

        var stale = await coordinator.UndoAsync(item, applied.Backup, "7", TestContext.Current.CancellationToken);

        Assert.Equal(1, elevation.Calls);                                // no second prompt
        Assert.False(stale.Written);
        Assert.Contains("not the '7' that was confirmed", stale.Message);
        Assert.Equal("2", item.Value);
    }

    [Fact]
    public async Task A_declined_prompt_reaches_the_activity_as_not_changed()
    {
        var item = new Item();
        var log = new RecordingLog();
        var elevation = new FakeElevation(_ => 0) { Fixed = ElevationStart.Declined };

        var result = await Coordinator(item, elevation, log, out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(1, elevation.Calls);
        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Equal("Not changed: you declined the administrator prompt", result.Message);
        Assert.Equal("1", item.Value);
        Assert.Equal(["Administrator", "Refused"], log.Steps.Select(s => s.Step));
        Assert.Equal("Waiting for the administrator prompt", log.Steps[0].Outcome);
        Assert.Equal((ChangeLevel.Warn, "Not changed: you declined the administrator prompt"), (log.Steps[1].Level, log.Steps[1].Outcome));

        var failed = await Coordinator(item, new FakeElevation(_ => 0) { Fixed = ElevationStart.Failed("the file is gone") }, log, out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal("Not changed: the administrator helper could not be started: the file is gone", failed.Message);
    }

    [Fact]
    public async Task The_helpers_steps_reach_the_log_in_order_after_it_exits()
    {
        var item = new Item();
        var log = new RecordingLog();
        var result = await Coordinator(item, RealHelper(item), log, out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.Changed, result.Status);
        Assert.True(result.UndoOffered);
        Assert.Equal(["Administrator", "Administrator", "Started", "Backup", "Write returned", "Verify", "Result"], log.Steps.Select(s => s.Step));
        Assert.Equal(["Waiting for the administrator prompt", "Applying"], log.Steps.Take(2).Select(s => s.Outcome));
    }

    // ---- the helper's own standing ----

    [Fact]
    public void An_unelevated_helper_refuses_and_writes_nothing()
    {
        var item = new Item();
        var request = ApplyRequest(item, Invocation());

        var code = RunHelper(item, request, standing: new Standing(false, AdminSid, new AccountFolder(UserSid, _user)));

        Assert.Equal(ElevatedHelper.ExitNotElevated, code);
        Assert.Equal("1", item.Value);
        Assert.False(File.Exists(new HelperFiles(_user).ResultPath(request.Invocation)));
        Assert.False(Directory.Exists(Path.Combine(_user, "backups")));
    }

    [Fact]
    public void A_request_naming_a_redirected_folder_is_refused_and_nothing_is_written_there()
    {
        var item = new Item();
        var elsewhere = Folder("redirected");
        var request = ApplyRequest(item, Invocation(), folder: elsewhere);

        var code = RunHelper(item, request);

        Assert.Equal(ElevatedHelper.ExitRefused, code);
        Assert.Equal("1", item.Value);
        Assert.False(Directory.Exists(elsewhere));
        Assert.False(Directory.Exists(_admin));
        Assert.Contains("data folder", ResultOf(request).Refusal);
        Assert.False(Directory.Exists(Path.Combine(_user, "backups")));
    }

    [Fact]
    public void A_request_found_in_a_folder_no_account_owns_is_never_looked_at()
    {
        var item = new Item();
        var unrelated = Folder("unrelated");
        var request = ApplyRequest(item, Invocation(), folder: unrelated);
        new HelperFiles(unrelated).WriteRequest(request);

        var code = ElevatedHelper.Run(Args("apply", item.Id, request.Invocation), AdminStanding(), RegistryOf(item));

        Assert.Equal(ElevatedHelper.ExitNoRequest, code);
        Assert.Equal("1", item.Value);
        Assert.Equal([Path.Combine(unrelated, "helper")], Directory.GetDirectories(unrelated));
        Assert.Empty(Directory.GetFiles(Path.Combine(unrelated, "helper"), "*.result.json"));
        Assert.False(Directory.Exists(_user));
    }

    [Fact]
    public void A_request_found_in_one_account_that_names_another_account_is_refused()
    {
        var item = new Item();
        var request = ApplyRequest(item, Invocation(), sid: AdminSid);

        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, request));
        Assert.Equal("1", item.Value);
        Assert.Contains("another account", ResultOf(request).Refusal);
    }

    [Fact]
    public void A_request_present_in_two_accounts_is_ambiguous_and_refused()
    {
        var item = new Item();
        var request = ApplyRequest(item, Invocation());
        new HelperFiles(_user).WriteRequest(request);
        new HelperFiles(_admin).WriteRequest(request with { InitiatorSid = AdminSid, DataFolder = _admin });

        Assert.Equal(ElevatedHelper.ExitNoRequest, ElevatedHelper.Run(Args("apply", item.Id, request.Invocation), AdminStanding(), RegistryOf(item)));
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public void A_helper_run_by_another_administrator_writes_into_the_initiating_users_folder_only()
    {
        var item = new Item();
        var request = ApplyRequest(item, Invocation());

        var code = RunHelper(item, request); // runs as AdminSid, for the panel of UserSid

        Assert.Equal(ElevatedHelper.ExitDone, code);
        Assert.Equal("2", item.Value);
        var result = ResultOf(request);
        Assert.Equal(ChangeStatus.Changed, result.Final!.Status);
        Assert.Contains(AdminSid, result.Steps[0].Outcome);
        Assert.Contains(UserSid, result.Steps[0].Outcome);
        // The backup, its applied marker and the result are in the initiating user's folder; the administrator's own folder was never made.
        var backups = new BackupStore(_user).List(item.Id, item.Value);
        Assert.Equal([result.Final.Backup], backups.Select(b => b.Ref.Name));
        Assert.Equal(BackupState.Applied, backups[0].State);
        Assert.False(Directory.Exists(_admin));
        Assert.All(Directory.EnumerateFileSystemEntries(_user, "*", SearchOption.AllDirectories), path => Assert.StartsWith(_user, path));
    }

    // ---- the request is bound to what the person confirmed ----

    [Fact]
    public void A_changed_proposal_or_an_in_range_substituted_value_is_refused_before_any_write()
    {
        var item = new Item();
        var shown = item.Propose().Value!;

        // The value the person saw was '2'; the request says '3': still a plausible value, and not what was confirmed.
        var substituted = ApplyRequest(item, Invocation(), proposal: shown with { After = "3" });
        Assert.Equal(ElevatedHelper.ExitNotChanged, RunHelper(item, substituted));
        Assert.Equal(ChangeStatus.NotChanged, ResultOf(substituted).Final!.Status);

        var moved = ApplyRequest(item, Invocation(), proposal: shown with { Before = "0" });
        Assert.Equal(ElevatedHelper.ExitNotChanged, RunHelper(item, moved));

        var other = ApplyRequest(item, Invocation(), proposal: shown with { Target = "disposable:Other" });
        Assert.Equal(ElevatedHelper.ExitNotChanged, RunHelper(item, other));

        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
        Assert.False(Directory.Exists(Path.Combine(_user, "backups")));
    }

    [Fact]
    public void An_unknown_id_a_mismatched_operation_and_another_target_are_refused()
    {
        var item = new Item();

        var unknown = ApplyRequest(item, Invocation()) with { ItemId = "other.item" };
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, unknown));
        Assert.Contains("not an item", ResultOf(unknown).Refusal);

        var wrongOperation = ApplyRequest(item, Invocation());
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, wrongOperation, operation: HelperFiles.Undo));
        // The result is bound to the command line's operation, which is what the panel asked for.
        Assert.Contains("'apply', not 'undo'", new HelperFiles(_user).ReadResult(wrongOperation.Invocation, "undo", item.Id).Result!.Refusal);

        var wrongItem = ApplyRequest(item, Invocation());
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, wrongItem, itemId: "second.item"));

        var wrongTarget = ApplyRequest(item, Invocation()) with { Target = "disposable:Other" };
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, wrongTarget));
        Assert.Contains("another setting", ResultOf(wrongTarget).Refusal);

        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
    }

    [Fact]
    public async Task An_undo_is_by_the_confirmed_backup_and_value_and_the_undo_can_be_undone_by_its_own_backup()
    {
        var item = new Item();
        var log = new RecordingLog();
        var coordinator = Coordinator(item, RealHelper(item), log, out var store);
        var applied = await coordinator.ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal("2", item.Value);

        // A second valid backup of the same target: it also takes the value from '2', to '0' (its before value).
        var second = store.Write(item.Id, BackupStore.Apply, new Proposal(item.Target, "0", "2"));
        Assert.True(new ChangeRunner(store, log, elevated: true).CanUndo(item, second).Allowed);
        Assert.True(new ChangeRunner(store, log, elevated: true).CanUndo(item, applied.Backup).Allowed);

        // The person confirmed the first backup, restoring '1'. A substituted backup is refused:
        var substituted = UndoRequest(item, Invocation(), second.Name, "1");
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, substituted));
        Assert.Contains("'0', not the '1' that was confirmed", ResultOf(substituted).Refusal);
        // an in-range substituted value for the right backup is refused:
        var value = UndoRequest(item, Invocation(), applied.Backup!.Name, "0");
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, value));
        // and a backup of another target, even one that restores the confirmed value, is refused:
        var foreign = store.Write(item.Id, BackupStore.Apply, new Proposal("disposable:Other", "1", "2"));
        var changedTarget = UndoRequest(item, Invocation(), foreign.Name, "1");
        Assert.Equal(ElevatedHelper.ExitRefused, RunHelper(item, changedTarget));
        Assert.Contains("another setting", ResultOf(changedTarget).Refusal);
        Assert.Equal("2", item.Value);
        Assert.Equal(1, item.Writes);

        // Undone by the confirmed one:
        var undone = await coordinator.UndoAsync(item, applied.Backup, "1", TestContext.Current.CancellationToken);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value);
        Assert.NotEqual(applied.Backup, undone.Backup);

        // and that Undo is undone by the backup it produced, whose restore value is what the target held before it.
        var again = await coordinator.UndoAsync(item, undone.Backup, "2", TestContext.Current.CancellationToken);
        Assert.Equal(ChangeStatus.Restored, again.Status);
        Assert.Equal("2", item.Value);
    }

    // ---- the steps, and the result ----

    /// <summary>Remembers each version of a result file the moment it is put in place.</summary>
    private sealed class SpyFiles : PanelSettingsFiles
    {
        public List<HelperResult> Results { get; } = [];

        public override void Replace(string from, string to)
        {
            if (to.EndsWith(".result.json", StringComparison.Ordinal)) Results.Add(HelperFiles.ParseResult(Read(from, HelperFiles.MaxFileBytes)));
            base.Replace(from, to);
        }
    }

    [Fact]
    public void Each_step_is_recorded_whole_as_it_happens_and_before_the_next()
    {
        var item = new Item();
        var spy = new SpyFiles();
        var request = ApplyRequest(item, Invocation());

        Assert.Equal(ElevatedHelper.ExitDone, RunHelper(item, request, files: spy));

        // Each version holds one more step than the last, none is the end until the last, and the backup is named before the write.
        Assert.Equal(["Started", "Backup", "Write returned", "Verify", "Result"], spy.Results[^1].Steps.Select(s => s.Step));
        Assert.Equal(Enumerable.Range(1, 5).Concat([5]), spy.Results.Select(r => r.Steps.Count));
        Assert.All(spy.Results.Take(5), r => Assert.False(r.Complete));
        Assert.True(spy.Results[^1].Complete);
        var backupAt = spy.Results.First(r => r.LastBackup is not null);
        Assert.Equal(["Started", "Backup"], backupAt.Steps.Select(s => s.Step));
        Assert.Equal(spy.Results[^1].Final!.Backup, backupAt.LastBackup);
    }

    [Fact]
    public void A_fault_in_the_helper_is_written_to_its_result_and_exits_with_a_code()
    {
        var item = new Item { Explode = true };
        var request = ApplyRequest(new Item(), Invocation());

        var code = RunHelper(item, request);

        Assert.Equal(ElevatedHelper.ExitFaulted, code);
        var result = ResultOf(request);
        Assert.Contains("the title faults", result.Fault);
        Assert.Null(result.Final);
        Assert.Equal("1", item.Value);
    }

    private ChangeCoordinator CoordinatorWith(Item item, Func<HelperCommand, HelperFiles, int> helper, RecordingLog log, out BackupStore store)
    {
        var files = new HelperFiles(_user);
        return Coordinator(item, new FakeElevation(command => helper(command, files)), log, out store);
    }

    private static HelperResult Partial(HelperCommand command, params HelperStep[] steps) =>
        new(command.Invocation, command.Operation, command.ItemId, steps, null, null, null);

    private static readonly HelperStep Started = new("Started", ChangeLevel.Info, "Helper running", null);
    private static readonly HelperStep BackedUp = new("Backup", ChangeLevel.Info, "Backed up", "20261119T000000000Z-apply.json");

    [Fact]
    public async Task A_missing_truncated_or_unrelated_result_is_said_and_never_taken_for_a_success()
    {
        var item = new Item();
        var shown = item.Propose().Value!;

        // Missing: the helper exited 0 and left nothing.
        var missing = await CoordinatorWith(item, (_, _) => 0, new RecordingLog(), out _).ApplyAsync(item, shown, TestContext.Current.CancellationToken);
        Assert.Equal(ChangeStatus.CouldNotVerify, missing.Status);
        Assert.StartsWith("Could not be verified: The administrator helper ended without a whole result (there is no result file;", missing.Message);
        Assert.Contains("exit code 0", missing.Message);
        Assert.False(missing.UndoOffered);

        // Truncated: half a file, as a foreign write could leave.
        var truncated = await CoordinatorWith(item, (c, files) =>
        {
            var bytes = HelperFiles.SerializeResult(Partial(c, Started));
            Directory.CreateDirectory(files.Folder);
            File.WriteAllBytes(files.ResultPath(c.Invocation), bytes[..(bytes.Length / 2)]);
            return 0;
        }, new RecordingLog(), out _).ApplyAsync(item, shown, TestContext.Current.CancellationToken);
        Assert.Contains("not whole", truncated.Message);
        Assert.Equal(ChangeStatus.CouldNotVerify, truncated.Status);

        // Stale or unrelated: a whole result, but bound to another invocation, even one that claims success.
        var log = new RecordingLog();
        var mismatched = await CoordinatorWith(item, (c, files) =>
        {
            var other = new HelperResult(Invocation(), c.Operation, c.ItemId, [Started], new HelperFinal(ChangeStatus.Changed, "Changed", null, true, "2", false), null, null);
            files.WriteResult(other);
            File.Move(files.ResultPath(other.Invocation), files.ResultPath(c.Invocation));
            return 0;
        }, log, out _).ApplyAsync(item, shown, TestContext.Current.CancellationToken);
        Assert.Contains("belongs to another invocation", mismatched.Message);
        Assert.NotEqual(ChangeStatus.Changed, mismatched.Status);
        Assert.DoesNotContain(log.Steps, s => s.Step == "Started"); // nothing from an unrelated result is replayed

        // A result for another item or operation is not this one's either.
        var otherItem = await CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, "other.item", [Started], new HelperFinal(ChangeStatus.Changed, "Changed", null, true, "2", false), null, null));
            return 0;
        }, new RecordingLog(), out _).ApplyAsync(item, shown, TestContext.Current.CancellationToken);
        Assert.Contains("another operation or item", otherItem.Message);
    }

    [Fact]
    public async Task A_helper_that_reports_a_change_the_target_does_not_hold_is_not_believed()
    {
        var item = new Item();
        var result = await CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [Started, BackedUp], new HelperFinal(ChangeStatus.Changed, "Changed: '1' to '2'", BackedUp.Backup, true, "2", false), null, null));
            return 0; // it says it wrote '2' and wrote nothing
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        // The target still holds the confirmed before value: nothing was written, whatever the helper said (round 8).
        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Null(result.Backup);
        Assert.Contains("the setting still reads '1'", result.Message);
    }

    [Fact]
    public async Task A_success_with_no_backup_recorded_before_its_write_is_not_believed()
    {
        // Round 5: exit 0, Changed, the confirmed value, written false, no backup and no steps was taken as a change.
        var item = new Item();
        var result = await CoordinatorWith(item, (c, files) =>
        {
            item.Value = "2";   // the setting even reads the confirmed value
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [], new HelperFinal(ChangeStatus.Changed, "Changed", null, false, "2", false), null, null));
            return 0;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.Contains("no backup of this change is in the panel's backups", result.Message);
    }

    [Fact]
    public async Task A_success_whose_backup_appears_only_after_the_write_is_not_believed()
    {
        // Round 6: a history holding only a Verify step that names the backup was taken as backed up first.
        var item = new Item();
        var named = BackedUp.Backup;
        foreach (var steps in new HelperStep[][]
                 {
                     [new("Verify", ChangeLevel.Info, "Reads '2'", named)],
                     [new("Write returned", ChangeLevel.Info, "Apply returned", named), BackedUp],
                 })
        {
            var result = await CoordinatorWith(item, (c, files) =>
            {
                item.Value = "2";
                files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, steps, new HelperFinal(ChangeStatus.Changed, "Changed", named, true, "2", false), null, null));
                return 0;
            }, new RecordingLog(), out _).ApplyAsync(item, new Proposal(item.Target, "1", "2"), TestContext.Current.CancellationToken);
            item.Value = "1";
            Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
            Assert.Contains("no backup of this change is in the panel's backups", result.Message);
        }
    }

    [Fact]
    public async Task A_failed_verification_reported_as_not_changed_is_unverified_when_the_target_holds_the_new_value()
    {
        // Round 7 #1: backup, write returned, failed verify, final NotChanged written false, exit 1; the target holds '2'.
        var item = new Item();
        var log = new RecordingLog();
        var result = await CoordinatorWith(item, (c, files) =>
        {
            var backup = new BackupStore(_user).Write(item.Id, BackupStore.Apply, item.Propose().Value!);
            item.Value = "2";
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId,
                [Started, new("Backup", ChangeLevel.Info, "Written and read back", backup.Name), new("Write returned", ChangeLevel.Info, "Apply returned", backup.Name), new("Verify", ChangeLevel.Fail, "Reads '2'", backup.Name)],
                new HelperFinal(ChangeStatus.NotChanged, "Not changed", null, false, null, false), null, null));
            return ElevatedHelper.ExitNotChanged;
        }, log, out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.True(result.Written);
        Assert.NotNull(result.Backup);
        Assert.True(result.UndoOffered);
        Assert.Contains($"Undo is offered from backup {result.Backup!.Name}", result.Message);
    }

    [Fact]
    public async Task A_successful_result_naming_a_backup_the_panel_does_not_hold_is_unverified_and_returns_no_backup()
    {
        // Round 7 #2: only a Backup step, naming a backup that is not in the store, and the target reads the new value.
        var item = new Item();
        var result = await CoordinatorWith(item, (c, files) =>
        {
            item.Value = "2";
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [BackedUp], new HelperFinal(ChangeStatus.Changed, "Changed", BackedUp.Backup, true, "2", false), null, null));
            return ElevatedHelper.ExitDone;
        }, new RecordingLog(), out _).ApplyAsync(item, new Proposal(item.Target, "1", "2"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.Null(result.Backup);
        Assert.False(result.UndoOffered);
    }

    [Fact]
    public async Task A_successful_result_naming_a_backup_of_another_proposal_is_unverified()
    {
        var item = new Item();
        var result = await CoordinatorWith(item, (c, files) =>
        {
            var foreign = new BackupStore(_user).Write(item.Id, BackupStore.Apply, new Proposal(item.Target, "0", "2"));   // a real backup, of another change
            item.Value = "2";
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId,
                [Started, new("Backup", ChangeLevel.Info, "Written and read back", foreign.Name)],
                new HelperFinal(ChangeStatus.Changed, "Changed", foreign.Name, true, "2", false), null, null));
            return ElevatedHelper.ExitDone;
        }, new RecordingLog(), out _).ApplyAsync(item, new Proposal(item.Target, "1", "2"), TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.Null(result.Backup);
        Assert.False(result.UndoOffered);
    }

    [Fact]
    public async Task A_restored_result_with_no_backup_of_the_undo_in_the_store_is_unverified_even_when_the_target_reads_the_restored_value()
    {
        var item = new Item();
        var log = new RecordingLog();
        var coordinator = CoordinatorWith(item, (c, files) =>
        {
            item.Value = "1";
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [Started, BackedUp], new HelperFinal(ChangeStatus.Restored, "Restored", BackedUp.Backup, true, "1", false), null, null));
            return ElevatedHelper.ExitDone;
        }, log, out var store);
        var applied = new ChangeRunner(store, log, elevated: true).Apply(item, item.Propose().Value!);

        var result = await coordinator.UndoAsync(item, applied.Backup, "1", TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.NotEqual(ChangeStatus.Restored, result.Status);
    }

    [Fact]
    public void A_result_whose_text_can_not_be_decoded_is_unreadable_never_an_exception()
    {
        // Round 5: an unpaired surrogate parsed, then GetString threw out of the reader.
        var files = new HelperFiles(_user);
        var invocation = HelperFiles.NewInvocation();
        files.WriteResult(new HelperResult(invocation, "apply", "disposable.item", [Started], null, null, "boom"));
        var path = Directory.GetFiles(files.Folder, invocation + "*").Single(f => !f.EndsWith(".request.json", StringComparison.Ordinal));
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"disposable.item\"", "\"\\ud800\""));   // the JSON escape, as text
        var read = files.ReadResult(invocation, "apply", "disposable.item");
        Assert.Null(read.Result);
        Assert.NotNull(read.Problem);
    }

    [Fact]
    public async Task A_helper_that_reports_a_restore_of_another_value_than_the_confirmed_one_is_not_believed()
    {
        // Round 4: a complete Restored result with exit 0 and value_now 'B' was taken as it said, though 'A' was confirmed.
        var item = new Item();
        var log = new RecordingLog();
        var coordinator = CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [Started, BackedUp], new HelperFinal(ChangeStatus.Restored, "Restored", BackedUp.Backup, true, "9", false), null, null));
            return 0;   // it says it restored '9' and wrote nothing
        }, log, out var store);
        var applied = new ChangeRunner(store, log, elevated: true).Apply(item, item.Propose().Value!);
        Assert.Equal("2", item.Value);

        var result = await coordinator.UndoAsync(item, applied.Backup, "1", TestContext.Current.CancellationToken);

        // The target still holds the value the Undo started from: nothing was restored, whatever the helper said (round 8).
        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Contains("the setting still reads '2'", result.Message);
        Assert.Equal("2", item.Value);
    }

    [Fact]
    public async Task A_refused_or_faulted_helper_result_is_said_as_it_is()
    {
        var item = new Item();
        var refused = await CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [Started], null, "The request is for another invocation", null));
            return ElevatedHelper.ExitRefused;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Equal("Not changed: the administrator helper refused: The request is for another invocation", refused.Message);
        Assert.False(refused.Written);

        var faulted = await CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [Started], null, null, "IOException: boom"));
            return ElevatedHelper.ExitFaulted;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        Assert.Contains("The administrator helper failed: IOException: boom", faulted.Message);
        Assert.Equal(ChangeStatus.NotChanged, faulted.Status); // no backup was named: no write was made
    }

    // ---- recovery after a lost helper, each with an older backup already present ----

    /// <summary>A change made earlier, by the helper itself: the older backup a lost helper must never be mistaken for.</summary>
    private async Task<BackupRef> EarlierChange(Item item)
    {
        var earlier = await Coordinator(item, RealHelper(item), new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);
        item.Value = "1"; // and the setting is back where the next change starts (by hand, so the old backup is no longer eligible)
        return earlier.Backup!;
    }

    [Fact]
    public async Task A_helper_lost_before_its_backup_offers_no_undo_even_with_an_older_backup_present()
    {
        var item = new Item();
        var older = await EarlierChange(item);
        var result = await CoordinatorWith(item, (c, files) =>
        {
            files.WriteResult(Partial(c, Started)); // stopped right after it started
            return 9;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.False(result.UndoOffered);
        Assert.Null(result.Backup);
        Assert.Contains("No backup was recorded for this change, so no Undo is offered", result.Message);
        Assert.DoesNotContain(older.Name, result.Message);
    }

    [Fact]
    public async Task A_helper_lost_after_its_backup_but_before_the_write_says_nothing_was_written()
    {
        var item = new Item();
        var older = await EarlierChange(item);
        var result = await CoordinatorWith(item, (c, files) =>
        {
            var proposal = item.Propose().Value!;
            var backup = new BackupStore(_user).Write(item.Id, BackupStore.Apply, proposal);
            files.WriteResult(Partial(c, Started, new HelperStep("Backup", ChangeLevel.Info, "Written and read back", backup.Name)));
            return 9;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.False(result.UndoOffered);
        Assert.Contains("Undo is not offered: The setting already holds the value this backup would restore", result.Message);
        Assert.NotEqual(older.Name, result.Backup!.Name);
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public async Task A_helper_lost_after_the_write_offers_undo_for_its_own_backup_only()
    {
        var item = new Item();
        var older = await EarlierChange(item);
        var store = new BackupStore(_user);
        var result = await CoordinatorWith(item, (c, files) =>
        {
            var backup = store.Write(item.Id, BackupStore.Apply, item.Propose().Value!);
            item.Apply("1", "2"); // the write ran; no applied marker, no verification, no final record
            files.WriteResult(Partial(c, Started, new HelperStep("Backup", ChangeLevel.Info, "Written and read back", backup.Name)));
            return 9;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.True(result.Written);
        Assert.True(result.UndoOffered);
        Assert.NotEqual(older.Name, result.Backup!.Name);
        Assert.Contains($"Undo is offered from backup {result.Backup.Name}", result.Message);
        Assert.Equal("2", item.Value);

        // The target changed again since: Undo would destroy the newer value, so it is no longer offered, and the reason is said.
        item.Value = "8";
        var moved = await CoordinatorWith(item, (c, files) =>
        {
            // The helper's own backup of this change (8 to 2): the write ran, and another program moved the setting on.
            var backup = store.Write(item.Id, BackupStore.Apply, new Proposal(item.Target, "8", "2"));
            item.Apply("8", "2");
            item.Value = "9";
            files.WriteResult(Partial(c, Started, new HelperStep("Backup", ChangeLevel.Info, "Written and read back", backup.Name)));
            return 9;
        }, new RecordingLog(), out _).ApplyAsync(item, new Proposal(item.Target, "8", "2"), TestContext.Current.CancellationToken);
        Assert.False(moved.UndoOffered);
        Assert.Contains("Undo is not offered: The setting changed since this change", moved.Message);
    }

    // ---- review round 1 (D05 T02 §6) ----

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string newName, string existing, IntPtr security);

    private static void Junction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c");
        info.ArgumentList.Add("mklink");
        info.ArgumentList.Add("/J");
        info.ArgumentList.Add(link);
        info.ArgumentList.Add(target);
        using var process = System.Diagnostics.Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void The_helper_writes_its_result_through_a_new_file_and_never_through_the_predictable_temp_name()
    {
        var item = new Item();
        var request = ApplyRequest(item, Invocation());
        var helperFiles = new HelperFiles(_user);
        helperFiles.WriteRequest(request);

        // Something else already sits at the old temporary name: a link to a file the elevated token could damage.
        var sentinel = Path.Combine(Folder("sentinel"), "precious.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(sentinel)!);
        File.WriteAllText(sentinel, "precious");
        var oldTemporary = helperFiles.ResultPath(request.Invocation) + ".tmp";
        if (!CreateHardLink(oldTemporary, sentinel, IntPtr.Zero)) File.WriteAllText(oldTemporary, "precious"); // not NTFS: a plain file at that name still shows it

        var exit = ElevatedHelper.Run(Args(request.Operation, request.ItemId, request.Invocation), AdminStanding(), RegistryOf(item));

        Assert.Equal(ElevatedHelper.ExitDone, exit);
        Assert.Equal(ChangeStatus.Changed, ResultOf(request).Final!.Status);
        Assert.Equal("precious", File.ReadAllText(sentinel));
        Assert.Equal("precious", File.ReadAllText(oldTemporary));
        Assert.Empty(Directory.GetFiles(helperFiles.Folder, "*.result.json.*.tmp")); // its own temporary file is gone
    }

    [Fact]
    public void A_helper_folder_that_a_junction_sends_elsewhere_is_refused_and_nothing_is_written_there()
    {
        var item = new Item();
        var elsewhere = Folder("elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(_user);
        Junction(Path.Combine(_user, "helper"), elsewhere);
        var request = ApplyRequest(item, Invocation());
        new HelperFiles(_user).WriteRequest(request); // the panel's own write lands in the junction's target
        var before = Directory.GetFileSystemEntries(elsewhere).Order().ToArray();

        var exit = ElevatedHelper.Run(Args(request.Operation, request.ItemId, request.Invocation), AdminStanding(), RegistryOf(item));

        Assert.Equal(ElevatedHelper.ExitRefused, exit);
        Assert.Equal(before, Directory.GetFileSystemEntries(elsewhere).Order().ToArray());
        Assert.Equal("1", item.Value);
        Assert.False(Directory.Exists(Path.Combine(_user, "backups")));
    }

    [Fact]
    public void A_backups_folder_that_a_junction_sends_elsewhere_is_refused_and_a_write_through_one_made_later_is_refused_too()
    {
        var item = new Item();
        var elsewhere = Folder("elsewhere-backups");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateDirectory(_user);
        Junction(Path.Combine(_user, "backups"), elsewhere);
        var request = ApplyRequest(item, Invocation());
        new HelperFiles(_user).WriteRequest(request);

        Assert.Equal(ElevatedHelper.ExitRefused, ElevatedHelper.Run(Args(request.Operation, request.ItemId, request.Invocation), AdminStanding(), RegistryOf(item)));
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.Equal("1", item.Value);

        // A junction swapped in after the folder was checked is caught at the write itself.
        var swapped = Folder("swapped");
        Directory.CreateDirectory(Path.Combine(swapped, "data"));
        var outside = Folder("outside");
        Directory.CreateDirectory(outside);
        var files = new ConfinedFiles(new PanelSettingsFiles(), Path.Combine(swapped, "data"));
        Junction(Path.Combine(swapped, "data", "later"), outside);
        Assert.Throws<IOException>(() => files.WriteNew(Path.Combine(swapped, "data", "later", "x.txt"), [1]));
        Assert.Empty(Directory.GetFileSystemEntries(outside));
        files.WriteNew(Path.Combine(swapped, "data", "inside", "x.txt"), [1]); // an ordinary folder inside is fine
    }

    [Fact]
    public async Task A_result_that_does_not_fit_the_operation_and_the_exit_code_is_read_as_unreadable_and_the_target_is_read_again()
    {
        var item = new Item();
        var readsAtExit = 0;
        var result = await CoordinatorWith(item, (c, files) =>
        {
            readsAtExit = item.Reads;
            // An Apply that says Restored, with the exit code of a fault.
            files.WriteResult(new HelperResult(c.Invocation, c.Operation, c.ItemId, [], new HelperFinal(ChangeStatus.Restored, "restored", null, true, "1", false), null, null));
            return ElevatedHelper.ExitFaulted;
        }, new RecordingLog(), out _).ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.NotEqual(ChangeStatus.Restored, result.Status);
        Assert.Contains("does not fit", result.Message);
        Assert.True(item.Reads > readsAtExit, "the target was read again");
    }

    [Fact]
    public void A_result_is_consistent_only_with_its_operation_and_its_exit_code()
    {
        HelperResult Ending(string operation, ChangeStatus status) => new("i", operation, "x", [], new HelperFinal(status, "m", null, true, null, false), null, null);
        Assert.True(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.Changed), "apply", ElevatedHelper.ExitDone));
        Assert.True(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.CouldNotVerify), "apply", ElevatedHelper.ExitNotChanged));
        Assert.True(ElevatedHelper.IsConsistent(Ending("undo", ChangeStatus.Restored), "undo", ElevatedHelper.ExitDone));
        Assert.True(ElevatedHelper.IsConsistent(Ending("undo", ChangeStatus.NotChanged), "undo", ElevatedHelper.ExitNotChanged));
        Assert.False(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.Restored), "apply", ElevatedHelper.ExitDone));
        Assert.False(ElevatedHelper.IsConsistent(Ending("undo", ChangeStatus.Changed), "undo", ElevatedHelper.ExitDone));
        Assert.False(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.Changed), "apply", ElevatedHelper.ExitNotChanged));
        Assert.False(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.NotChanged), "apply", ElevatedHelper.ExitDone));
        Assert.False(ElevatedHelper.IsConsistent(Ending("apply", ChangeStatus.Changed), "undo", ElevatedHelper.ExitDone));
        Assert.True(ElevatedHelper.IsConsistent(new("i", "apply", "x", [], null, "no", null), "apply", ElevatedHelper.ExitRefused));
        Assert.False(ElevatedHelper.IsConsistent(new("i", "apply", "x", [], null, "no", null), "apply", ElevatedHelper.ExitDone));
        Assert.True(ElevatedHelper.IsConsistent(new("i", "apply", "x", [], null, null, "boom"), "apply", ElevatedHelper.ExitFaulted));
        Assert.False(ElevatedHelper.IsConsistent(new("i", "apply", "x", [], Ending("apply", ChangeStatus.Changed).Final, "no", null), "apply", ElevatedHelper.ExitDone));
    }

    private sealed class CancellingElevation : IElevation
    {
        public int Calls;

        public Task<ElevationStart> StartAsync(HelperCommand command, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromCanceled<ElevationStart>(new CancellationToken(true));
        }
    }

    private void AssertCancelledCleanly(ChangeResult result, RecordingLog log)
    {
        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Contains("cancelled", result.Message);
        Assert.Empty(Directory.GetFiles(new HelperFiles(_user).Folder, "*.request.json"));
        Assert.Contains(log.Steps, s => s.Outcome == "Cancelled before the prompt opened");
    }

    [Fact]
    public async Task An_already_cancelled_token_leaves_no_waiting_line_and_no_request_and_returns_an_outcome()
    {
        var item = new Item();
        var elevation = new FakeElevation(_ => 0);
        var log = new RecordingLog();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var result = await Coordinator(item, elevation, log, out _).ApplyAsync(item, item.Propose().Value, cancelled.Token);

        AssertCancelledCleanly(result, log);
        Assert.Equal(0, elevation.Calls);
        Assert.DoesNotContain(log.Steps, s => s.Outcome.Contains("Waiting for the administrator prompt"));
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public async Task A_cancellation_during_the_elevation_start_is_an_outcome_and_removes_the_request()
    {
        var item = new Item();
        var elevation = new CancellingElevation();
        var log = new RecordingLog();
        var store = new BackupStore(_user);

        var result = await new ChangeCoordinator(new ChangeRunner(store, log), store, log, elevation, new HelperFiles(_user), UserSid, _user)
            .ApplyAsync(item, item.Propose().Value, TestContext.Current.CancellationToken);

        Assert.Equal(1, elevation.Calls);
        AssertCancelledCleanly(result, log);
    }

    // ---- review round 3 (D05 T02 §6) ----

    [Fact]
    public void A_profile_whose_local_folder_is_a_junction_is_refused_and_nothing_is_written_through_it()
    {
        var item = new Item();
        var profile = Folder("profile");
        var redirected = Folder("redirected-local");
        Directory.CreateDirectory(Path.Combine(profile, "AppData"));
        Directory.CreateDirectory(redirected);
        Junction(Path.Combine(profile, "AppData", "Local"), redirected);
        var data = Path.Combine(profile, "AppData", "Local", "Bosun");
        var request = new HelperRequest(Invocation(), HelperFiles.Apply, item.Id, UserSid, data, item.Target, item.Propose().Value, null, null);
        new HelperFiles(data).WriteRequest(request); // the panel's own write follows the junction
        var before = Directory.GetFileSystemEntries(redirected, "*", SearchOption.AllDirectories).Order().ToArray();
        var standing = new Standing(true, AdminSid, new AccountFolder(UserSid, data, profile));

        var exit = ElevatedHelper.Run(Args(request.Operation, request.ItemId, request.Invocation), standing, RegistryOf(item));

        Assert.Equal(ElevatedHelper.ExitRefused, exit);
        Assert.Equal(before, Directory.GetFileSystemEntries(redirected, "*", SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal("1", item.Value);
        Assert.Contains("junction or link", FolderConfinement.Problem(data, profile));
        var clean = Folder("clean-profile");
        Directory.CreateDirectory(Path.Combine(clean, "AppData", "Local", "Bosun"));
        Assert.Null(FolderConfinement.Problem(Path.Combine(clean, "AppData", "Local", "Bosun"), clean));
    }

    /// <summary>Between the confinement's check and the helper's create, the helper folder is replaced by a junction to another folder.</summary>
    private sealed class SwappingFiles(string helperFolder, string elsewhere) : PanelSettingsFiles
    {
        public override void WriteNew(string path, byte[] bytes, Func<string, bool>? finalPathAllowed)
        {
            Directory.Move(helperFolder, helperFolder + ".gone");
            Junction(helperFolder, elsewhere);
            base.WriteNew(path, bytes, finalPathAllowed);
        }
    }

    [Fact]
    public void A_folder_swapped_for_a_junction_between_the_check_and_the_create_never_receives_the_write()
    {
        var data = Path.Combine(Folder("race"), "data");
        var helper = Path.Combine(data, "helper");
        Directory.CreateDirectory(helper);
        var elsewhere = Folder("race-elsewhere");
        Directory.CreateDirectory(elsewhere);
        var files = new ConfinedFiles(new SwappingFiles(helper, elsewhere), data);

        Assert.Throws<IOException>(() => files.WriteNew(Path.Combine(helper, "result.tmp"), [1, 2, 3]));

        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    /// <summary>Tries, just before the move, to put a junction where the held folder is.</summary>
    private sealed class SwappingMoves(string folder, string elsewhere) : PanelSettingsFiles
    {
        public bool Swapped;

        public override void Replace(string from, string to)
        {
            try
            {
                Directory.Move(folder, folder + ".gone");
                Junction(folder, elsewhere);
                Swapped = true;
            }
            catch (IOException)
            {
                // the folder is held open by the confinement: it can't be renamed, so it can't be swapped
            }
            base.Replace(from, to);
        }
    }

    [Fact]
    public void A_move_into_a_folder_that_is_swapped_for_a_junction_never_lands_outside()
    {
        var data = Path.Combine(Folder("move"), "data");
        var helper = Path.Combine(data, "helper");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(helper, "a.tmp"), "a");
        var elsewhere = Folder("move-elsewhere");
        Directory.CreateDirectory(elsewhere);
        var inner = new SwappingMoves(helper, elsewhere);

        new ConfinedFiles(inner, data).Replace(Path.Combine(helper, "a.tmp"), Path.Combine(helper, "a.json"));

        Assert.False(inner.Swapped);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
        Assert.True(File.Exists(Path.Combine(helper, "a.json")));
    }
}
