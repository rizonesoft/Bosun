using System.IO;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Shell;
using Xunit;

namespace Bosun.Panel.Tests.Settings;

/// <summary>
/// The settings in the shell (D02 T02 §5): the Settings row opens the page over the panel's own settings, the
/// Activity pane's height is kept between runs, and neither it nor the theme overwrites the other.
/// </summary>
public class SettingsShellTests
{
    private static void OnUi(Action test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        test();
    });

    [Fact]
    public void The_settings_row_opens_the_settings_page_and_the_other_six_are_still_placeholders()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();

            Assert.Equal(7, shell.Sections.Count);
            foreach (var section in shell.Sections)
            {
                if (section.Key == "settings") Assert.IsType<SettingsPage>(section.Page);
                else Assert.IsType<PlaceholderPage>(section.Page);
            }
            Assert.Equal(("Settings", "How the panel starts, looks and notifies."), (f.Page.Title, f.Page.Summary));
            Assert.Equal("Panel ready", Assert.Single(shell.Activity).Message); // settings that read cleanly say nothing

            // A page that is not a row of the rail is refused, not dropped.
            Assert.Throws<ArgumentException>(() => Sections.All(new Dictionary<string, PageBase> { ["wallpaper"] = new TestPage() }));
        });
    }

    [Fact]
    public void The_panel_s_shell_over_a_real_data_folder_writes_its_settings_there_and_nowhere_else()
    {
        using var data = new TempFolder();
        OnUi(() =>
        {
            var store = new PanelSettingsStore(Path.Combine(data.Path, "panel.json")); // the real files, in a folder of the test's own
            var shell = WindowStart.NewShell(Dispatcher.CurrentDispatcher, store, new StartupEntry(new FakeRunKey(), SettingsFixture.Program), () => { });
            var page = (SettingsPage)shell.Sections.Single(section => section.Key == "settings").Page;

            page.SelectedTheme = page.ThemeOptions.Single(option => option.Value == ThemeChoice.Light);
            Assert.True(page.SaveAsync().Result.Succeeded);
            shell.ActivityHeight = 210;

            Assert.Equal(["panel.json"], Directory.GetFiles(data.Path).Select(Path.GetFileName));
            var read = new PanelSettingsStore(Path.Combine(data.Path, "panel.json")).Current;
            Assert.Equal((ThemeChoice.Light, 210), (read.Theme, read.ActivityPaneHeight));
        });
    }

    [Fact]
    public void The_activity_pane_s_height_is_saved_and_read_back_by_the_next_shell_within_its_limits()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            Assert.Equal(132, shell.ActivityHeight);

            shell.ActivityHeight = 220; // as the splitter leaves it
            Assert.Equal(220, f.OnDisk().ActivityPaneHeight);

            var next = f.Shell(); // the panel started again over the same file
            Assert.Equal(220, next.ActivityHeight);

            // A saved height outside the limits is brought inside them.
            f.Files.Put(SettingsFixture.FilePath, "{\"schema_version\": 1, \"theme\": \"Dark\", \"activity_pane_height\": 9000}");
            Assert.Equal(400, f.Shell().ActivityHeight);
            f.Files.Put(SettingsFixture.FilePath, "{\"schema_version\": 1, \"activity_pane_height\": 3}");
            Assert.Equal(64, f.Shell().ActivityHeight);
        });
    }

    [Fact]
    public void A_height_that_cant_be_saved_writes_a_warn_line_and_the_pane_stays_where_it_was_put()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            f.Files.FailWrite = new IOException("the disk is full");

            shell.ActivityHeight = 300;

            Assert.Equal(300, shell.ActivityHeight); // the person's pane is not put back
            var warn = shell.Activity.Single(entry => entry.Severity == LogSeverity.Warn);
            Assert.Equal($"The Activity pane's height was not saved: {SettingsFixture.FilePath} could not be saved: the disk is full", warn.Message);
            Assert.Equal(132, f.OnDisk().ActivityPaneHeight);

            shell.ActivityHeight = 300; // the same height again is no change: nothing more is written or said
            Assert.Single(shell.Activity, entry => entry.Severity == LogSeverity.Warn);
        });
    }

    [Fact]
    public void A_resize_while_the_theme_is_changed_but_not_saved_and_then_save_keeps_both()
    {
        OnUi(() =>
        {
            var f = new SettingsFixture();
            var shell = f.Shell();
            shell.Show(shell.Sections.Single(section => section.Key == "settings"));

            f.Page.SelectedTheme = f.Option(ThemeChoice.Dark); // changed, not saved
            shell.ActivityHeight = 260;                        // the pane resized meanwhile: saved at once
            Assert.Equal((ThemeChoice.FollowWindows, 260), (f.OnDisk().Theme, f.OnDisk().ActivityPaneHeight));
            Assert.True(f.Page.HasUnsavedEdits);               // the theme is still an unsaved edit

            Assert.True(f.Page.SaveAsync().Result.Succeeded);

            // The save changed the theme of the latest stored settings: the height saved since is still there.
            Assert.Equal((ThemeChoice.Dark, 260), (f.OnDisk().Theme, f.OnDisk().ActivityPaneHeight));

            shell.ActivityHeight = 180; // and the other way round: a later resize keeps the saved theme
            Assert.Equal((ThemeChoice.Dark, 180), (f.OnDisk().Theme, f.OnDisk().ActivityPaneHeight));
        });
    }

    [Fact]
    public void A_shell_with_no_settings_keeps_its_pane_s_height_for_the_run_only()
    {
        OnUi(() =>
        {
            var (shell, _, _, _) = Shells.WithTestPages();
            shell.ActivityHeight = 250;
            Assert.Equal(250, shell.ActivityHeight);
            Assert.Empty(shell.Activity);
        });
    }
}

/// <summary>Rules over the application's own source (D02 T02 §5): the stored theme is applied before the window is made.</summary>
public class SettingsRulesTests
{
    [Fact]
    public void The_application_applies_the_stored_theme_before_it_makes_its_window_and_follows_later_saves()
    {
        var app = File.ReadAllText(Path.Combine(TestPaths.Panel(), "App.xaml.cs"));
        var make = app[app.IndexOf("private void MakeWindow()", StringComparison.Ordinal)..];
        make = make[..make.IndexOf("/// <summary>", StringComparison.Ordinal)];

        var read = make.IndexOf("new PanelSettingsStore(", StringComparison.Ordinal);
        var applied = make.IndexOf("theme.Apply(settings.Current.Theme)", StringComparison.Ordinal);
        var opened = make.IndexOf("WindowStart.Open(", StringComparison.Ordinal);
        Assert.True(read >= 0 && applied > read && opened > applied, "the settings are read, then the theme applied, then the window made");
        Assert.Contains("settings.Changed += saved => theme.Apply(saved.Theme)", make, StringComparison.Ordinal); // a saved change applies with no restart
        Assert.Contains("_windowsTheme.Listen()", make, StringComparison.Ordinal);                                // and Windows' own changes are followed
        Assert.Contains("_startup.DataFolder", make, StringComparison.Ordinal);                                   // in the panel's data folder, wherever that is
        Assert.Contains("new RegistryRunKey()", make, StringComparison.Ordinal);                                  // the real Run key, only here

        // The listener on Windows' static event is let go when the panel exits.
        var exit = app[app.IndexOf("protected override void OnExit", StringComparison.Ordinal)..];
        Assert.Contains("_windowsTheme?.Dispose()", exit[..exit.IndexOf('}')], StringComparison.Ordinal);
    }
}
