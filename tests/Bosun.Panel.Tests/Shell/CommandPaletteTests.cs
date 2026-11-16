using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Mvvm;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Tests.Tray;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The command palette (D02 T03 §8, design/SPEC.md §19): every page and the page's own commands by the keyboard, each
/// run through its own command, checked as it runs; a danger command only through its own question; leaving a page
/// only through the question about unsaved edits.
/// </summary>
public class CommandPaletteTests
{
    /// <summary>A page with commands of its own: a plain one, a danger one whose button asks first, and one it can turn off.</summary>
    sealed class ActionPage : PageBase
    {
        public ActionPage(ActivityLog log, string title = "Optimise") : base(title, "Changes with evidence.")
        {
            Apply = log.Command("Apply the change", () =>
            {
                Applied++;
                return Task.FromResult(ActionOutcome.Done);
            });
            // The danger command asks its own question before it acts: the button and the palette run this one command.
            Undo = log.Command("Undo every change", async () =>
            {
                Asked++;
                AskedWhilePaletteOpen = PaletteOpen?.Invoke() ?? false;
                if (!await Confirm()) return ActionOutcome.Done;
                Undone++;
                return ActionOutcome.Done;
            });
            Gated = log.Command("Restart the logger", () =>
            {
                Restarted++;
                return Task.FromResult(ActionOutcome.Done);
            }, () => CanRestart);
            Slow = log.Command("Measure the build", async () =>
            {
                Measured++;
                await SlowEnds.Task;
                return ActionOutcome.Done;
            });
            Throws = log.Command("Read the evidence", () =>
            {
                ThrowCalls++;
                if (ThrowCalls == 1) throw new InvalidOperationException("the evidence file is locked");
                return Task.FromResult(ActionOutcome.Done);
            });
            ShowData();
        }

        public AsyncCommand Apply { get; }

        public AsyncCommand Undo { get; }

        public AsyncCommand Gated { get; }

        public AsyncCommand Slow { get; }

        public AsyncCommand Throws { get; }

        public int Applied, Asked, Undone, Restarted, Measured, ThrowCalls;

        public bool AskedWhilePaletteOpen;

        public bool CanRestart;

        public Func<bool>? PaletteOpen;

        public TaskCompletionSource SlowEnds { get; } = new();

        public Func<Task<bool>> Confirm { get; set; } = () => Task.FromResult(false);

        protected override IEnumerable<PageCommand> Commands =>
        [
            new("Apply the change", GlyphAction.Save, Apply),
            new("Undo every change", GlyphAction.Discard, Undo) { Danger = true },
            new("Restart the logger", GlyphAction.Refresh, Gated) { Reason = () => "The logger is not installed" },
            new("Measure the build", GlyphAction.Refresh, Slow),
            new("Read the evidence", GlyphAction.Search, Throws),
        ];
    }

    static (ShellViewModel Shell, ActionPage Actions, TestPage Memory) WithActions()
    {
        var log = Shells.Log();
        var actions = new ActionPage(log);
        var memory = new TestPage("Memory", "Live headroom.").Data();
        var shell = new ShellViewModel([new Section("optimise", "Optimise", Sections.Machine, actions), new Section("memory", "Memory", Sections.Monitor, memory)], log);
        actions.PaletteOpen = () => shell.Palette.IsOpen;
        return (shell, actions, memory);
    }

