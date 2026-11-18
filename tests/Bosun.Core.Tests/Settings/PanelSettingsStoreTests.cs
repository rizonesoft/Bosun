using System.Text;
using Xunit;

namespace Bosun.Core.Tests.Settings;

/// <summary>A folder of a test's own under the system's temporary folder, removed when the test ends.</summary>
sealed class Scratch : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bosun-settings-test-" + Guid.NewGuid().ToString("N"))).FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Settings => File("panel.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something still holds a file in it: the system's own clean-up takes the folder later.
        }
    }
}

/// <summary>The settings themselves (D02 T02 §5): their defaults, and a height that is always within its limits.</summary>
public class PanelSettingsTests
{
    [Fact]
    public void The_defaults_follow_windows_with_a_132_px_pane_and_a_height_is_always_within_its_limits()
    {
        Assert.Equal((ThemeChoice.FollowWindows, 132), (PanelSettings.Defaults.Theme, PanelSettings.Defaults.ActivityPaneHeight));
        Assert.Equal(new PanelSettings(), PanelSettings.Defaults);

        Assert.Equal(400, new PanelSettings { ActivityPaneHeight = 9000 }.ActivityPaneHeight);
        Assert.Equal(64, new PanelSettings { ActivityPaneHeight = -5 }.ActivityPaneHeight);
        Assert.Equal(64, (PanelSettings.Defaults with { ActivityPaneHeight = 1 }).ActivityPaneHeight);
        Assert.Equal(132, new PanelSettings { ActivityPaneHeight = double.NaN }.ActivityPaneHeight);
        Assert.Equal(250.5, new PanelSettings { ActivityPaneHeight = 250.5 }.ActivityPaneHeight);

        // A change is a new record: the one it came from is as it was.
        var dark = PanelSettings.Defaults with { Theme = ThemeChoice.Dark };
        Assert.Equal(ThemeChoice.FollowWindows, PanelSettings.Defaults.Theme);
        Assert.Equal(132, dark.ActivityPaneHeight);
    }
}

/// <summary>
/// `panel.json` (D02 T02 §5): what is saved is read back, a file that is not settings is kept and not lost, a file
/// that can't be read is never saved over, and a save that fails leaves everything as it was.
/// </summary>
public class PanelSettingsStoreTests
{
    private static readonly PanelSettings Chosen = new() { Theme = ThemeChoice.Light, ActivityPaneHeight = 200 };

    // ---- reading ----

    [Fact]
    public void A_file_that_is_not_there_is_the_defaults_with_no_notice_and_a_saved_file_loads_what_was_saved()
    {
        using var scratch = new Scratch();
        var store = new PanelSettingsStore(scratch.Settings);
        Assert.Equal((SettingsState.Defaults, PanelSettings.Defaults, null), (store.State, store.Current, store.Notice));
        Assert.False(File.Exists(scratch.Settings)); // reading makes nothing

        Assert.True(store.Save(Chosen).Succeeded);
        Assert.Equal(SettingsState.Loaded, store.State);
        Assert.Equal(["panel.json"], Directory.GetFiles(scratch.Path).Select(Path.GetFileName)); // and no temporary file is left

        var again = new PanelSettingsStore(scratch.Settings);
        Assert.Equal((SettingsState.Loaded, Chosen, null), (again.State, again.Current, again.Notice));

        // The file is JSON a person can read, with its version.
        var text = File.ReadAllText(scratch.Settings);
        Assert.Contains("\"schema_version\": 1", text);
        Assert.Contains("\"theme\": \"Light\"", text);
        Assert.Contains("\"activity_pane_height\": 200", text);
    }

