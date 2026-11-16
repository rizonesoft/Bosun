using System.IO;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Settings;

/// <summary>
/// The Settings page (D02 T02 §5, design/SPEC.md §15, General): its two settings save and read back, a save that
/// only half took says so and keeps the rest as an edit, and what could not be read is never offered for editing.
/// Everything here is over a settings file and a Run key that are only memory.
/// </summary>
public class SettingsPageTests
{
    private static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
    });

    // ---- the page's state ----

    [Fact]
    public void Each_setting_changed_is_an_unsaved_edit_and_a_save_stores_both_reads_them_back_and_leaves_the_page_clean()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            var page = f.Page;
            Assert.Equal((PageState.Data, ThemeChoice.FollowWindows, false, false), (page.State, page.SelectedTheme.Value, page.StartWithWindows, page.HasUnsavedEdits));
            Assert.Equal(["Follow Windows", "Dark", "Light"], page.ThemeOptions.Select(option => option.Name));
            Assert.False(page.Save.CanExecute(null));
            Assert.False(page.DiscardChanges.CanExecute(null));

            page.SelectedTheme = f.Option(ThemeChoice.Dark);
            Assert.True(page.HasUnsavedEdits);
            Assert.True(page.Save.CanExecute(null));
            page.SelectedTheme = f.Option(ThemeChoice.FollowWindows); // put back by hand: no longer an edit
            Assert.False(page.HasUnsavedEdits);
            page.StartWithWindows = true;
            Assert.True(page.HasUnsavedEdits);
            page.SelectedTheme = f.Option(ThemeChoice.Dark);

            var applied = new List<ThemeChoice>();
            f.Store.Changed += settings => applied.Add(settings.Theme); // as the theme service listens

            var outcome = page.SaveAsync().Result;

            Assert.True(outcome.Succeeded);
            Assert.Equal("theme Dark, Start with Windows on", outcome.Reason);
            Assert.Equal("Saved and read back: theme Dark, Start with Windows on.", page.Status);
            Assert.False(page.HasUnsavedEdits);
            Assert.False(page.Save.CanExecute(null));
            Assert.Equal(ThemeChoice.Dark, f.OnDisk().Theme);
            Assert.Equal("\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray", f.Key.Values[StartupEntry.Name]);
            Assert.Equal([ThemeChoice.Dark], applied); // the theme is applied by the save, with no restart

            // A new page over the same file and key shows what was saved.
            var again = new SettingsFixture();
            foreach (var (path, bytes) in f.Files.Files) again.Files.Files[path] = bytes;
            again.Key.Values[StartupEntry.Name] = f.Key.Values[StartupEntry.Name];
            again.Open();
            Assert.Equal((ThemeChoice.Dark, true, false), (again.Page.SelectedTheme.Value, again.Page.StartWithWindows, again.Page.HasUnsavedEdits));
        });
    }

    [Fact]
    public void Discard_puts_the_page_back_to_what_is_stored_and_a_save_with_nothing_changed_writes_nothing()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.SelectedTheme = f.Option(ThemeChoice.Light);
            f.Page.StartWithWindows = true;

            f.Page.Discard();

            Assert.Equal((ThemeChoice.FollowWindows, false, false), (f.Page.SelectedTheme.Value, f.Page.StartWithWindows, f.Page.HasUnsavedEdits));
            Assert.Empty(f.Files.Files);
            Assert.Empty(f.Key.Values);

            var nothing = f.Page.SaveAsync().Result;
            Assert.True(nothing.Succeeded);
            Assert.Equal("Nothing to save.", f.Page.Status);
            Assert.Empty(f.Files.Files);
        });
    }

    // ---- a save that only half took ----

    [Fact]
    public void When_the_file_s_save_fails_and_the_run_key_s_works_the_one_is_saved_and_the_other_stays_an_edit_with_its_reason()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark);
            f.Page.StartWithWindows = true;
            f.Files.FailWrite = new IOException("the disk is full");

            var outcome = f.Page.SaveAsync().Result;

            Assert.False(outcome.Succeeded);
            Assert.Equal("Saved and read back: Start with Windows on. panel.json could not be saved: the disk is full", outcome.Reason);
            Assert.Equal("Saved and read back: Start with Windows on. Not saved: panel.json could not be saved: the disk is full", f.Page.Status);
            Assert.True(f.Key.Values.ContainsKey(StartupEntry.Name)); // this one took
            Assert.Equal(ThemeChoice.FollowWindows, f.Store.Current.Theme); // this one did not
            Assert.True(f.Page.HasUnsavedEdits);                            // and is still an edit
            Assert.Equal((ThemeChoice.Dark, true), (f.Page.SelectedTheme.Value, f.Page.StartWithWindows)); // nothing the person chose is lost

            // The fault gone, a second save finishes it, saving only what was left.
            f.Files.FailWrite = null;
            var second = f.Page.SaveAsync().Result;
            Assert.True(second.Succeeded);
            Assert.Equal("theme Dark", second.Reason);
            Assert.False(f.Page.HasUnsavedEdits);
            Assert.Equal(ThemeChoice.Dark, f.OnDisk().Theme);
        });
    }

    [Fact]
    public void When_the_run_key_s_write_fails_and_the_file_s_works_the_theme_is_saved_and_discard_restores_what_is_really_stored()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture().Open();
            f.Page.SelectedTheme = f.Option(ThemeChoice.Light);
            f.Page.StartWithWindows = true;
            f.Key.FailWrite = new UnauthorizedAccessException("the key is locked by policy");

            var outcome = f.Page.SaveAsync().Result;

            Assert.False(outcome.Succeeded);
            Assert.Equal("Saved and read back: theme Light. Start with Windows could not be turned on: the key is locked by policy", outcome.Reason);
            Assert.Equal(ThemeChoice.Light, f.OnDisk().Theme);
            Assert.Empty(f.Key.Values);
            Assert.True(f.Page.HasUnsavedEdits);

            // Discard goes back to what the file and the key hold now: the saved theme stays, the failed switch is off.
            f.Page.Discard();
            Assert.Equal((ThemeChoice.Light, false, false), (f.Page.SelectedTheme.Value, f.Page.StartWithWindows, f.Page.HasUnsavedEdits));
            Assert.Null(f.Page.Status);
        });
    }

    [Fact]
    public void Leaving_on_a_half_saved_save_is_refused_and_leaving_after_discard_is_not()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            var settings = shell.Sections.Single(section => section.Key == "settings");
            shell.Show(settings);
            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark);
            f.Page.StartWithWindows = true;
            f.Key.FailWrite = new UnauthorizedAccessException("the key is locked by policy");

            var leaving = shell.GoToAsync(shell.Sections[0]);
            Assert.Equal(new LeaveQuestion("Settings"), shell.Asking);
            shell.Answer(LeaveChoice.Save);
            Assert.True(StartupSupport.Pump(() => leaving.IsCompleted));
            Assert.False(leaving.Result); // the save did not finish, so the page is not left
            Assert.Same(f.Page, shell.Page);
            Assert.StartsWith("Not saved: Saved and read back: theme Dark.", f.Page.SaveFailure);
            Assert.Contains("the key is locked by policy", f.Page.SaveFailure);
            Assert.True(f.Page.HasUnsavedEdits);
            Assert.Contains(shell.Activity, entry => entry.Severity == LogSeverity.Fail && entry.Message.StartsWith("Save Settings: ", StringComparison.Ordinal));

            leaving = shell.GoToAsync(shell.Sections[0]);
            shell.Answer(LeaveChoice.Discard);
            Assert.True(StartupSupport.Pump(() => leaving.IsCompleted));
            Assert.True(leaving.Result);
            Assert.NotSame(f.Page, shell.Page);
            Assert.Equal(ThemeChoice.Dark, f.OnDisk().Theme); // what was saved stays saved
            Assert.False(f.Page.HasUnsavedEdits);
        });
    }

    // ---- what could not be read ----

    [Fact]
    public void A_settings_file_that_was_moved_aside_is_said_on_the_page_and_in_the_activity_pane()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            f.Files.Put(SettingsFixture.FilePath, "not json at all");
            var shell = f.Shell();

            Assert.Equal(PageState.Data, f.Page.State);
            Assert.Contains("panel.json was not valid settings (it is not JSON", f.Page.Notice);
            Assert.Contains("panel.json.bad", f.Page.Notice);
            Assert.Equal(f.Page.Notice, shell.Activity.Single(entry => entry.Severity == LogSeverity.Warn).Message);
            Assert.True(f.Files.Files.ContainsKey(SettingsFixture.FilePath + ".bad"));

            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark);
            Assert.True(f.Page.SaveAsync().Result.Succeeded);
            Assert.Null(f.Page.Notice); // saved: the defaults are no longer standing in
        });
    }

    [Fact]
    public void Settings_that_cant_be_read_are_the_failed_state_with_retry_and_nothing_is_offered_for_editing()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            f.Files.Put(SettingsFixture.FilePath, "{\"schema_version\": 1, \"theme\": \"Dark\"}");
            f.Files.FailRead = new IOException("the file is being used by another process");
            f.Open();

            Assert.Equal(PageState.Failed, f.Page.State);
            Assert.Equal("panel.json could not be read: the file is being used by another process", f.Page.FailureMessage);
            Assert.False(f.Page.HasUnsavedEdits);
            Assert.False(f.Page.Save.CanExecute(null));

            f.Page.Retry!.Execute(null); // still held: still failed, and the pane says so
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Count == 1));
            Assert.Equal(PageState.Failed, f.Page.State);
            Assert.Equal(LogSeverity.Fail, f.Log.Entries[0].Severity);

            var applied = new List<ThemeChoice>();
            f.Store.Changed += settings => applied.Add(settings.Theme); // as the theme service listens

            f.Files.FailRead = null; // released
            f.Page.Retry.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.Page.State == PageState.Data));
            Assert.Equal(ThemeChoice.Dark, f.Page.SelectedTheme.Value);
            // Round 1: the page showed the recovered theme and the panel went on drawing the default one.
            Assert.Equal([ThemeChoice.Dark], applied);
            Assert.False(f.Page.HasUnsavedEdits);
            Assert.Equal("{\"schema_version\": 1, \"theme\": \"Dark\"}", System.Text.Encoding.UTF8.GetString(f.Files.Files[SettingsFixture.FilePath])); // never written over
        });
    }

    [Fact]
    public void A_run_key_that_cant_be_read_is_the_failed_state_with_retry()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            f.Key.FailRead = new UnauthorizedAccessException("the key is locked by policy");
            f.Open();

            Assert.Equal(PageState.Failed, f.Page.State);
            Assert.Equal("The Windows start-up entry could not be read: the key is locked by policy", f.Page.FailureMessage);

            f.Key.FailRead = null;
            f.Key.Values[StartupEntry.Name] = f.Startup.Command;
            f.Page.Retry!.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.Page.State == PageState.Data));
            Assert.True(f.Page.StartWithWindows);
        });
    }

    [Fact]
    public void An_entry_windows_has_turned_off_is_said_so_with_the_way_to_windows_own_settings()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            f.Key.Values[StartupEntry.Name] = "\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray";
            f.Key.ApprovedBy[StartupEntry.Name] = false;
            f.Open();

            Assert.True(f.Page.StartWithWindows); // the person's choice stands
            Assert.Contains("Windows has Bosun Panel turned off in its start-up settings", f.Page.StartupNote);
            Assert.False(f.Page.HasUnsavedEdits);

            f.Page.OpenStartupSettings.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.StartupSettingsOpened == 1));
            Assert.Equal("Open Windows start-up settings", f.Log.Entries.Single().Message);

            // Turned back on in Windows, the page says so when it reads again.
            f.Key.ApprovedBy[StartupEntry.Name] = true;
            f.Page.Discard();
            Assert.Null(f.Page.StartupNote);
        });
    }

    [Fact]
    public void An_entry_turned_back_on_in_windows_is_read_again_when_the_person_comes_back_and_unsaved_edits_are_kept()
    {
        // Round 2: the page sent the person to Windows' settings and never looked again.
        OnUi(() =>
        {
            var f = new SettingsFixture();
            f.Key.Values[StartupEntry.Name] = "\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray";
            f.Key.ApprovedBy[StartupEntry.Name] = false;
            var shell = f.Shell();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));
            Assert.NotNull(f.Page.StartupNote);

            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark); // an edit, not saved
            f.Key.ApprovedBy[StartupEntry.Name] = true;        // turned on in Windows' own settings
            shell.WindowActivated();                           // and back to the panel

            Assert.Null(f.Page.StartupNote);
            Assert.True(f.Page.StartWithWindows);
            Assert.Equal(ThemeChoice.Dark, f.Page.SelectedTheme.Value); // the unsaved edit is still there
            Assert.True(f.Page.HasUnsavedEdits);

            // Removed altogether elsewhere while the page is away: read when the page comes back on screen.
            f.Page.Discard();
            shell.Show(shell.Sections[0]);
            f.Key.Values.Clear();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));
            Assert.False(f.Page.StartWithWindows);
            Assert.False(f.Page.HasUnsavedEdits);

            // A switch the person has changed and not saved is theirs: a read does not undo it.
            f.Page.StartWithWindows = true;
            shell.WindowActivated();
            Assert.True(f.Page.StartWithWindows);
            Assert.True(f.Page.HasUnsavedEdits);

            // Another page on screen is not told about the Settings page's world.
            shell.AskToLeave = _ => Task.FromResult(LeaveChoice.Discard);
            Assert.True(shell.GoToAsync(shell.Sections[0]).Result);
            f.Key.Values[StartupEntry.Name] = f.Startup.Command;
            shell.WindowActivated();
            Assert.False(f.Page.StartWithWindows); // not on screen: read when it next is
        });
    }

    [Fact]
    public void A_not_saved_line_from_leaving_is_cleared_by_a_later_save_that_works_and_by_discard()
    {
        // Round 2: the frame went on saying "Not saved" after the page's own Save had saved everything.
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));

            void FailOnTheWayOut()
            {
                f.Page.SelectedTheme = f.Option(f.Page.SelectedTheme.Value == ThemeChoice.Dark ? ThemeChoice.Light : ThemeChoice.Dark);
                f.Files.FailWrite = new IOException("the file is being used by another process");
                var leaving = shell.GoToAsync(shell.Sections[0]);
                shell.Answer(LeaveChoice.Save);
                Assert.True(StartupSupport.Pump(() => leaving.IsCompleted));
                Assert.False(leaving.Result);
                Assert.StartsWith("Not saved: ", f.Page.SaveFailure);
                f.Files.FailWrite = null;
            }

            FailOnTheWayOut();
            Assert.True(f.Page.SaveAsync().Result.Succeeded); // the page's own Save, once the file is free
            Assert.Null(f.Page.SaveFailure);
            Assert.False(f.Page.HasUnsavedEdits);

            FailOnTheWayOut();
            f.Page.Discard(); // or the page's own Discard
            Assert.Null(f.Page.SaveFailure);
            Assert.False(f.Page.HasUnsavedEdits);
        });
    }

    [Fact]
    public void A_start_up_entry_that_cant_be_read_on_return_is_said_so_with_read_again_and_edits_and_warnings_are_kept()
    {
        // The completion review: a read that failed on the way back was ignored, the switch went on as if current,
        // and Discard then wiped the warning that Windows had the entry off.
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));
            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark); // an edit, not saved
            Assert.Null(f.Page.StartupNote);

            f.Key.FailRead = new UnauthorizedAccessException("the key is locked by policy");
            shell.WindowActivated();

            Assert.Equal(PageState.Data, f.Page.State); // the page and its edit stay
            Assert.Equal("The Windows start-up entry could not be read: the key is locked by policy The switch shows what was last read.", f.Page.StartupNote);
            Assert.Equal("Read again", f.Page.StartupActionName);
            Assert.Equal(ThemeChoice.Dark, f.Page.SelectedTheme.Value);
            Assert.True(f.Page.HasUnsavedEdits);

            f.Page.Discard(); // discarding invents no value and hides no warning
            Assert.Contains("could not be read", f.Page.StartupNote);
            Assert.False(f.Page.StartWithWindows);
            Assert.False(f.Page.HasUnsavedEdits);

            f.Page.StartupAction!.Execute(null); // still locked: said in the pane, and the line stays
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Any(entry => entry.Severity == LogSeverity.Fail)));
            Assert.Contains("could not be read", f.Page.StartupNote);

            f.Key.FailRead = null; // unlocked, and the entry is in fact on
            f.Key.Values[StartupEntry.Name] = f.Startup.Command;
            f.Page.StartupAction!.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.Page.StartupNote is null), "Read again did not recover");
            Assert.True(f.Page.StartWithWindows);
            Assert.Null(f.Page.StartupAction);
            Assert.False(f.Page.HasUnsavedEdits);
        });
    }

    [Fact]
    public void An_entry_that_starts_another_copy_is_said_so_and_use_this_copy_repairs_it()
    {
        // The completion review: any value read as on, so after the panel moved the page said on while Windows
        // started the old path, and the unchanged switch gave Save nothing to do.
        OnUi(() =>
        {
            var f = new SettingsFixture();
            const string old = "\"D:\\Old\\Bosun.Panel.exe\" --tray";
            f.Key.Values[StartupEntry.Name] = old;
            f.Open();

            Assert.True(f.Page.StartWithWindows); // the person did choose it
            Assert.False(f.Page.HasUnsavedEdits);
            Assert.Equal($"Start with Windows is on for another copy of Bosun Panel ({old}). Windows would start that one, or nothing if it is gone.", f.Page.StartupNote);
            Assert.Equal("Use this copy", f.Page.StartupActionName);

            f.Page.StartupAction!.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.Page.StartupNote is null), "the repair did nothing");
            Assert.Equal(f.Startup.Command, f.Key.Values[StartupEntry.Name]);
            Assert.True(f.Page.StartWithWindows);
            Assert.Equal((LogSeverity.Info, "Point Start with Windows at this copy"), (f.Log.Entries.Single().Severity, f.Log.Entries.Single().Message));

            // A repair the registry refuses says why, and the line stays.
            f.Key.Values[StartupEntry.Name] = old;
            f.Page.Discard();
            f.Key.FailWrite = new UnauthorizedAccessException("the key is locked by policy");
            f.Page.StartupAction!.Execute(null);
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Count == 2));
            Assert.Equal(LogSeverity.Fail, f.Log.Entries[1].Severity);
            Assert.Contains("another copy", f.Page.StartupNote);

            // Turning it off is the other way out, through Save.
            f.Key.FailWrite = null;
            f.Page.StartWithWindows = false;
            Assert.True(f.Page.SaveAsync().Result.Succeeded);
            Assert.Empty(f.Key.Values);
            Assert.Null(f.Page.StartupNote);
        });
    }

    // ---- the page, drawn ----

    private static void Invoke(Button button) => ((IInvokeProvider)UIElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void The_page_is_drawn_in_the_frame_with_its_two_controls_and_save_stores_what_they_hold(string theme)
    {
        SettingsFixture f = null!;
        PageFrame frame = null!;
        PageRenderer.InPage(theme, () =>
        {
            f = new SettingsFixture().Open();
            frame = new PageFrame { DataContext = f.Page };
            return frame;
        }, host =>
        {
            var view = ShellSupport.Under<SettingsView>(frame).Single(); // through the frame's implicit template for the page
            Assert.Equal(["Settings", "How the panel starts, looks and notifies.", "General", "Theme", "Follow Windows", "Dark", "Light", "Start with Windows, in the tray", "Pressure alert threshold", "MB available", "Notifications", "Notify me about memory pressure", "Save", "Discard"], PageRenderer.TextsOf(frame));

            Assert.Equal("Theme", AutomationProperties.GetName(view.ThemeBox));
            Assert.Equal(f.Option(ThemeChoice.FollowWindows), view.ThemeBox.SelectedItem);
            Assert.Equal(3, view.ThemeBox.Items.Count);
            Assert.False(view.StartWithWindows.IsChecked);
            Assert.False(view.Save.IsEnabled);    // nothing to save yet
            Assert.False(view.Discard.IsEnabled);
            Assert.Same(host.FindResource("Btn.Primary"), view.Save.Style);
            Assert.Same(host.FindResource("Btn"), view.Discard.Style);
            Assert.False(view.StartupOff.IsVisible);

            // The person chooses Dark and ticks the box (the box as a screen reader does; opening the list is a
            // window of its own, seen in D02 T02 §10).
            view.ThemeBox.SelectedItem = f.Option(ThemeChoice.Dark);
            ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(view.StartWithWindows).GetPattern(PatternInterface.Toggle)).Toggle();
            PageRenderer.Settle(host);
            Assert.Equal((ThemeChoice.Dark, true), (f.Page.SelectedTheme.Value, f.Page.StartWithWindows));
            Assert.True(view.Save.IsEnabled);
            Assert.True(view.Discard.IsEnabled);
            Assert.Contains("Dark", PageRenderer.TextsOf(frame));

            Invoke(view.Save);
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Count == 1), "Save wrote no line");
            PageRenderer.Settle(host);
            Assert.Equal(ThemeChoice.Dark, f.OnDisk().Theme);
            Assert.True(f.Key.Values.ContainsKey(StartupEntry.Name));
            Assert.Equal("Saved and read back: theme Dark, Start with Windows on.", view.Status.Text);
            Assert.Equal("INFO 09:10:00 Save settings: theme Dark, Start with Windows on", f.Log.Entries[0].ToString());
            Assert.False(view.Save.IsEnabled);

            // Discard, from the button.
            view.ThemeBox.SelectedItem = f.Option(ThemeChoice.Light);
            PageRenderer.Settle(host);
            Invoke(view.Discard);
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Count == 2));
            PageRenderer.Settle(host);
            Assert.Equal(f.Option(ThemeChoice.Dark), view.ThemeBox.SelectedItem);
            Assert.Equal("Discard settings changes", f.Log.Entries[1].Message);
            return true;
        }, 900, 600);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Memory_pressure_notifications_are_edited_saved_obeyed_turned_back_on_discarded_and_kept_when_the_write_fails(string theme)
    {
        // D02 T02 §7: the setting through the real page and its view, with the notifier that reads it.
        SettingsFixture f = null!;
        PageFrame frame = null!;
        PageRenderer.InPage(theme, () =>
        {
            f = new SettingsFixture().Open();
            frame = new PageFrame { DataContext = f.Page };
            return frame;
        }, host =>
        {
            var view = ShellSupport.Under<SettingsView>(frame).Single();
            var box = view.NotifyMemoryPressure;
            var tray = new Bosun.Panel.Tests.Tray.FakeTrayIcon();
            tray.Install(Bosun.Panel.Tray.TrayState.Normal, "Bosun Panel");
            var notifier = new Bosun.Panel.Tray.Notifier(f.Store, f.Log) { Sink = tray };
            bool Raise() => notifier.Raise(Bosun.Panel.Tray.NotificationType.MemoryPressure, "Memory pressure", "4 GB free");
            void Toggle() => ((IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(box).GetPattern(PatternInterface.Toggle)).Toggle();
            void Press(Button button, Func<bool> until)
            {
                PageRenderer.Settle(host);
                Invoke(button);
                Assert.True(StartupSupport.Pump(until), $"{button.Name} did not end");
                PageRenderer.Settle(host);
            }

            Assert.True(box.IsVisible);
            Assert.Equal("Notify me about memory pressure", box.Content);
            Assert.True(box.IsChecked); // on until turned off
            Assert.True(Raise());

            Toggle(); // an edit, and nothing stored yet
            Assert.Equal((false, true, true), (f.Page.NotifyMemoryPressure, f.Page.HasUnsavedEdits, f.OnDisk().NotifyMemoryPressure));
            Assert.True(Raise());

            Press(view.Save, () => f.Page.Status is not null);
            Assert.Equal("Saved and read back: memory pressure notifications off.", view.Status.Text);
            Assert.Equal((false, false), (f.OnDisk().NotifyMemoryPressure, f.Page.HasUnsavedEdits));
            Assert.False(box.IsChecked);
            Assert.False(Raise()); // obeyed at once
            Assert.Equal(2, tray.Notifications.Count);

            Toggle(); // back on, in the same run
            Press(view.Save, () => f.Page.Status == "Saved and read back: memory pressure notifications on.");
            Assert.True(f.OnDisk().NotifyMemoryPressure);
            Assert.True(Raise());
            Assert.Equal(3, tray.Notifications.Count);

            Toggle(); // changed and then discarded: back to what is stored
            Assert.True(f.Page.HasUnsavedEdits);
            Press(view.Discard, () => !f.Page.HasUnsavedEdits);
            Assert.True(box.IsChecked);
            Assert.True(f.Page.NotifyMemoryPressure);

            Toggle(); // a write that fails keeps the edit, with why
            f.Files.FailWrite = new IOException("There is not enough space on the disk.");
            Press(view.Save, () => f.Page.Status is { } status && status.StartsWith("Not saved:", StringComparison.Ordinal));
            Assert.Contains("There is not enough space on the disk.", view.Status.Text);
            Assert.False(box.IsChecked);
            Assert.Equal((false, true, true), (f.Page.NotifyMemoryPressure, f.Page.HasUnsavedEdits, f.OnDisk().NotifyMemoryPressure));
            Assert.True(view.Save.IsEnabled);
            Assert.True(Raise()); // what is stored is what is obeyed
            return true;
        }, 900, 600);
    }

    [Fact]
    public void The_page_draws_its_notices_the_half_saved_line_and_windows_own_switch()
    {
        SettingsFixture f = null!;
        PageFrame frame = null!;
        PageRenderer.InPage("Dark", () =>
        {
            f = new SettingsFixture();
            f.Files.Put(SettingsFixture.FilePath, "not json at all");
            f.Key.Values[StartupEntry.Name] = "\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray";
            f.Key.ApprovedBy[StartupEntry.Name] = false;
            f.Open();
            frame = new PageFrame { DataContext = f.Page };
            return frame;
        }, host =>
        {
            var view = ShellSupport.Under<SettingsView>(frame).Single();
            Assert.True(view.Notice.IsVisible);
            Assert.Contains("panel.json.bad", view.Notice.Text);
            Assert.True(view.StartupOff.IsVisible);
            Assert.Contains("turned off in its start-up settings", view.StartupNote.Text);
            Assert.Equal("Open Startup settings", view.StartupAction.Content);
            Invoke(view.StartupAction);
            Assert.True(StartupSupport.Pump(() => f.StartupSettingsOpened == 1), "the action never ran");

            // Half saved: the line says what took and what did not.
            view.ThemeBox.SelectedItem = f.Option(ThemeChoice.Light);
            view.StartWithWindows.IsChecked = false;
            f.Key.FailWrite = new UnauthorizedAccessException("the key is locked by policy");
            PageRenderer.Settle(host);
            Invoke(view.Save);
            Assert.True(StartupSupport.Pump(() => f.Log.Entries.Any(entry => entry.Severity == LogSeverity.Fail)), "the failed half wrote no line");
            PageRenderer.Settle(host);
            Assert.Equal("Saved and read back: theme Light. Not saved: Start with Windows could not be turned off: the key is locked by policy", view.Status.Text);
            Assert.True(view.Save.IsEnabled); // there is still something to save
            Assert.False(view.Notice.IsVisible);
            return true;
        }, 900, 600);
    }
}