    static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(time, DispatcherPriority.Background, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    /// <summary>Runs the test on the UI thread with a dispatcher, as the palette runs.</summary>
    static void OnUi(Action test) => PageRenderer.InPage("Dark", () => new Grid(), _ =>
    {
        test();
        return 0;
    });

    static PaletteRow Row(CommandPalette palette, string name) => palette.Rows.Single(r => r.Name == name);

    /// <summary>Runs a row and lets everything it started settle.</summary>
    static bool Run(CommandPalette palette, string name)
    {
        var task = palette.RunAsync(Row(palette, name));
        Pump(TimeSpan.FromMilliseconds(80));
        return task.IsCompletedSuccessfully && task.Result;
    }

    [Fact]
    public void Every_page_is_listed_with_only_the_page_on_screen_commands()
    {
        OnUi(() =>
        {
            var (shell, _, _) = WithActions();
            Assert.True(shell.Palette.Open());
            Assert.Equal(["Optimise", "Memory"], shell.Palette.Rows.Where(r => r.Group == CommandPalette.PagesGroup).Select(r => r.Name));
            Assert.Equal(["Apply the change", "Undo every change", "Restart the logger", "Measure the build", "Read the evidence"],
                shell.Palette.Rows.Where(r => r.Group == "Optimise").Select(r => r.Name));
            Assert.True(Run(shell.Palette, "Memory"));                                      // to the other page
            Assert.Equal("Memory", shell.Page.Title);
            shell.Palette.Open();
            Assert.DoesNotContain(shell.Palette.Rows, r => r.Group == "Optimise");          // its commands went with it
            Assert.Equal(2, shell.Palette.Rows.Count(r => r.Group == CommandPalette.PagesGroup));
        });
    }

    [Fact]
    public void Typing_ranks_and_selects_the_best_the_arrows_move_and_nothing_matching_runs_nothing()
    {
        OnUi(() =>
        {
            var (shell, actions, _) = WithActions();
            var palette = shell.Palette;
            palette.Open();
            palette.Query = "ch";
            Assert.Equal("Apply the change", palette.Selected!.Name);                     // two word starts: the first listed wins
            palette.Query = "undo";
            Assert.Equal(["Undo every change"], palette.Rows.Select(r => r.Name));
            palette.Query = "e";
            Assert.Equal(0, palette.SelectedIndex);
            palette.Move(1);
            Assert.Equal(1, palette.SelectedIndex);
            palette.Move(-5);
            Assert.Equal(0, palette.SelectedIndex);                                         // stays on the list
            palette.Query = "apply";
            Assert.Equal("Apply the change", palette.Selected!.Name);                     // the selection follows the filter
            palette.Query = "zzz";
            Assert.True(palette.NothingMatched);
            Assert.Equal(-1, palette.SelectedIndex);
            var ran = palette.RunSelectedAsync();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(ran.Result);
            Assert.True(palette.IsOpen);
            Assert.Equal(0, actions.Applied);
            palette.Query = "apply";
            _ = palette.RunSelectedAsync();
            Pump(TimeSpan.FromMilliseconds(80));
            Assert.Equal(1, actions.Applied);
            Assert.False(palette.IsOpen);
        });
    }

    [Fact]
    public void A_danger_command_runs_only_through_its_own_question_once_on_yes_never_on_no()
    {
        OnUi(() =>
        {
            var (shell, actions, _) = WithActions();
            var answer = new TaskCompletionSource<bool>();
            actions.Confirm = () => answer.Task;
            shell.Palette.Open();
            Assert.True(Row(shell.Palette, "Undo every change").Danger);
            Assert.True(Run(shell.Palette, "Undo every change"));
            Assert.Equal(1, actions.Asked);
            Assert.False(actions.AskedWhilePaletteOpen);                                      // the palette closed before the question
            Assert.Equal(0, actions.Undone);                                                  // nothing before the answer
            answer.SetResult(false);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(0, actions.Undone);                                                  // No: nothing

            actions.Confirm = () => Task.FromResult(true);
            shell.Palette.Open();
            Run(shell.Palette, "Undo every change");
            Assert.Equal((2, 1), (actions.Asked, actions.Undone));                            // Yes: once
            actions.Undo.Execute(null);                                                       // and its button asks the same question
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal((3, 2), (actions.Asked, actions.Undone));
        });
    }

    [Fact]
    public void A_page_change_under_it_closes_it_and_a_row_no_longer_offered_runs_nothing()
    {
        // Round 1: the tray's Open queue changed the page while the palette was open, and its old row still ran.
        OnUi(() =>
        {
            var (shell, actions, memory) = WithActions();
            shell.Palette.Open();
            var stale = Row(shell.Palette, "Apply the change");
            shell.Show(shell.Sections.Single(s => s.Title == "Memory"));                   // as the tray's Open does
            Assert.False(shell.Palette.IsOpen);
            shell.Palette.Open();
            var ran = shell.Palette.RunAsync(stale);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.False(ran.Result);
            Assert.Equal(0, actions.Applied);
            Assert.Equal("Apply the change: it is no longer offered here", shell.Palette.Message);
        });
    }

    [Fact]
    public void A_disabled_command_runs_nothing_says_why_and_follows_its_command_live()
    {
        OnUi(() =>
        {
            var (shell, actions, _) = WithActions();
            var palette = shell.Palette;
            palette.Open();
            Assert.Equal("The logger is not installed", Row(palette, "Restart the logger").Reason);
            Assert.False(Run(palette, "Restart the logger"));
            Assert.Equal(0, actions.Restarted);
            Assert.True(palette.IsOpen);
            Assert.Equal("Restart the logger: The logger is not installed", palette.Message);

            actions.CanRestart = true;
            actions.Gated.Refresh();                                                          // its command says it may run now
            Assert.Null(Row(palette, "Restart the logger").Reason);
            Assert.True(Run(palette, "Restart the logger"));
            Assert.Equal(1, actions.Restarted);
        });
    }

    [Fact]
    public void A_running_command_is_not_started_twice_and_one_that_throws_says_so_and_runs_again()
    {
        OnUi(() =>
        {
            var (shell, actions, _) = WithActions();
            var palette = shell.Palette;
            palette.Open();
            Run(palette, "Measure the build");
            Assert.Equal(1, actions.Measured);
            palette.Open();
            Assert.Equal(PageCommand.Running, Row(palette, "Measure the build").Reason);
            Assert.False(Run(palette, "Measure the build"));
            Assert.Equal(1, actions.Measured);                                                // not twice
            actions.SlowEnds.SetResult();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Null(Row(palette, "Measure the build").Reason);                            // live: it ended while open

            Run(palette, "Read the evidence");
            Assert.Contains(shell.Activity, l => l.Message.Contains("the evidence file is locked"));  // reported as its button would
            palette.Open();
            Assert.True(Run(palette, "Read the evidence"));
            Assert.Equal(2, actions.ThrowCalls);                                              // and it can run again
        });
    }

    [Theory]
    [InlineData(LeaveChoice.Stay, "Queue", true)]
    [InlineData(LeaveChoice.Discard, "Memory", false)]
    [InlineData(LeaveChoice.Save, "Memory", false)]
    public void Leaving_a_page_with_edits_asks_the_same_question_and_does_as_answered(LeaveChoice choice, string after, bool kept)
    {
        OnUi(() =>
        {
            var (shell, first, _, _) = Shells.WithTestPages();
            first.Edits = "slots: 3";
            shell.Palette.Open();
            var going = shell.Palette.RunAsync(Row(shell.Palette, "Memory"));
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.True(shell.IsAsking);                                                      // the shell's own question
            Assert.False(shell.Palette.IsOpen);                                               // the palette closed first
            Assert.False(shell.Palette.Open());                                               // and it won't open over the question
            Assert.False(shell.RefreshPage());                                                // nor F5 run under it
            shell.Answer(choice);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(after, shell.Page.Title);
            Assert.Equal(kept, first.Edits is not null);
            Assert.Equal(choice == LeaveChoice.Save ? 1 : 0, first.SaveCount);
        });
    }

    [Fact]
    public void A_failed_save_keeps_the_page_and_its_edits()
    {
        OnUi(() =>
        {
            var (shell, first, _, _) = Shells.WithTestPages();
            first.Edits = "slots: 3";
            first.OnSave = () => Task.FromResult(ActionOutcome.Failed("the limits file is read-only"));
            shell.Palette.Open();
            _ = shell.Palette.RunAsync(Row(shell.Palette, "Memory"));
            Pump(TimeSpan.FromMilliseconds(50));
            shell.Answer(LeaveChoice.Save);
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal("Queue", shell.Page.Title);
            Assert.Equal("slots: 3", first.Edits);
        });
    }

    [Fact]
    public void The_settings_page_lists_every_action_it_offers_in_its_state()
    {
        OnUi(() =>
        {
            var settings = new SettingsFixture();
            var shell = settings.Shell();
            shell.Show(shell.Sections.Single(s => s.Key == "settings"));
            var palette = shell.Palette;
            palette.Open();
            Assert.Equal("There are no changes to save", Row(palette, "Save settings").Reason);
            Assert.Equal("There are no changes to discard", Row(palette, "Discard settings changes").Reason);
            palette.Close();

            settings.Page.NotifyMemoryPressure = !settings.Page.NotifyMemoryPressure;        // an edit
            palette.Open();
            Assert.Null(Row(palette, "Save settings").Reason);
            Assert.True(Run(palette, "Save settings"));
            Assert.False(settings.Page.HasUnsavedEdits);                                      // the same save as the button
            Assert.Contains(shell.Activity, l => l.Message.Contains("Save settings"));

            settings.Page.NotifyMemoryPressure = !settings.Page.NotifyMemoryPressure;
            palette.Open();
            Assert.True(Run(palette, "Discard settings changes"));
            Assert.False(settings.Page.HasUnsavedEdits);
        });
    }

    [Fact]
    public void A_page_that_failed_lists_its_retry_and_a_page_with_a_refresh_lists_it_and_F5_runs_it()
    {
        OnUi(() =>
        {
            var (shell, first, second, _) = Shells.WithTestPages();
            first.Failed(log: shell.Log);
            shell.Palette.Open();
            Assert.True(Run(shell.Palette, "Retry"));
            Assert.Equal(1, first.RetryCount);

            var scheduler = new RefreshScheduler(driver: new IdleDriver());
            second.Polling(scheduler, shell.Log);
            Assert.False(shell.RefreshPage());                                               // Queue has no refresh: F5 does nothing
            shell.Show(shell.Sections.Single(s => s.Title == "Memory"));
            shell.Palette.Open();
            Assert.Contains(shell.Palette.Rows, r => r.Name == "Refresh" && r.Group == "Memory");
            shell.Palette.Close();
            var reads = 0;
            second.OnLoad = _ =>
            {
                reads++;
                return Task.FromResult("again");
            };
            Assert.True(shell.RefreshPage());
            Pump(TimeSpan.FromMilliseconds(80));
            Assert.Equal(1, reads);
        });
    }

    [Fact]
    public void The_tray_pause_or_resume_says_what_it_does_after_each_change_and_when_the_pause_runs_out()
    {
        OnUi(() =>
        {
            var tray = new TrayFixture();
            var palette = tray.Shell.Palette;
            palette.Open();
            Assert.Contains(palette.Rows, r => r.Name == "Pause notifications for 1 hour");
            Assert.Contains(palette.Rows, r => r.Name == "Exit");
            Run(palette, "Pause notifications for 1 hour");
            Assert.NotNull(tray.Notifier.PausedUntil);
            palette.Open();
            var resume = palette.Rows.Single(r => r.Name.StartsWith("Resume notifications", StringComparison.Ordinal));
            Assert.Equal("Resume notifications (paused until 10:10)", resume.Name);
            Assert.Equal(tray.Controller.Tray.Menu().Single(i => i.Id == "exit").Run, palette.Rows.Single(r => r.Name == "Exit").Run); // Exit: the menu's own
            Run(palette, resume.Name);
            Assert.Null(tray.Notifier.PausedUntil);
        });
    }

    sealed class IdleDriver : IRefreshDriver
    {
        public void Start(Action tick)
        {
        }

        public void Stop()
        {
        }
    }

    sealed class SoonPanel : IPanelCommands
    {
        public string Name = "Resume notifications (paused until 09:10)";

        public IReadOnlyList<PaletteRow> Commands() => [new PaletteRow(Name, CommandPalette.PanelGroup, Glyphs.For(GlyphAction.Pause), null, () => Task.CompletedTask)];

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();

        public int Listeners => Changed?.GetInvocationList().Length ?? 0;
    }

    [Fact]
    public void A_row_that_changes_by_itself_is_read_again_while_open_and_not_listened_to_once_closed()
    {
        OnUi(() =>
        {
            var (shell, _, _) = WithActions();
            var panel = new SoonPanel();
            shell.Palette.Panel = panel;
            shell.Palette.Open();
            panel.Name = "Pause notifications for 1 hour";                                  // the pause runs out
            panel.Raise();
            Assert.Contains(shell.Palette.Rows, r => r.Name == "Pause notifications for 1 hour");
            shell.Palette.Close();
            Assert.Equal(0, panel.Listeners);                                                 // closed, it no longer listens
        });
    }

    [Fact]
    public void The_tray_looks_at_its_pause_on_the_panels_one_scheduler()
    {
        OnUi(() =>
        {
            var clock = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
            var scheduler = new RefreshScheduler(() => clock, new IdleDriver());
            var tray = new TrayFixture(refreshes: scheduler);
            Assert.Contains(scheduler.Refreshes, r => r.Name == "Notification pause" && r.IsActive);
            var changes = 0;
            tray.Controller.Tray.Changed += () => changes++;
            tray.Controller.Tray.Menu().Single(i => i.Id == "pause").Run();
            changes = 0;
            tray.Now = tray.Now.AddHours(2);
            clock += RefreshScheduler.Fast + TimeSpan.FromSeconds(1);                          // its next look is due
            scheduler.Tick();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(1, changes);                                                          // its tick noticed the pause had run out
        });
    }

    [Fact]
    public void The_tray_notices_its_pause_running_out_and_the_palette_says_Pause_again()
    {
        OnUi(() =>
        {
            var tray = new TrayFixture();
            var palette = tray.Shell.Palette;
            palette.Open();
            Run(palette, "Pause notifications for 1 hour");
            palette.Open();
            Assert.Contains(palette.Rows, r => r.Name.StartsWith("Resume notifications", StringComparison.Ordinal));
            tray.Now = tray.Now.AddHours(1).AddMinutes(1);                                   // the hour has passed
            tray.Controller.Tray.NoticePause();                                               // as the scheduler's tick does
            Assert.Contains(palette.Rows, r => r.Name == "Pause notifications for 1 hour");
            Assert.DoesNotContain(palette.Rows, r => r.Name.StartsWith("Resume", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("HighContrast")]
    public void The_overlay_shows_its_rows_with_names_a_screen_reader_says_and_says_nothing_matches(string theme)
    {
        // Off screen no window can take the keyboard, so where it goes is the end-to-end test's (CommandPaletteE2ETests).
        PageRenderer.InPage(theme, () =>
        {
            var (shell, _, _) = WithActions();
            return new ShellView { DataContext = shell };
        }, page =>
        {
            var view = ShellSupport.Under<ShellView>(page).Single();
            var shell = (ShellViewModel)view.DataContext;
            shell.Palette.Open();
            PageRenderer.Settle(page);
            var palette = ShellSupport.Under<CommandPaletteView>(page).Single();
            var search = (TextBox)palette.FindName("Search");
            Assert.True(palette.IsVisible);
            Assert.Equal("Search pages and commands", AutomationProperties.GetName(search));
            Assert.Equal("Command palette", AutomationProperties.GetName(palette));
            shell.Palette.Query = "restart";
            PageRenderer.Settle(page);
            Assert.Equal("restart", search.Text);
            var results = (ListBox)palette.FindName("Results");
            var item = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
            var peer = UIElementAutomationPeer.CreatePeerForElement(item);
            Assert.Equal("Restart the logger, Optimise, unavailable: The logger is not installed", peer.GetName());
            Assert.Equal("The logger is not installed", peer.GetItemStatus());
            Assert.Same(shell.Palette.Selected, results.SelectedItem);
            var chosen = (ListBoxItem)results.ItemContainerGenerator.ContainerFromItem(results.SelectedItem);
            Assert.True(chosen.IsSelected && ReferenceEquals(item, chosen));
            Assert.Contains("The logger is not installed", PageRenderer.TextsOf(item));     // the reason is seen too
            shell.Palette.Query = "zzz";
            PageRenderer.Settle(page);
            Assert.Equal(Visibility.Visible, ((TextBlock)palette.FindName("Nothing")).Visibility);
            shell.Palette.Close();
            PageRenderer.Settle(page);
            Assert.False(palette.IsVisible);
            return 0;
        }, 1360, 860);
    }

    sealed class FakeAnimation(bool on) : IWindowsAnimation
    {
        public bool On { get; private set; } = on;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    [Fact]
    public void It_fades_in_on_the_gate_at_once_with_motion_off_and_holds_no_clock_hidden()
    {
        foreach (var animates in new[] { true, false })
        {
            PageRenderer.InPage("Dark", () => new ShellView { DataContext = WithActions().Shell }, page =>
            {
                var gate = new Motion(page.Resources, new FakeAnimation(animates));
                page.Resources[Motion.GateKey] = gate;
                var shell = (ShellViewModel)ShellSupport.Under<ShellView>(page).Single().DataContext;
                var palette = ShellSupport.Under<CommandPaletteView>(page).Single();
                shell.Palette.Open();
                Assert.Equal(animates, palette.Fading);
                shell.Palette.Close();
                Assert.False(palette.Fading);                                                // hidden: no clock
                return 0;
            }, 1360, 860);
        }
    }
}
