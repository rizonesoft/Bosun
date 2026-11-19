using System.IO;
using System.Windows.Threading;
using Bosun.Core.Optimise;
using Bosun.Panel.Optimise;
using Bosun.Panel.Shell;
using Bosun.Panel.Startup;
using Xunit;

namespace Bosun.Panel.Tests.Optimise;

/// <summary>
/// The change framework through the panel's real Activity adapter (D05 T02 §2): the lines the Activity pane shows
/// for a failed verify, with the Undo that is offered.
/// </summary>
public sealed class OptimisationFrameworkTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "bosun-panel-framework-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private sealed class DisposableItem : IOptimisation
    {
        public string Value = "1";
        public bool VerifyFails;

        public string Id => "disposable-setting";
        public string Title => "Disposable setting";
        public string Target => "disposable:Setting";
        public bool NeedsElevation => false;

        public Fact<string> Read() => Fact<string>.Read(Value);

        public Fact<Proposal> Propose() => Fact<Proposal>.Read(new Proposal(Target, Value, "2"));

        public void Apply(string expectedBefore, string after) => Value = after;

        public Fact<string> Verify(Proposal proposal) => Fact<string>.Read(VerifyFails ? "unexpected" : Value);

        public void Restore(string expectedCurrent, string value) => Value = value;
    }

    [Fact]
    public void A_failed_verify_reaches_the_activity_pane_with_its_undo_offered()
    {
        Sta.Run(() =>
        {
            var activity = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
            var item = new DisposableItem { VerifyFails = true };
            // The folder the panel gives for a variable naming a test folder: never the operator's own.
            var store = new BackupStore(PanelPaths.Data(_data));
            var runner = new ChangeRunner(store, new ActivityChangeLog(activity));

            var applied = runner.Apply(item, item.Propose().Value!);

            Assert.Equal(ChangeStatus.CouldNotVerify, applied.Status);
            Assert.True(applied.UndoOffered);
            Assert.StartsWith(_data, store.PathOf(applied.Backup!));
            var lines = activity.Entries.Select(e => e.Tag + " " + e.Message).ToArray();
            Assert.Equal(4, lines.Length);
            Assert.Equal($"INFO Disposable setting, Backup (disposable:Setting): Written and read back [backup {applied.Backup!.Name}]", lines[0]);
            Assert.Equal($"INFO Disposable setting, Write returned (disposable:Setting): Apply returned [backup {applied.Backup.Name}]", lines[1]);
            Assert.Equal($"FAIL Disposable setting, Verify (disposable:Setting): The item's own verification did not confirm it [backup {applied.Backup.Name}]", lines[2]);
            Assert.StartsWith("FAIL Disposable setting, Result (disposable:Setting): Could not be verified: it now reads '2' but its verification read 'unexpected'. Undo is offered from backup " + applied.Backup.Name, lines[3]);

            // And the Undo it offers works, saying so in the same pane.
            item.VerifyFails = false;
            var undone = runner.Undo(item, applied.Backup);
            Assert.Equal(ChangeStatus.Restored, undone.Status);
            Assert.Equal("1", item.Value);
            Assert.Equal("INFO", activity.Entries[^1].Tag);
            Assert.StartsWith("Disposable setting, Result", activity.Entries[^1].Message);
        });
    }

    [Fact]
    public void A_refusal_is_a_warn_line_and_a_change_is_an_info_line()
    {
        Sta.Run(() =>
        {
            var activity = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
            var item = new DisposableItem();
            var runner = new ChangeRunner(new BackupStore(PanelPaths.Data(_data)), new ActivityChangeLog(activity));
            var shown = item.Propose().Value!;
            item.Value = "5";

            runner.Apply(item, shown);

            var entry = Assert.Single(activity.Entries);
            Assert.Equal("WARN", entry.Tag);
            Assert.Equal("Disposable setting, Refused (disposable:Setting): Not changed: The setting changed since the preview", entry.Message);
        });
    }
}
