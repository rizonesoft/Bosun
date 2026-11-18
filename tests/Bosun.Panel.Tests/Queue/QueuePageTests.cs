using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Core.Memory;
using Bosun.Core.Queue;
using Bosun.Panel.Controls;
using Bosun.Panel.Queue;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;
using static Bosun.Panel.Tests.Queue.QueueFixtures;

namespace Bosun.Panel.Tests.Queue;

/// <summary>
/// The Queue page (D03 T02 §3, design/SPEC.md §5): its 5 s refresh, the slots and waiters in words, what the status
/// can't tell, the history with its exit and admission apart, its states, and the limits form: each field's source,
/// edits kept through refresh, saves of changed fields checked against the read-back, Use the default, the shell's
/// unsaved-edits contract, the rail badge and Save in Ctrl+K.
/// </summary>
public class QueuePageTests
{
    internal sealed class Driver : IRefreshDriver
    {
        public void Start(Action tick)
        {
        }

        public void Stop()
        {
        }
    }

    internal static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    internal static void Settle()
    {
        for (var i = 0; i < 8; i++) Pump();
    }

    /// <summary>
    /// Runs a task on this thread's dispatcher until it ends, bounded by time: a task that ends through a thread-pool
    /// continuation (Wait(Task) below) may need longer than a count of pumps on a slow machine. CI's runner ran 400
    /// pumps in 53 ms and failed a save that had not yet ended (a81a980).
    /// </summary>
    internal static T Wait<T>(Task<T> task)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < until) Pump();
        Assert.True(task.IsCompleted, "the task did not end within 10 s");
        return task.GetAwaiter().GetResult();
    }

    internal static void Wait(Task task) => Wait(task.ContinueWith(_ => 0, TaskScheduler.Default));

    internal sealed record Harness(QueuePage Page, ShellViewModel Shell, ActivityLog Log, FakeQueueSource Source, Action<TimeSpan> Advance)
    {
        public IReadOnlyList<string> Lines(LogSeverity severity)
        {
            Settle();
            return [.. Log.Entries.Where(e => e.Severity == severity).Select(e => e.Message)];
        }

        public LimitField Slots => Page.Field("slots");
        public LimitField Wait => Page.Field("max_wait_s");

        public void Refresh()
        {
            Page.RefreshNow!.Execute(null);
            Settle();
        }

        public ActionOutcome Save() => QueuePageTests.Wait(Page.SaveAsync());

        /// <summary>The button's own command, which writes the action's Activity line.</summary>
        public void Press()
        {
            Wait(((Bosun.Panel.Mvvm.AsyncCommand)Page.Save).ExecuteAsync());
            Settle();
        }

        public Section Other => Shell.Sections.Single(s => s.Key == "overview");

        public Section Queue => Shell.Sections.Single(s => s.Key == "queue");
    }

    internal static void OnPage(FakeQueueSource source, Action<Harness> test, bool show = true)
    {
        PageRenderer.InPage("Dark", () => new Grid(), _ =>
        {
            var clock = Now.UtcDateTime;
            var scheduler = new RefreshScheduler(() => clock, new Driver());
            var log = Shells.Log();
            var page = new QueuePage(source, scheduler, log);
            var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
            if (show) shell.Show(shell.Sections.Single(s => s.Key == "queue"));
            Settle();
            test(new Harness(page, shell, log, source, by =>
            {
                clock += by;
                scheduler.Tick();
                Settle();
            }));
            return 0;
        });
    }

    /// <summary>The shell on the Queue page over <paramref name="source"/>, for a render, and when it has read (then <paramref name="then"/>).</summary>
    internal static (System.Windows.FrameworkElement View, Func<System.Windows.FrameworkElement, Task> Ready) Rendered(FakeQueueSource source, Func<QueuePage, Task>? then = null)
    {
        var scheduler = new RefreshScheduler(() => Now.UtcDateTime, new Driver());
        var log = Shells.Log();
        var page = new QueuePage(source, scheduler, log);
        var shell = new ShellViewModel(Sections.All(new Dictionary<string, PageBase> { ["queue"] = page }), log);
        var held = source.HoldStatus is not null || source.StatusNeverAnswers;
        shell.Show(shell.Sections.Single(s => s.Key == "queue"));
        return (new ShellView { DataContext = shell }, async _ =>
        {
            // Held loading: the skeleton shows only after its delay, so a quick read never flashes it.
            if (held) await Task.Delay(Skeleton.DefaultDelay + TimeSpan.FromMilliseconds(150));
            for (var i = 0; i < 50 && !held && page.State == PageState.Loading; i++) await Dispatcher.Yield(DispatcherPriority.Background);
            if (then is not null) await then(page);
            for (var i = 0; i < 5; i++) await Dispatcher.Yield(DispatcherPriority.Background);
        });
    }

    // ---- refresh ----

    [Fact]
    public void It_reads_once_per_5_s_tick_while_shown_and_nothing_while_hidden()
    {
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            Assert.Equal((1, 1, 1), (source.StatusReads, source.HistoryReads, source.LimitsReads));
            Assert.Contains($"history {QueuePage.HistoryCount}", source.Calls);
            h.Advance(TimeSpan.FromSeconds(4));
            Assert.Equal(1, source.StatusReads);
            h.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal((2, 2, 2), (source.StatusReads, source.HistoryReads, source.LimitsReads));
            h.Shell.Show(h.Other);
            h.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal((2, 2, 2), (source.StatusReads, source.HistoryReads, source.LimitsReads)); // hidden: nothing read
            h.Shell.Show(h.Queue);
            Assert.Equal(3, source.StatusReads);                                                    // back on screen: read at once
        });
    }

    [Fact]
    public void A_slow_read_begun_before_a_save_that_ends_after_it_does_not_undo_the_save_s_read_back()
    {
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            var held = new TaskCompletionSource();
            source.HoldLimits.Enqueue(held);          // the next tick's limits read is slow: it read slots 2
            h.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal(2, source.LimitsReads);
            h.Slots.Text = "1";
            Assert.True(h.Save().Succeeded);          // its own read-back says slots 1
            Assert.Equal(("1", "Saved: 1"), (h.Slots.Text, h.Slots.Source));
            held.SetResult();                         // the older reading ends last
            Settle();
            Assert.Equal(("1", "Saved: 1", false), (h.Slots.Text, h.Slots.Source, h.Page.HasUnsavedEdits));
        });
    }

    // ---- the live queue ----

    [Fact]
    public void Slots_and_waiters_show_with_their_words_in_order_and_a_free_slot_as_Free()
    {
        OnPage(new FakeQueueSource(), h =>
        {
            var page = h.Page;
            Assert.Equal(PageState.Data, page.State);
            Assert.Equal([0, 1], page.Slots.Select(s => s.Index));
            Assert.Equal([SlotState.Running, SlotState.Free], page.Slots.Select(QueueSlotTile.StateOf));
            var (lines, _) = WaitingRow.Read(page.Status);
            Assert.Equal(["Waiting for memory: slot 2 opens when 6.0 GB is free; 4.5 GB is free now", "Waiting for a free slot"], lines.Select(l => l.Why));
            Assert.Equal(["Office: dotnet build", "Office: dotnet test"], lines.Select(l => l.Job));
            Assert.Equal("1 running, 2 waiting", page.LiveWords);
            Assert.Null(page.Idle);
            Assert.Equal(2, page.Waiting);           // the rail's badge
        });
    }

    [Fact]
    public void What_the_status_cannot_tell_is_said_beside_the_jobs_read_and_never_read_as_idle()
    {
        var cases = new (StatusDoc Status, string Said, string Words)[]
        {
            (Status($"{Held(0, Holder(0))},{Free(1)}", skipped: 2), "2 records could not be read, so a job may be missing", "1 running, 2 records not readable"),
            (Status($"{Free(0)},{Free(1)}", skipped: 1), "1 record could not be read", "1 record not readable"),
            (Status($"{Free(0)},{Free(1)}", readable: false), "The queue's records in C:\\run could not be read", "records not readable"),
            (Status($"{Held(0, null, busy: null)},{Free(1)}"), "Slot 1: whether it is in use could not be checked.", "1 slot not known"),
            (Status($"{Free(0)},{Held(1, null)}"), "Slot 2 is held, but no record says which job holds it.", "1 held"),
            (Status($"{Free(0)},{Held(1, Holder(1), above: true)}"), "Slot 2 is above the slot limit", "1 running"),
        };
        foreach (var (status, said, words) in cases)
        {
            var source = new FakeQueueSource { Status = Reading<StatusDoc>.Of(status, Now) };
            OnPage(source, h =>
            {
                Assert.Null(h.Page.Idle);
                Assert.Equal(words, h.Page.LiveWords);
                Assert.Contains(h.Page.Doubts, d => d.Contains(said, StringComparison.Ordinal));
                Assert.NotEqual(QueueSummary.Idle, h.Page.Words.Words);
                Assert.Equal(status.Slots.Count, h.Page.Slots.Count);   // the jobs that were read stay on screen
            });
        }
        // Starting and waiting for guest memory are words of their own, never "running" and never idle.
        var phases = Status($"{Held(0, Holder(0, "\"starting\""))},{Held(1, Holder(1, "\"waiting for guest memory\""))}");
        Assert.Equal("1 starting, 1 waiting for guest memory", QueueSummary.WordsOf(phases));
        Assert.Equal(QueueSummary.Idle, QueueSummary.WordsOf(IdleStatus()));
    }

    // ---- history ----

    [Fact]
    public void History_keeps_the_exit_code_apart_from_how_the_job_was_admitted_with_slots_counted_from_one()
    {
        var timedOut = new HistoryLine(Job(slot: null, exit: 1, timedOut: true, waited: 900));
        Assert.Equal(("None", "1", HistoryLine.TimedOut, "15 min"), (timedOut.Slot, timedOut.Exit, timedOut.Admission, timedOut.Waited));
        var cancelled = new HistoryLine(Job(slot: null, exit: -1073741510, ran: 0, timedOut: false));
        Assert.Equal(("None", "0xC000013A (stopped by Ctrl+C)", HistoryLine.Cancelled, "0 s"), (cancelled.Slot, cancelled.Exit, cancelled.Admission, cancelled.Ran));
        // A Windows status reads in hex as Windows writes it, named where it is a common one; a small code is its number.
        Assert.Equal(("0", "1", "0xC0000005 (access violation)", "0xC0000135"), (HistoryLine.ExitWords(0), HistoryLine.ExitWords(1), HistoryLine.ExitWords(-1073741819), HistoryLine.ExitWords(-1073741515)));
        var wsl = new HistoryLine(Job(side: "wsl", slot: 1, exit: null, ran: 0.5));
        Assert.Equal(("WSL", "2", "Ran in slot 2", HistoryLine.NotKnown, true, "0.5 s"), (wsl.Side, wsl.Slot, wsl.Admission, wsl.Exit, wsl.ExitIsWord, wsl.Ran));
        Assert.Contains("not recorded", wsl.ExitWhy, StringComparison.Ordinal);
        Assert.Equal(("no repo", 2), (new HistoryLine(Job(repo: null, slot: 1)).Repo, new HistoryLine(Job(slot: 1)).SlotSort));

        var source = new FakeQueueSource
        {
            History = Reading<JobHistory>.Of(new JobHistory([Job(command: "b"), Job(command: "a")], 3, ["jobs-2026-11-17.jsonl: denied"]), Now),
        };
        OnPage(source, h =>
        {
            Assert.Equal(["b", "a"], h.Page.History.Select(l => l.Command));   // newest first, as read
            Assert.Contains("jobs-2026-11-17.jsonl: denied", h.Page.HistoryNotice, StringComparison.Ordinal);
            Assert.Contains("3 lines of the job logs are not a job record", h.Page.HistoryNotice, StringComparison.Ordinal);
        });
    }

    // ---- states ----

    [Fact]
    public void Loading_first_then_an_idle_queue_keeps_its_history_and_a_saveable_form_on_screen()
    {
        var source = new FakeQueueSource { Status = Reading<StatusDoc>.Of(IdleStatus(), Now), HoldStatus = new TaskCompletionSource() };
        var hold = source.HoldStatus;
        OnPage(source, h =>
        {
            Assert.Equal(PageState.Loading, h.Page.State);
            hold.SetResult();
            Settle();
            Assert.Equal(PageState.Data, h.Page.State);                 // not the frame's Empty state, which hides the page
            Assert.Equal(QueuePage.IdleNote, h.Page.Idle);
            Assert.Null(h.Page.LiveWords);
            Assert.Equal([SlotState.Free, SlotState.Free], h.Page.Slots.Select(QueueSlotTile.StateOf));
            Assert.Single(h.Page.History);
            Assert.Equal(QueueSummary.Idle, h.Page.Words.Words);
            Assert.Equal(0, h.Page.Waiting);
            h.Slots.Text = "3";
            Assert.True(h.Save().Succeeded);
            Assert.Contains("set slots 3", source.Calls);
        });
    }

    [Fact]
    public void A_part_that_can_t_be_read_is_said_where_it_would_be_and_the_others_stay_and_nothing_readable_fails_with_Retry()
    {
        var source = new FakeQueueSource { Status = Reading<StatusDoc>.Fail(ReadingProblem.Failed, "bosun status --json ended with exit code 1: boom") };
        OnPage(source, h =>
        {
            Assert.Equal(PageState.Data, h.Page.State);
            Assert.Equal("The queue could not be read: bosun status --json ended with exit code 1: boom", h.Page.StatusNotice);
            Assert.Empty(h.Page.Slots);
            Assert.Null(h.Page.Idle);
            Assert.Single(h.Page.History);
            Assert.True(h.Page.FormReady);
            Assert.Null(h.Page.Words.Words);

            source.Status = Reading<StatusDoc>.Of(BusyStatus(), Now);
            source.Limits = Reading<ConfigDoc>.Fail(ReadingProblem.Failed, "bosun config get --json did not answer within 20 s and was stopped.");
            h.Refresh();
            Assert.Null(h.Page.StatusNotice);
            Assert.Equal("The limits could not be read: bosun config get --json did not answer within 20 s and was stopped.", h.Page.LimitsNotice);
            Assert.Equal(2, h.Page.Slots.Count);

            source.Status = Reading<StatusDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            source.History = Reading<JobHistory>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            source.Limits = Reading<ConfigDoc>.Fail(ReadingProblem.NotInstalled, "bosun.exe is not installed.");
            h.Refresh();
            Assert.Equal((PageState.Failed, "bosun.exe is not installed."), (h.Page.State, h.Page.FailureMessage));
            source.Status = Reading<StatusDoc>.Of(BusyStatus(), Now);
            h.Page.Retry!.Execute(null);
            Settle();
            Assert.Equal(PageState.Data, h.Page.State);
        });
    }

    // ---- the form ----

    [Fact]
    public void The_form_says_each_field_s_source_and_the_file_s_own_problems()
    {
        var source = new FakeQueueSource { Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(3, "file", 3), wait: WaitLimit(60, "file", 60)), Now) };
        OnPage(source, h =>
        {
            Assert.Equal(["slots", "min_avail_mb", "max_wait_s", "min_guest_mb"], h.Page.Fields.Select(f => f.Key));
            Assert.Equal(["Slots", "Memory floor for each slot after the first", "Maximum wait", "Guest memory floor"], h.Page.Fields.Select(f => f.Label));
            Assert.Equal(("3", "From 1 to 64. Saved: 3."), (h.Slots.Text, h.Slots.Detail));
            Assert.Equal(("seconds", "0 means no limit."), (h.Wait.Unit, h.Wait.Hint));
            Assert.Equal("The default, 6144", h.Page.Field("min_avail_mb").Source);
            Assert.Null(h.Page.FileNotice);

            // A valid variable: read-only, said; an ignored one: said with its error, and editable.
            source.Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(4, "environment", 3), guest: GuestLimit(3072, "default", null)), Now);
            h.Refresh();
            Assert.True(h.Slots.IsReadOnly);
            Assert.False(h.Slots.IsEditable);
            Assert.StartsWith("Set by BOSUN_SLOTS", h.Slots.Source, StringComparison.Ordinal);
            Assert.Equal("4", h.Slots.Text);
            source.Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(2, "default", null, "BOSUN_SLOTS=abc is not a whole number from 1 to 64")), Now);
            h.Refresh();
            Assert.True(h.Slots.IsEditable);
            Assert.Equal("BOSUN_SLOTS is ignored: BOSUN_SLOTS=abc is not a whole number from 1 to 64", h.Slots.VariableNote);

            // The file unreadable, invalid, or with a refused value: said above the form. bosun will not overwrite a file it
            // can't read or that is damaged (exit 74), so Save and Use the default wait on it being fixed (round 2), and each
            // field still says where its own value comes from.
            source.Limits = Reading<ConfigDoc>.Of(Config(readable: false, error: "it can't be read"), Now);
            h.Refresh();
            Assert.Contains("can't be read: it can't be read.", h.Page.FileNotice, StringComparison.Ordinal);
            Assert.Contains("bosun will not overwrite", h.Page.FileNotice, StringComparison.Ordinal);
            Assert.DoesNotContain("writes a new file", h.Page.FileNotice, StringComparison.Ordinal);
            Assert.Equal("The default in force, 2, not a saved value", h.Slots.Source);
            h.Wait.Text = "60";
            Assert.StartsWith("bosun will not overwrite", h.Page.SaveReason(), StringComparison.Ordinal);
            Assert.False(h.Page.Save.CanExecute(null));
            h.Page.Discard();
            source.Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(4, "environment", null), readable: false, error: "it can't be read"), Now);
            h.Refresh();
            Assert.StartsWith("Set by BOSUN_SLOTS", h.Slots.Source, StringComparison.Ordinal);   // its own source, never called a default
            source.Limits = Reading<ConfigDoc>.Of(Config(valid: false, error: "line 1: not JSON"), Now);
            h.Refresh();
            Assert.Contains("is not valid: line 1: not JSON.", h.Page.FileNotice, StringComparison.Ordinal);
            Assert.Contains("while it is damaged", h.Page.FileNotice, StringComparison.Ordinal);
            source.Limits = Reading<ConfigDoc>.Of(Config(refused: """{"key":"slots","error":"slots must be a whole number from 1 to 64"}"""), Now);
            h.Refresh();
            Assert.Equal("Its slots was refused: slots must be a whole number from 1 to 64.", h.Page.FileNotice);
        });
    }

    [Fact]
    public void A_tick_updates_the_fields_not_being_edited_and_keeps_an_edit_and_its_error()
    {
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            h.Wait.Text = "60";
            h.Page.Field("min_guest_mb").Text = "lots";
            Assert.NotNull(h.Page.Field("min_guest_mb").Error);
            source.Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(5, "file", 5), wait: WaitLimit(120, "file", 120), guest: GuestLimit(1024, "file", 1024)), Now);
            h.Advance(TimeSpan.FromSeconds(6));
            Assert.Equal(("5", "60", "lots"), (h.Slots.Text, h.Wait.Text, h.Page.Field("min_guest_mb").Text));
            Assert.NotNull(h.Page.Field("min_guest_mb").Error);
            Assert.Equal("Saved: 120", h.Wait.Source);   // what is in force is still said beside the edit
            Assert.True(h.Page.HasUnsavedEdits);
        });
    }

    [Fact]
    public void Save_sends_only_the_changed_fields_in_order_and_a_refusal_stays_beside_its_field_with_the_draft()
    {
        var source = new FakeQueueSource();
        source.SaveAnswers["max_wait_s"] = _ => FakeQueueSource.Refused("max_wait_s", "max_wait_s must be a whole number from 0 to 604800");
        OnPage(source, h =>
        {
            h.Wait.Text = "999999999";
            h.Slots.Text = "1";
            h.Press();
            Assert.Equal(["set slots 1", "set max_wait_s 999999999", "get"], source.Calls.Where(c => c != "history 200").Skip(1));
            Assert.Equal(("1", "Saved: 1", null), (h.Slots.Text, h.Slots.Source, h.Slots.Error));
            Assert.Equal(("999999999", "max_wait_s must be a whole number from 0 to 604800"), (h.Wait.Text, h.Wait.Error));
            Assert.True(h.Page.HasUnsavedEdits);
            Assert.Contains("Saved and read back: slots 1.", h.Page.SaveStatus, StringComparison.Ordinal);
            Assert.Equal(["Queue limits saved and read back: slots 1"], h.Lines(LogSeverity.Info).Where(l => l.StartsWith("Queue limits", StringComparison.Ordinal)));
            Assert.Equal(["Save queue limits: Maximum wait: max_wait_s must be a whole number from 0 to 604800."], h.Lines(LogSeverity.Warn));
            Assert.Empty(h.Lines(LogSeverity.Fail));

            // Saved whole: one INFO line, the action's own.
            h.Wait.Text = "60";
            h.Page.Field("min_guest_mb").Text = "1024";
            source.SaveAnswers.Clear();
            source.Calls.Clear();
            h.Press();
            Assert.Equal(["set max_wait_s 60", "set min_guest_mb 1024", "get"], source.Calls);
            Assert.Contains("Save queue limits: max_wait_s 60, min_guest_mb 1024", h.Lines(LogSeverity.Info));
            Assert.False(h.Page.HasUnsavedEdits);
        });
    }

    [Fact]
    public void A_save_sends_the_values_as_submitted_and_keeps_an_edit_made_while_it_runs()
    {
        // Round 1: the values were read again after each write, so a field cleared while an earlier one saved was sent
        // as 0 (no wait limit at all). They are taken once, as submitted, and the later edit is left as the person's.
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            h.Slots.Text = "1";
            h.Wait.Text = "60";
            source.SaveAnswers["slots"] = value =>
            {
                h.Wait.Text = "";   // the person clears the wait while the slot count saves
                return Reading<LimitSave>.Of(new LimitSave("slots", LimitSaveOutcome.Saved, value, "saved"), Now);
            };
            source.Calls.Clear();
            h.Press();
            Assert.Equal(["set slots 1", "set max_wait_s 60"], source.Calls.Where(c => c.StartsWith("set ", StringComparison.Ordinal)));
            Assert.Equal("", h.Wait.Text);   // their later edit kept, not replaced by the read-back
        });
    }

    [Fact]
    public void A_read_back_that_could_not_read_the_file_confirms_neither_a_save_nor_a_default()
    {
        // Round 1: config get can exit 0 and still say the file could not be read, its saved values null.
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            h.Slots.Text = "1";
            source.ReadBack = () => Reading<ConfigDoc>.Of(QueueFixtures.Config(readable: false, valid: false), Now);
            var saved = h.Save();
            Assert.False(saved.Succeeded);
            Assert.Contains("not confirmed", saved.Reason, StringComparison.Ordinal);
            Assert.Contains("could not be read back", h.Page.FormNotice, StringComparison.Ordinal);
        });
        var reset = new FakeQueueSource { Limits = Reading<ConfigDoc>.Of(QueueFixtures.Config(slots: SlotsLimit(3, "file", 3)), Now) };
        OnPage(reset, h =>
        {
            reset.ReadBack = () => Reading<ConfigDoc>.Of(QueueFixtures.Config(readable: false, valid: false), Now);
            Wait(((Bosun.Panel.Mvvm.AsyncCommand)h.Slots.UseDefault!).ExecuteAsync());
            Assert.Contains("not confirmed", h.Page.FormNotice, StringComparison.Ordinal);
            Assert.Contains(h.Lines(LogSeverity.Warn), l => l.Contains("not confirmed", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Lines(LogSeverity.Info), l => l.StartsWith(h.Slots.UseDefaultName, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_busy_writer_stops_the_save_and_a_failed_or_mismatched_read_back_is_not_confirmed()
    {
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            source.SaveAnswers["slots"] = _ => Reading<LimitSave>.Of(new LimitSave("slots", LimitSaveOutcome.Busy, null, "another save holds the config"), Now);
            h.Slots.Text = "1";
            h.Wait.Text = "60";
            source.Calls.Clear();
            var busy = h.Save();
            Assert.True(busy is { Succeeded: false, IsWarning: true });
            Assert.Equal(["set slots 1", "get"], source.Calls);           // the wait was not tried after the busy file
            Assert.Contains("another save held the limits file", h.Page.FormNotice, StringComparison.Ordinal);
            Assert.Equal(("1", "60"), (h.Slots.Text, h.Wait.Text));       // both drafts kept

            source.SaveAnswers.Clear();
            source.ReadBack = () => Reading<ConfigDoc>.Fail(ReadingProblem.Failed, "bosun config get --json ended with exit code 74: damaged");
            var failed = h.Save();
            Assert.False(failed.Succeeded);
            Assert.Contains("could not be read back, so what was saved is not confirmed", h.Page.FormNotice, StringComparison.Ordinal);
            Assert.Contains("not confirmed", failed.Reason, StringComparison.Ordinal);

            source.ReadBack = () => Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(3, "file", 3)), Now);
            h.Slots.Text = "1";
            var mismatched = h.Save();
            Assert.False(mismatched.Succeeded);
            Assert.Equal(("1", "Saved, but read back as 3, so it is not confirmed."), (h.Slots.Text, h.Slots.Error));
            Assert.Contains("Slots read back as 3, not 1", mismatched.Reason, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Use_the_default_unsets_the_field_and_reads_it_back()
    {
        var source = new FakeQueueSource { Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(3, "file", 3)), Now) };
        OnPage(source, h =>
        {
            Assert.True(h.Slots.UseDefault!.CanExecute(null));
            Assert.False(h.Wait.UseDefault!.CanExecute(null));            // nothing saved: it is the default already
            Wait(((Bosun.Panel.Mvvm.AsyncCommand)h.Slots.UseDefault).ExecuteAsync());
            Settle();
            Assert.Contains("unset slots", source.Calls);
            Assert.Equal(("2", "The default, 2", (long?)null), (h.Slots.Text, h.Slots.Source, h.Slots.Saved));
            Assert.Contains("Use the default for Slots: slots 2 (the default)", h.Lines(LogSeverity.Info));
        });
    }

    // ---- the shell's contract ----

    [Fact]
    public void Leaving_with_an_edit_asks_and_Save_Discard_and_Keep_editing_each_do_what_they_say()
    {
        var source = new FakeQueueSource();
        source.SaveAnswers["max_wait_s"] = _ => FakeQueueSource.Refused("max_wait_s", "max_wait_s must be a whole number from 0 to 604800");
        OnPage(source, h =>
        {
            h.Wait.Text = "999999999";
            // Keep editing: still on Queue, with the edit.
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Stay);
            Assert.False(Wait(h.Shell.GoToAsync(h.Other)));
            Assert.Same(h.Page, h.Shell.Page);
            // Save, refused: still on Queue with the draft and why.
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Save);
            Assert.False(Wait(h.Shell.GoToAsync(h.Other)));
            Assert.Same(h.Page, h.Shell.Page);
            Assert.Equal("999999999", h.Wait.Text);
            Assert.StartsWith("Not saved: Maximum wait: max_wait_s must be", h.Page.SaveFailure, StringComparison.Ordinal);
            Assert.Contains("Save Queue: Maximum wait: max_wait_s must be a whole number from 0 to 604800.", h.Lines(LogSeverity.Warn));
            // Discard: the values read back, and away.
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Discard);
            Assert.True(Wait(h.Shell.GoToAsync(h.Other)));
            Assert.Equal(("900", null), (h.Wait.Text, h.Wait.Error));
            // Save that works: saved, and away; and the window's close goes through the same question.
            Wait(h.Shell.GoToAsync(h.Queue));
            Settle();
            source.SaveAnswers.Clear();
            h.Wait.Text = "60";
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Save);
            Assert.True(Wait(h.Shell.GoToAsync(h.Other)));
            Assert.Contains("set max_wait_s 60", source.Calls);
            Wait(h.Shell.GoToAsync(h.Queue));
            Settle();
            h.Wait.Text = "120";
            var window = new FakeWindow();
            var guard = new ExitGuard(h.Shell, window);
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Stay);
            Assert.False(Wait(guard.AskThenCloseAsync()));
            Assert.DoesNotContain("close", window.Calls);
            h.Shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Save);
            Assert.True(Wait(guard.AskThenCloseAsync()));
            Assert.Contains("set max_wait_s 120", source.Calls);
        });
    }

    [Fact]
    public void Save_is_in_Ctrl_K_as_the_button_s_command_disabled_with_why()
    {
        var source = new FakeQueueSource();
        OnPage(source, h =>
        {
            PaletteRow Row()
            {
                h.Shell.Palette.Open();
                h.Shell.Palette.Query = "Save queue";
                var row = h.Shell.Palette.Rows.Single(r => r.Name == "Save queue limits");
                h.Shell.Palette.Close();
                return row;
            }
            Assert.Equal(QueuePage.NoChanges, Row().Reason);
            Assert.Same(h.Page.Save, h.Page.PaletteCommands.Single(c => c.Name == "Save queue limits").Command);
            h.Slots.Text = "1";
            var row = Row();
            Assert.Null(row.Reason);
            h.Shell.Palette.Open();
            Assert.True(Wait(h.Shell.Palette.RunAsync(h.Shell.Palette.Rows.Single(r => r.Name == "Save queue limits"))));
            Settle();
            Assert.Contains("set slots 1", source.Calls);
            Assert.Contains("Save queue limits: slots 1", h.Lines(LogSeverity.Info));

            // While a save runs: held at its read-back.
            var held = new TaskCompletionSource();
            source.HoldLimits.Enqueue(held);
            h.Slots.Text = "2";
            var running = ((Bosun.Panel.Mvvm.AsyncCommand)h.Page.Save).ExecuteAsync();
            Settle();
            Assert.Equal(PageCommand.Running, Row().Reason);
            held.SetResult();
            Wait(running);

            // Nothing editable: every limit set by a variable.
            source.Limits = Reading<ConfigDoc>.Of(Config(slots: SlotsLimit(2, "environment"), floor: FloorLimit(0, "environment"), wait: WaitLimit(1, "environment"),
                guest: GuestLimit(1, "environment")), Now);
            h.Refresh();
            Assert.Equal(QueuePage.NothingEditable, Row().Reason);
        });

        // Nothing read: the limits could not be read.
        OnPage(new FakeQueueSource { Limits = Reading<ConfigDoc>.Fail(ReadingProblem.Failed, "no answer") }, u =>
        {
            Assert.Equal(QueuePage.NotRead, u.Page.SaveReason());
            Assert.False(u.Page.Save.CanExecute(null));
        });
    }
}