    [Theory]
    [InlineData("not json at all", "it is not JSON")]
    [InlineData("[1, 2]", "it is not a JSON object")]
    [InlineData("{\"theme\": \"Dark\"}", "it has no schema_version")]
    [InlineData("{\"schema_version\": \"1\", \"theme\": \"Dark\"}", "it has no schema_version")]
    [InlineData("{\"schema_version\": 2, \"theme\": \"Dark\"}", "its schema_version is 2, and this panel reads 1")]
    [InlineData("{\"schema_version\": 1, \"theme\": \"Purple\"}", "its theme is not FollowWindows, Dark or Light")]
    [InlineData("{\"schema_version\": 1, \"theme\": 3}", "its theme is not FollowWindows, Dark or Light")]
    [InlineData("{\"schema_version\": 1, \"theme\": \"7\"}", "its theme is not FollowWindows, Dark or Light")]
    [InlineData("{\"schema_version\": 1, \"activity_pane_height\": \"tall\"}", "its activity_pane_height is not a number")]
    [InlineData("{\"schema_version\": 1, \"notify_memory_pressure\": \"no\"}", "its notify_memory_pressure is not true or false")]
    [InlineData("{\"schema_version\": 1, \"tray_notice_said\": 1}", "its tray_notice_said is not true or false")]
    [InlineData("", "it is not JSON")]
    public void A_file_that_is_not_settings_is_kept_as_bad_and_the_defaults_load_with_the_reason(string content, string reason)
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, content);
        File.WriteAllText(scratch.Settings + ".bad", "an older damaged file");

        var store = new PanelSettingsStore(scratch.Settings);

        Assert.Equal((SettingsState.Restored, PanelSettings.Defaults), (store.State, store.Current));
        Assert.Contains(reason, store.Notice);
        Assert.Contains("panel.json.bad", store.Notice);
        Assert.False(File.Exists(scratch.Settings));
        Assert.Equal(content, File.ReadAllText(store.BadPath)); // kept whole, the older one replaced

        // The panel carries on: a save now writes a new file, and the kept one stays.
        Assert.True(store.Save(Chosen).Succeeded);
        Assert.Equal((SettingsState.Loaded, null), (store.State, store.Notice));
        Assert.Equal(content, File.ReadAllText(store.BadPath));
        Assert.Equal(Chosen, new PanelSettingsStore(scratch.Settings).Current);
    }

    [Fact]
    public void A_height_outside_its_limits_is_brought_inside_them_and_the_file_and_its_theme_are_kept()
    {
        using var scratch = new Scratch();
        const string content = "{\"schema_version\": 1, \"theme\": \"Dark\", \"activity_pane_height\": 9000}";
        File.WriteAllText(scratch.Settings, content);

        var store = new PanelSettingsStore(scratch.Settings);

        Assert.Equal((SettingsState.Loaded, ThemeChoice.Dark, 400), (store.State, store.Current.Theme, store.Current.ActivityPaneHeight));
        Assert.Null(store.Notice);
        Assert.Equal(content, File.ReadAllText(scratch.Settings)); // not moved aside: one number out of range is not a damaged file
        Assert.False(File.Exists(store.BadPath));
    }

    [Fact]
    public void A_file_from_before_a_setting_existed_takes_that_setting_s_default_and_one_with_a_byte_order_mark_reads()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Light\", \"a_later_setting\": true}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var store = new PanelSettingsStore(scratch.Settings);

        Assert.Equal((SettingsState.Loaded, ThemeChoice.Light, 132), (store.State, store.Current.Theme, store.Current.ActivityPaneHeight));
    }

    [Fact]
    public void A_file_held_by_another_program_is_unavailable_left_as_it_is_and_never_saved_over_until_it_has_been_read()
    {
        using var scratch = new Scratch();
        const string content = "{\"schema_version\": 1, \"theme\": \"Dark\", \"activity_pane_height\": 300}";
        File.WriteAllText(scratch.Settings, content);

        PanelSettingsStore store;
        using (new FileStream(scratch.Settings, FileMode.Open, FileAccess.Read, FileShare.None)) // as an editor or a scanner holds it
        {
            store = new PanelSettingsStore(scratch.Settings);
            Assert.Equal((SettingsState.Unavailable, PanelSettings.Defaults), (store.State, store.Current)); // the defaults stand in for now
            Assert.StartsWith("panel.json could not be read: ", store.Notice);

            var refused = store.Save(Chosen);
            Assert.False(refused.Succeeded);
            Assert.StartsWith("Nothing is saved over settings that could not be read.", refused.Reason);
            Assert.False(store.Update(current => current with { Theme = ThemeChoice.Light }).Succeeded);
            Assert.Equal(PanelSettings.Defaults, store.Current);

            store.Reload(); // still held
            Assert.Equal(SettingsState.Unavailable, store.State);
        }
        Assert.Equal(content, File.ReadAllText(scratch.Settings)); // byte for byte as it was
        Assert.Equal(["panel.json"], Directory.GetFiles(scratch.Path).Select(Path.GetFileName));

        store.Reload(); // released: read at last
        Assert.Equal((SettingsState.Loaded, ThemeChoice.Dark, 300, null), (store.State, store.Current.Theme, store.Current.ActivityPaneHeight, store.Notice));
        Assert.True(store.Save(Chosen).Succeeded);
    }

    [Fact]
    public void A_damaged_file_that_cant_be_moved_aside_is_unavailable_and_left_where_it_is()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, "not json at all");
        Directory.CreateDirectory(scratch.Settings + ".bad"); // a folder stands where the kept copy would go
        File.WriteAllText(Path.Combine(scratch.Settings + ".bad", "keep.txt"), "x");

        var store = new PanelSettingsStore(scratch.Settings);

        Assert.Equal(SettingsState.Unavailable, store.State);
        Assert.Contains("is not valid settings (it is not JSON", store.Notice);
        Assert.Contains("could not be moved aside", store.Notice);
        Assert.Equal("not json at all", File.ReadAllText(scratch.Settings));
        Assert.False(store.Save(Chosen).Succeeded); // and not saved over
        Assert.Equal("not json at all", File.ReadAllText(scratch.Settings));
    }

    [Fact]
    public void A_file_far_larger_than_settings_are_is_never_read_whole_and_is_kept_as_bad()
    {
        // Round 1: the whole file was read, and copied again to parse, before anything looked at its size.
        using var scratch = new Scratch();
        var huge = "{\"schema_version\": 1, \"theme\": \"Dark\", \"padding\": \"" + new string('x', 8 * 1024 * 1024) + "\"}";
        File.WriteAllText(scratch.Settings, huge);
        var files = new Counting();

        var store = new PanelSettingsStore(scratch.Settings, files);

        Assert.Equal((SettingsState.Restored, PanelSettings.Defaults), (store.State, store.Current));
        Assert.Contains("it is larger than 64 KB", store.Notice);
        Assert.Equal(PanelSettingsStore.MaxFileBytes + 1, files.MostRead); // one byte past the limit, never the eight megabytes
        Assert.Equal(huge.Length, new FileInfo(store.BadPath).Length);     // kept whole, moved not copied

        // A file of exactly the limit is still read; valid settings padded to it load.
        var pad = PanelSettingsStore.MaxFileBytes - "{\"schema_version\": 1, \"theme\": \"Light\", \"padding\": \"\"}".Length;
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Light\", \"padding\": \"" + new string('x', pad) + "\"}");
        Assert.Equal(PanelSettingsStore.MaxFileBytes, new FileInfo(scratch.Settings).Length);
        store.Reload();
        Assert.Equal((SettingsState.Loaded, ThemeChoice.Light), (store.State, store.Current.Theme));
    }

    private sealed class Counting : PanelSettingsFiles
    {
        public int MostRead;

        public override byte[] Read(string path, int limit)
        {
            var bytes = base.Read(path, limit);
            MostRead = Math.Max(MostRead, bytes.Length);
            return bytes;
        }
    }

    [Fact]
    public void A_reload_that_reads_different_settings_tells_the_listeners_and_one_that_reads_the_same_does_not()
    {
        // Round 1: a file that was held and then released was read by Retry, and nothing that follows the settings
        // (the theme) was told.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Dark\"}");
        PanelSettingsStore store;
        var told = new List<PanelSettings>();
        using (new FileStream(scratch.Settings, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new PanelSettingsStore(scratch.Settings);
            store.Changed += told.Add;
            store.Reload(); // still held: still the defaults, nothing to tell
            Assert.Empty(told);
        }

        store.Reload();
        Assert.Equal(ThemeChoice.Dark, Assert.Single(told).Theme);

        store.Reload(); // the same again
        Assert.Single(told);
    }

    private sealed class Denied : PanelSettingsFiles
    {
        public bool Deny = true;

        public override byte[] Read(string path, int limit) => Deny ? throw new UnauthorizedAccessException("Access to the path is denied.") : base.Read(path, limit);
    }

    [Fact]
    public void A_settings_path_that_may_not_be_looked_at_is_unavailable_and_not_taken_for_a_first_run()
    {
        // The completion review: the store asked whether the file existed, and Windows answers no for a file in a
        // folder that may not be looked into, so the defaults loaded in silence and a save was allowed over it.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Dark\"}");
        var files = new Denied();

        var store = new PanelSettingsStore(scratch.Settings, files);

        Assert.Equal(SettingsState.Unavailable, store.State);
        Assert.Equal("panel.json could not be read: Access to the path is denied.", store.Notice);
        Assert.False(store.Save(Chosen).Succeeded);
        Assert.Equal("{\"schema_version\": 1, \"theme\": \"Dark\"}", File.ReadAllText(scratch.Settings));

        files.Deny = false;
        store.Reload();
        Assert.Equal((SettingsState.Loaded, ThemeChoice.Dark), (store.State, store.Current.Theme));

        // And a file, or its folder, that really is not there is still a first run.
        Assert.Equal(SettingsState.Defaults, new PanelSettingsStore(scratch.File("none.json")).State);
        Assert.Equal(SettingsState.Defaults, new PanelSettingsStore(Path.Combine(scratch.File("no-such-folder"), "panel.json")).State);
    }

    // ---- saving ----

    /// <summary>The store's hands on the disk, with one of them made to fail.</summary>
    private sealed class Faulty : PanelSettingsFiles
    {
        public bool FailWrite, FailReplace, ReadBackDifferent, FailDelete;

        public override void Write(string path, byte[] bytes)
        {
            if (FailWrite) throw new IOException("the disk is full");
            base.Write(path, bytes);
        }

        public override byte[] Read(string path, int limit) =>
            ReadBackDifferent && path.EndsWith(".tmp", StringComparison.Ordinal) ? [.. base.Read(path, limit), (byte)'x'] : base.Read(path, limit);

        public override void Replace(string from, string to)
        {
            if (FailReplace) throw new UnauthorizedAccessException("the file is read-only");
            base.Replace(from, to);
        }

        public override void Delete(string path)
        {
            if (FailDelete) throw new IOException("the temporary file is held");
            base.Delete(path);
        }
    }

    public static TheoryData<string, string> Faults => new()
    {
        { "write", "could not be saved: the disk is full" },
        { "read-back", "could not be saved: the new settings did not read back as they were written" },
        { "replace", "could not be saved: the file is read-only" },
    };

    [Theory]
    [MemberData(nameof(Faults))]
    public void A_save_that_fails_at_any_step_leaves_the_old_file_the_current_settings_and_the_listeners_as_they_were(string fault, string reason)
    {
        using var scratch = new Scratch();
        var files = new Faulty();
        var store = new PanelSettingsStore(scratch.Settings, files);
        var before = new PanelSettings { Theme = ThemeChoice.Dark, ActivityPaneHeight = 300 };
        Assert.True(store.Save(before).Succeeded);
        var onDisk = File.ReadAllBytes(scratch.Settings);
        var told = new List<PanelSettings>();
        store.Changed += told.Add;

        files.FailWrite = fault == "write";
        files.ReadBackDifferent = fault == "read-back";
        files.FailReplace = fault == "replace";
        var outcome = store.Save(Chosen);

        Assert.False(outcome.Succeeded);
        Assert.Equal($"{scratch.Settings} {reason}", outcome.Reason);                     // the file named by its whole path
        Assert.Equal(onDisk, File.ReadAllBytes(scratch.Settings)); // the old file, byte for byte
        Assert.Equal(["panel.json"], Directory.GetFiles(scratch.Path).Select(Path.GetFileName)); // no temporary file left
        Assert.Equal(before, store.Current);
        Assert.Equal(SettingsState.Loaded, store.State);
        Assert.Empty(told);

        // With the fault gone the same save goes through.
        files.FailWrite = files.ReadBackDifferent = files.FailReplace = false;
        Assert.True(store.Save(Chosen).Succeeded);
        Assert.Equal([Chosen], told);
        Assert.Equal(Chosen, new PanelSettingsStore(scratch.Settings).Current);
    }

    [Fact]
    public void A_save_into_a_folder_that_cant_be_made_fails_with_the_reason_and_throws_nothing()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.File("data"), "a file where the data folder should be");
        var store = new PanelSettingsStore(Path.Combine(scratch.File("data"), "panel.json"));
        Assert.Equal(SettingsState.Defaults, store.State);

        var outcome = store.Save(Chosen);

        Assert.False(outcome.Succeeded);
        Assert.StartsWith(Path.Combine(scratch.File("data"), "panel.json") + " could not be saved: ", outcome.Reason);
        Assert.Equal(PanelSettings.Defaults, store.Current);
    }

    [Fact]
    public void A_temporary_file_that_cant_be_removed_after_a_failed_save_does_not_turn_the_failure_into_an_exception()
    {
        using var scratch = new Scratch();
        var files = new Faulty { FailReplace = true, FailDelete = true };
        var store = new PanelSettingsStore(scratch.Settings, files);

        var outcome = store.Save(Chosen);

        Assert.False(outcome.Succeeded);
        files.FailReplace = files.FailDelete = false;
        Assert.True(store.Save(Chosen).Succeeded); // the left-over temporary file is written over
        Assert.Equal(["panel.json"], Directory.GetFiles(scratch.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void The_notification_and_tray_settings_are_saved_and_read_back_and_a_file_from_before_them_takes_their_defaults()
    {
        // D02 T02 §7: memory pressure notifications, on unless turned off, and whether the tray notice was said.
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Settings, "{\"schema_version\": 1, \"theme\": \"Dark\"}"); // written before either setting existed
        var store = new PanelSettingsStore(scratch.Settings);
        Assert.Equal((true, false), (store.Current.NotifyMemoryPressure, store.Current.TrayNoticeSaid));
        Assert.Equal((true, false), (PanelSettings.Defaults.NotifyMemoryPressure, PanelSettings.Defaults.TrayNoticeSaid));

        Assert.True(store.Update(current => current with { NotifyMemoryPressure = false }).Succeeded);
        Assert.True(store.Update(current => current with { TrayNoticeSaid = true }).Succeeded);

        var read = new PanelSettingsStore(scratch.Settings).Current;
        Assert.Equal((false, true, ThemeChoice.Dark), (read.NotifyMemoryPressure, read.TrayNoticeSaid, read.Theme)); // and the theme saved before is still there
        var text = File.ReadAllText(scratch.Settings);
        Assert.Contains("\"notify_memory_pressure\": false", text);
        Assert.Contains("\"tray_notice_said\": true", text);
    }

    // ---- the current settings ----

    [Fact]
    public void An_update_changes_one_setting_of_the_latest_stored_ones_and_tells_its_listeners_once()
    {
        using var scratch = new Scratch();
        var store = new PanelSettingsStore(scratch.Settings);
        var told = new List<PanelSettings>();
        store.Changed += told.Add;

        Assert.True(store.Update(current => current with { Theme = ThemeChoice.Dark }).Succeeded);
        Assert.Equal(new PanelSettings { Theme = ThemeChoice.Dark }, Assert.Single(told));
        Assert.Equal(ThemeChoice.Dark, new PanelSettingsStore(scratch.Settings).Current.Theme);

        // Two parts of the panel, each changing its own setting: neither undoes the other's.
        Assert.True(store.Update(current => current with { ActivityPaneHeight = 250 }).Succeeded);
        Assert.True(store.Update(current => current with { Theme = ThemeChoice.Light }).Succeeded);
        var onDisk = new PanelSettingsStore(scratch.Settings).Current;
        Assert.Equal((ThemeChoice.Light, 250), (onDisk.Theme, onDisk.ActivityPaneHeight));
        Assert.Equal(3, told.Count);

        // The same settings again are already on disk: nothing is written and nobody is told.
        var written = File.GetLastWriteTimeUtc(scratch.Settings);
        Assert.True(store.Update(current => current).Succeeded);
        Assert.Equal(3, told.Count);
        Assert.Equal(written, File.GetLastWriteTimeUtc(scratch.Settings));
    }

    [Fact]
    public void An_update_whose_save_fails_leaves_the_current_settings_and_raises_nothing()
    {
        using var scratch = new Scratch();
        var files = new Faulty();
        var store = new PanelSettingsStore(scratch.Settings, files);
        Assert.True(store.Update(current => current with { Theme = ThemeChoice.Dark }).Succeeded);
        var told = new List<PanelSettings>();
        store.Changed += told.Add;

        files.FailWrite = true;
        var outcome = store.Update(current => current with { Theme = ThemeChoice.Light });

        Assert.False(outcome.Succeeded);
        Assert.Equal(ThemeChoice.Dark, store.Current.Theme);
        Assert.Empty(told);
    }
}
