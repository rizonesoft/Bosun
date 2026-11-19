using Bosun.Core.Optimise;
using Xunit;

namespace Bosun.Core.Tests.Optimise;

/// <summary>
/// The change framework (D05 T02 §2): one path that confirms, backs up, applies, verifies and reads again, and an
/// Undo from the exact backup of the change it reverses. Items here are fakes; every store is a folder of its own
/// under the temp folder, never the operator's own data folder.
/// </summary>
public sealed class OptimisationFrameworkTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bosun-framework-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private sealed class FakeItem(string id = "fake-setting", string target = "fake:HKLM\\64\\Setting", string value = "1") : IOptimisation
    {
        public string Value = value;
        public string After = "2";
        public bool ThrowAfterWrite;
        public bool ThrowBeforeWrite;
        public bool VerifyReadsOther;
        public bool VerifyThrows;
        public bool RestoreLies;
        public bool RestoreThrowsAfterWrite;
        public bool ReadFails;
        public bool Elevated;
        public int Writes;

        public string Id => id;
        public string Title => "Fake setting";
        public string Target => target;
        public bool NeedsElevation => Elevated;

        public Fact<string> Read() => ReadFails ? Fact<string>.Unavailable("the fake can't be read") : Fact<string>.Read(Value);

        public Fact<Proposal> Propose() => ReadFails ? Fact<Proposal>.Unavailable("the fake can't be read") : Fact<Proposal>.Read(new Proposal(target, Value, After));

        public void Apply(Proposal proposal)
        {
            if (ThrowBeforeWrite) throw new IOException("denied before writing");
            Writes++;
            Value = proposal.After;
            if (ThrowAfterWrite) throw new IOException("failed after writing");
        }

        public Fact<string> Verify(Proposal proposal)
        {
            if (VerifyThrows) throw new InvalidOperationException("verify broke");
            return Fact<string>.Read(VerifyReadsOther ? "something else" : Value);
        }

        public void Restore(string restoreTarget, string restored)
        {
            Assert.Equal(target, restoreTarget);
            if (RestoreLies) return;
            Writes++;
            Value = restored;
            if (RestoreThrowsAfterWrite) throw new IOException("restore failed after writing");
        }
    }

    private sealed class RecordingLog : IChangeLog
    {
        public List<ChangeStep> Steps { get; } = [];

        public void Step(ChangeStep step) => Steps.Add(step);
    }

    private sealed class FaultFiles : PanelSettingsFiles
    {
        public bool FailWrite;
        public bool TruncateReads;

        public override void Write(string path, byte[] bytes)
        {
            if (FailWrite) throw new IOException("the disk is full");
            base.Write(path, bytes);
        }

        public override byte[] Read(string path, int limit)
        {
            var bytes = base.Read(path, limit);
            return TruncateReads ? bytes[..(bytes.Length / 2)] : bytes;
        }
    }

    private BackupStore Store(PanelSettingsFiles? files = null, Func<DateTimeOffset>? clock = null) => new(_data, files, clock);

    private (ChangeRunner Runner, RecordingLog Log, BackupStore Store) Runner(PanelSettingsFiles? files = null, bool elevated = false)
    {
        var log = new RecordingLog();
        var store = Store(files);
        return (new ChangeRunner(store, log, elevated), log, store);
    }

    private static Proposal Preview(IOptimisation item) => item.Propose().Value!;

    private string BackupFolder(string id) => Path.Combine(_data, "backups", id);

    // Item and proposal

    [Fact]
    public void A_fake_item_proposes_its_exact_target_before_and_after()
    {
        var item = new FakeItem(target: "startup:HKCU\\32\\Discord");
        var proposal = Preview(item);

        Assert.Equal("startup:HKCU\\32\\Discord", proposal.Target);
        Assert.Equal("1", proposal.Before);
        Assert.Equal("2", proposal.After);
    }

    // The one path: a confirmed proposal on an unchanged target

    [Fact]
    public void Apply_backs_up_writes_verifies_and_reports_the_change_with_its_backup_named()
    {
        var item = new FakeItem();
        var (runner, log, store) = Runner();

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.Changed, result.Status);
        Assert.Equal("2", item.Value);
        Assert.True(result.UndoOffered);
        var path = store.PathOf(result.Backup!);
        Assert.Equal(BackupFolder("fake-setting"), Path.GetDirectoryName(path));
        Assert.Matches(@"^\d{8}T\d{9}Z-apply\.json$", result.Backup!.Name);
        var saved = store.Read(result.Backup);
        Assert.Equal(new Backup("fake-setting", "fake:HKLM\\64\\Setting", "apply", "1", new Proposal("fake:HKLM\\64\\Setting", "1", "2"), saved.Created, result.Backup.Name), saved);
        Assert.Equal(["Backup", "Verify", "Result"], log.Steps.Select(s => s.Step).ToArray());
        Assert.All(log.Steps, s => Assert.Equal(result.Backup.Name, s.Backup));
        Assert.Empty(Directory.GetFiles(BackupFolder("fake-setting"), ".*.tmp"));
    }

    [Fact]
    public void A_cancelled_confirmation_never_reaches_apply_and_a_fresh_preview_is_asked_for()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();

        // The person cancelled: there is no confirmed proposal, so nothing is asked of Apply, and the row says why.
        Assert.Equal(Eligibility.No("Preview it first"), runner.CanApply(item, null));
        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
        Assert.False(Directory.Exists(BackupFolder("fake-setting")));
    }

    [Fact]
    public void An_edited_proposal_is_refused_with_nothing_changed()
    {
        var item = new FakeItem();
        var (runner, log, _) = Runner();
        var shown = Preview(item);

        foreach (var edited in new[] { shown with { After = "99" }, shown with { Target = "fake:HKLM\\64\\Other" } })
        {
            var result = runner.Apply(item, edited);

            Assert.Equal(ChangeStatus.NotChanged, result.Status);
            Assert.False(result.Written);
            Assert.Contains("Preview it again", result.Message);
        }
        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
        Assert.False(Directory.Exists(BackupFolder("fake-setting")));
        Assert.All(log.Steps, s => Assert.Equal("Refused", s.Step));
    }

    [Fact]
    public void A_setting_changed_between_preview_and_apply_is_refused_and_not_overwritten()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var shown = Preview(item);
        item.Value = "5"; // changed elsewhere while the confirmation was open

        var result = runner.Apply(item, shown);

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Contains("The setting changed since the preview", result.Message);
        Assert.Equal("5", item.Value);
        Assert.Equal(0, item.Writes);
        Assert.False(Directory.Exists(BackupFolder("fake-setting")));
        Assert.Equal(Eligibility.No("The setting changed since the preview"), runner.CanApply(item, shown));
    }

    [Fact]
    public void An_unreadable_target_is_refused_before_anything_is_written()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var shown = Preview(item);
        item.ReadFails = true;

        var result = runner.Apply(item, shown);

        Assert.False(result.Written);
        Assert.Contains("could not be read", result.Message);
        Assert.Equal(0, item.Writes);
    }

    // The backup

    [Fact]
    public void An_apply_whose_backup_can_not_be_written_changes_nothing()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner(new FaultFiles { FailWrite = true });

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Contains("backup could not be written", result.Message);
        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
    }

    [Fact]
    public void An_apply_whose_backup_does_not_read_back_changes_nothing_and_leaves_no_backup()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner(new FaultFiles { TruncateReads = true });

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.False(result.Written);
        Assert.Contains("did not read back", result.Message);
        Assert.Equal("1", item.Value);
        Assert.Equal(0, item.Writes);
        Assert.Empty(Directory.GetFiles(BackupFolder("fake-setting")));
    }

    [Fact]
    public void A_backup_is_never_overwritten_even_when_the_clock_stands_still()
    {
        var store = Store(clock: () => new DateTimeOffset(2026, 11, 19, 1, 2, 3, 4, TimeSpan.Zero));
        var proposal = new Proposal("t", "1", "2");

        var first = store.Write("fake-setting", BackupStore.Apply, proposal);
        var firstBytes = File.ReadAllBytes(store.PathOf(first));
        var second = store.Write("fake-setting", BackupStore.Apply, proposal with { After = "3" });

        Assert.Equal("20261119T010203004Z-apply.json", first.Name);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal(firstBytes, File.ReadAllBytes(store.PathOf(first)));
        Assert.Equal("3", store.Read(second).Proposal.After);
    }

    [Fact]
    public void A_test_run_writes_nothing_under_the_operators_own_data_folder()
    {
        var own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bosun", "backups");
        string[] Snapshot() => Directory.Exists(own) ? [.. Directory.GetFiles(own, "*", SearchOption.AllDirectories)] : [];
        var before = Snapshot();

        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var result = runner.Apply(item, Preview(item));
        runner.Undo(item, result.Backup!);

        Assert.StartsWith(_data, Store().PathOf(result.Backup!));
        Assert.Equal(before, Snapshot());
    }

    // Undo

    [Fact]
    public void Undo_restores_from_the_applied_result_after_the_store_is_reopened()
    {
        var item = new FakeItem();
        var applied = Runner().Runner.Apply(item, Preview(item));

        var (reopened, _, _) = Runner(); // a new store and runner, as after a restart
        var undone = reopened.Undo(item, applied.Backup!);

        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value);
        Assert.Equal("1", undone.ValueNow);
    }

    [Fact]
    public void Undo_uses_the_backup_of_the_change_it_reverses_not_the_newest_one()
    {
        var item = new FakeItem();
        var (runner, _, store) = Runner();
        var applied = runner.Apply(item, Preview(item)); // 1 to 2
        item.After = "3";
        item.ThrowBeforeWrite = true;
        var failed = runner.Apply(item, Preview(item)); // a later attempt that fails, leaving the newest backup (before 2)

        Assert.NotEqual(applied.Backup, failed.Backup);
        Assert.Equal("2", store.Read(failed.Backup!).Before);

        var undone = runner.Undo(item, applied.Backup!);

        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value); // the newest backup would have restored 2
    }

    [Fact]
    public void Undo_twice_restores_each_exact_value_because_an_undo_is_itself_backed_up()
    {
        var item = new FakeItem();
        var (runner, _, store) = Runner();
        var applied = runner.Apply(item, Preview(item));
        Assert.Equal("2", item.Value);

        var undone = runner.Undo(item, applied.Backup!);
        Assert.Equal(ChangeStatus.Restored, undone.Status);
        Assert.Equal("1", item.Value);
        Assert.EndsWith("-undo.json", undone.Backup!.Name);
        Assert.Equal(new Proposal(item.Target, "2", "1"), store.Read(undone.Backup).Proposal);

        var redone = runner.Undo(item, undone.Backup);
        Assert.Equal(ChangeStatus.Restored, redone.Status);
        Assert.Equal("2", item.Value);
        Assert.Equal(new Proposal(item.Target, "1", "2"), store.Read(redone.Backup!).Proposal);
    }

    [Fact]
    public void Undo_refuses_when_the_setting_holds_a_newer_value_than_the_change_wrote()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var applied = runner.Apply(item, Preview(item));
        item.Value = "7";

        var result = runner.Undo(item, applied.Backup!);

        Assert.False(result.Written);
        Assert.Contains("overwrite a newer value", result.Message);
        Assert.Equal("7", item.Value);
    }

    [Fact]
    public void Undo_refuses_a_well_formed_backup_for_a_different_target()
    {
        var item = new FakeItem();
        var (runner, _, store) = Runner();
        item.Value = "2";
        // A whole, valid backup in this item's own folder, but of another setting.
        var foreign = store.Write(item.Id, BackupStore.Apply, new Proposal("fake:HKLM\\32\\Setting", "1", "2"));

        var result = runner.Undo(item, foreign);

        Assert.False(result.Written);
        Assert.Contains("another setting", result.Message);
        Assert.Equal("2", item.Value);
        Assert.Equal(0, item.Writes);
    }

    [Fact]
    public void Undo_refuses_a_backup_of_another_item_a_truncated_one_and_one_named_for_another_operation()
    {
        var item = new FakeItem();
        var (runner, _, store) = Runner();
        var applied = runner.Apply(item, Preview(item));
        var path = store.PathOf(applied.Backup!);
        var bytes = File.ReadAllBytes(path);
        var whole = store.Read(applied.Backup!);

        // Another item's backup, even carrying the same target.
        var other = new FakeItem(id: "other-setting");
        Assert.Equal(Eligibility.No("That backup belongs to another item"), runner.CanUndo(other, applied.Backup));
        var copied = Path.Combine(BackupFolder("other-setting"), applied.Backup!.Name);
        Directory.CreateDirectory(BackupFolder("other-setting"));
        File.WriteAllBytes(copied, bytes);
        Assert.Contains("can't be trusted", runner.CanUndo(other, new BackupRef("other-setting", applied.Backup.Name)).Reason);

        // Truncated.
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        var truncated = runner.Undo(item, applied.Backup);
        Assert.False(truncated.Written);
        Assert.Contains("can't be trusted", truncated.Message);

        // Whole, but its operation field disagrees with its name.
        File.WriteAllBytes(path, BackupStore.Serialize(whole with { Operation = "undo" }));
        Assert.Contains("named for another operation", runner.Undo(item, applied.Backup).Message);

        // A target that is not the proposal's.
        File.WriteAllBytes(path, BackupStore.Serialize(whole with { Target = "fake:HKCU\\64\\Setting" }));
        Assert.Contains("not its proposal's", runner.Undo(item, applied.Backup).Message);

        Assert.Equal("2", item.Value);
        Assert.Equal(1, item.Writes);
    }

    // Every outcome after a write

    [Fact]
    public void A_write_that_then_errors_is_reported_from_what_the_target_reads_with_undo_offered()
    {
        var item = new FakeItem { ThrowAfterWrite = true };
        var (runner, _, _) = Runner();

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.True(result.UndoOffered);
        Assert.Equal("2", result.ValueNow);
        Assert.Contains("operation reported an error", result.Message);
        Assert.Contains("Undo is offered", result.Message);

        item.ThrowAfterWrite = false;
        Assert.Equal(ChangeStatus.Restored, runner.Undo(item, result.Backup!).Status);
        Assert.Equal("1", item.Value);
    }

    [Fact]
    public void A_write_that_errors_without_changing_anything_says_so_and_still_offers_undo()
    {
        var item = new FakeItem { ThrowBeforeWrite = true };
        var (runner, _, _) = Runner();

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.Equal("1", result.ValueNow);
        Assert.True(result.Written);
        Assert.True(result.UndoOffered);
        Assert.Contains("still reads '1'", result.Message);
    }

    [Fact]
    public void A_verify_that_fails_is_never_a_successful_change_and_undo_is_offered()
    {
        foreach (var item in new[] { new FakeItem { VerifyReadsOther = true }, new FakeItem { VerifyThrows = true } })
        {
            var (runner, log, _) = Runner();

            var result = runner.Apply(item, Preview(item));

            Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
            Assert.True(result.UndoOffered);
            Assert.Contains("Could not be verified", result.Message);
            Assert.Equal(ChangeLevel.Fail, log.Steps.Single(s => s.Step == "Result").Level);
            Assert.Equal(ChangeLevel.Fail, log.Steps.Single(s => s.Step == "Verify").Level);
        }
    }

    [Fact]
    public void A_target_that_can_not_be_read_after_the_write_is_could_not_be_verified()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var shown = Preview(item);
        var result = runner.Apply(new ReadsFailAfterApply(item), shown);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.Null(result.ValueNow);
        Assert.True(result.UndoOffered);
    }

    private sealed class ReadsFailAfterApply(FakeItem inner) : IOptimisation
    {
        private bool _written;

        public string Id => inner.Id;
        public string Title => inner.Title;
        public string Target => inner.Target;
        public bool NeedsElevation => false;
        public Fact<string> Read() => _written ? Fact<string>.Unavailable("gone dark") : inner.Read();
        public Fact<Proposal> Propose() => inner.Propose();
        public Fact<string> Verify(Proposal proposal) => inner.Verify(proposal);
        public void Restore(string target, string value) => inner.Restore(target, value);

        public void Apply(Proposal proposal)
        {
            inner.Apply(proposal);
            _written = true;
        }
    }

    [Fact]
    public void An_item_that_says_ok_while_its_target_reads_otherwise_is_not_believed()
    {
        // Verify echoes the proposal's own after value whatever the target holds.
        var item = new LyingVerify(new FakeItem());
        var (runner, _, _) = Runner();

        var result = runner.Apply(item, Preview(item));

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.Equal("1", result.ValueNow);
        Assert.True(result.UndoOffered);
    }

    private sealed class LyingVerify(FakeItem inner) : IOptimisation
    {
        public string Id => inner.Id;
        public string Title => inner.Title;
        public string Target => inner.Target;
        public bool NeedsElevation => false;
        public Fact<string> Read() => inner.Read();
        public Fact<Proposal> Propose() => inner.Propose();
        public void Apply(Proposal proposal) { } // writes nothing
        public Fact<string> Verify(Proposal proposal) => Fact<string>.Read(proposal.After);
        public void Restore(string target, string value) => inner.Restore(target, value);
    }

    [Fact]
    public void An_undo_that_reports_success_but_leaves_another_value_is_not_a_recovery()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var applied = runner.Apply(item, Preview(item));
        item.RestoreLies = true; // Restore returns normally and writes nothing

        var result = runner.Undo(item, applied.Backup!);

        Assert.Equal(ChangeStatus.NotChanged, result.Status);
        Assert.NotEqual(ChangeStatus.Restored, result.Status);
        Assert.Equal("2", result.ValueNow);
        Assert.Contains("Not restored", result.Message);
        Assert.True(result.UndoOffered);
    }

    [Fact]
    public void An_undo_that_writes_then_errors_is_could_not_be_verified_not_restored()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var applied = runner.Apply(item, Preview(item));
        item.RestoreThrowsAfterWrite = true;

        var result = runner.Undo(item, applied.Backup!);

        Assert.Equal(ChangeStatus.CouldNotVerify, result.Status);
        Assert.Equal("1", result.ValueNow);
        Assert.True(result.UndoOffered);
    }

    // Reporting

    [Fact]
    public void Each_step_is_reported_with_the_target_the_outcome_and_the_backups_name()
    {
        var item = new FakeItem();
        var (runner, log, _) = Runner();

        runner.Preview(item);
        var result = runner.Apply(item, Preview(item));
        runner.Undo(item, result.Backup!);

        Assert.Equal(["Preview", "Backup", "Verify", "Result", "Backup", "Verify", "Result"], log.Steps.Select(s => s.Step).ToArray());
        Assert.All(log.Steps, s =>
        {
            Assert.Equal("fake-setting", s.ItemId);
            Assert.Equal("fake:HKLM\\64\\Setting", s.Target);
            Assert.NotEmpty(s.Outcome);
        });
        Assert.Null(log.Steps[0].Backup);
        Assert.Equal(result.Backup!.Name, log.Steps[1].Backup);
        Assert.EndsWith("-undo.json", log.Steps[4].Backup);
    }

    // Eligibility

    [Fact]
    public void One_eligibility_result_gives_each_reason_for_apply_and_undo()
    {
        var item = new FakeItem();
        var (runner, _, _) = Runner();
        var shown = Preview(item);

        Assert.Equal(Eligibility.Yes, runner.CanApply(item, shown));
        Assert.Equal(Eligibility.No("Preview it first"), runner.CanApply(item, null));
        Assert.Equal(Eligibility.No("Nothing to undo"), runner.CanUndo(item, null));
        Assert.Contains("Preview it again", runner.CanApply(item, shown with { After = "9" }).Reason);
        item.Value = "3";
        Assert.Equal(Eligibility.No("The setting changed since the preview"), runner.CanApply(item, shown));

        var admin = new FakeItem { Elevated = true };
        var adminShown = Preview(admin);
        Assert.Equal(Eligibility.No("Needs an administrator prompt"), runner.CanApply(admin, adminShown));
        var refused = runner.Apply(admin, adminShown);
        Assert.False(refused.Written);
        Assert.Equal("1", admin.Value);
        Assert.Equal(Eligibility.Yes, Runner(elevated: true).Runner.CanApply(admin, adminShown));

        var applied = Runner().Runner.Apply(new FakeItem(), shown with { Before = "1" });
        Assert.Equal(Eligibility.Yes, runner.CanUndo(new FakeItem { Value = "2" }, applied.Backup));
        Assert.Equal(Eligibility.No("Needs an administrator prompt"), runner.CanUndo(new FakeItem { Value = "2", Elevated = true }, applied.Backup));
    }
}
