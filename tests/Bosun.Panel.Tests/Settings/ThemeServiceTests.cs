using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Theme;
using Microsoft.Win32;
using Xunit;

namespace Bosun.Panel.Tests.Settings;

/// <summary>
/// The live theme (D02 T02 §5, design/SPEC.md §1): a choice replaces the one generated dictionary, a control that is
/// already drawn repaints, and Follow Windows follows a change that arrives from another thread.
/// </summary>
public class ThemeServiceTests
{
    private static Color Generated(string theme, string key)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"));
        return ((SolidColorBrush)((ResourceDictionary)XamlReader.Load(stream))[key]).Color;
    }

    private static Color? Paint(Brush? brush) => (brush as SolidColorBrush)?.Color;

    [Fact]
    public void A_choice_replaces_the_generated_theme_and_a_control_already_drawn_repaints_with_the_styles_untouched()
    {
        var (dark, light) = (Generated("Dark", "B.Base"), Generated("Light", "B.Base"));
        Assert.NotEqual(dark, light);
        Border drawn = null!;
        PageRenderer.InPage("Dark", () =>
        {
            drawn = new Border();
            drawn.SetResourceReference(Border.BackgroundProperty, "B.Base");
            return drawn;
        }, page =>
        {
            var merged = page.Resources.MergedDictionaries; // the theme, then the control styles, as App.xaml has them
            var styles = merged[1];
            var windows = new FakeWindowsTheme { IsLight = true };
            var theme = new ThemeService(merged, windows, page.Dispatcher); // loading the compiled themes, as the app does
            var effective = new List<string>();
            theme.EffectiveChanged += () => effective.Add(theme.Effective);
            Assert.Equal(dark, Paint(drawn.Background));

            theme.Apply(ThemeChoice.Light);
            PageRenderer.Settle(page);
            Assert.Equal(light, Paint(drawn.Background)); // the control that was already on screen
            Assert.Equal(ThemeService.Light, theme.Effective);

            theme.Apply(ThemeChoice.Dark);
            PageRenderer.Settle(page);
            Assert.Equal(dark, Paint(drawn.Background));

            theme.Apply(ThemeChoice.FollowWindows); // Windows is light
            PageRenderer.Settle(page);
            Assert.Equal(light, Paint(drawn.Background));

            theme.Apply(ThemeChoice.FollowWindows); // the same again changes nothing
            Assert.Equal(["Light", "Dark", "Light"], effective);
            Assert.Equal(2, merged.Count);
            Assert.Same(styles, merged[1]); // the styles were never reloaded, and still come after the theme
            Assert.True(merged[0].Contains("C.Base"));
            return true;
        });
    }

    [Fact]
    public void Following_windows_a_change_from_another_thread_is_applied_on_the_ui_thread_and_a_fixed_choice_ignores_it()
    {
        var (dark, light) = (Generated("Dark", "B.Base"), Generated("Light", "B.Base"));
        Border drawn = null!;
        PageRenderer.InPage("Dark", () =>
        {
            drawn = new Border();
            drawn.SetResourceReference(Border.BackgroundProperty, "B.Base");
            return drawn;
        }, page =>
        {
            var windows = new FakeWindowsTheme { IsLight = false };
            var theme = new ThemeService(page.Resources.MergedDictionaries, windows, page.Dispatcher);
            var swappedOn = new List<int>();
            theme.EffectiveChanged += () => swappedOn.Add(Environment.CurrentManagedThreadId);
            theme.Apply(ThemeChoice.FollowWindows);
            Assert.Equal(dark, Paint(drawn.Background));

            // Windows says so on a thread of its own.
            var other = Task.Run(() =>
            {
                windows.Flip();
                return Environment.CurrentManagedThreadId;
            });
            Assert.True(StartupSupport.Pump(() => other.IsCompleted && theme.Effective == ThemeService.Light), "the change was never followed");
            PageRenderer.Settle(page);
            Assert.NotEqual(Environment.CurrentManagedThreadId, other.Result);
            Assert.Equal([Environment.CurrentManagedThreadId], swappedOn); // swapped here, on the UI thread
            Assert.Equal(light, Paint(drawn.Background));

            // A fixed choice does not follow. Windows is light here and the panel is told to be dark: Windows going
            // dark and then light again must leave it dark both times (round 3: one flip, to dark, proved nothing,
            // since following Windows would have given dark too).
            theme.Apply(ThemeChoice.Dark);
            PageRenderer.Settle(page);
            Assert.True(windows.IsLight);
            var swapsSoFar = swappedOn.Count; // following to light, then the choice of dark
            foreach (var windowsIsLight in new[] { false, true })
            {
                var again = Task.Run(windows.Flip);
                StartupSupport.Pump(() => false, TimeSpan.FromMilliseconds(200));
                Assert.True(again.IsCompleted);
                Assert.Equal(windowsIsLight, windows.IsLight);
                Assert.Equal(ThemeService.Dark, theme.Effective);
                Assert.Equal(dark, Paint(drawn.Background));
            }
            Assert.Equal(swapsSoFar, swappedOn.Count); // and nothing was swapped for either

            // Back to following: it takes what Windows is now.
            theme.Apply(ThemeChoice.FollowWindows);
            PageRenderer.Settle(page);
            Assert.Equal(windows.IsLight ? light : dark, Paint(drawn.Background));
            return true;
        });
    }

    // ---- what Windows says ----

    [Fact]
    public void Windows_is_light_for_1_and_for_a_missing_value_and_dark_for_0()
    {
        Assert.True(WindowsTheme.Light(1));
        Assert.True(WindowsTheme.Light(null));
        Assert.False(WindowsTheme.Light(0));

        int? value = 0;
        Assert.False(new WindowsTheme(() => value).IsLight);
        value = null;
        Assert.True(new WindowsTheme(() => value).IsLight);
    }

    [Fact]
    public void A_general_preference_change_is_passed_on_only_when_the_theme_really_changed()
    {
        int? value = 1;
        var windows = new WindowsTheme(() => value);
        var changes = 0;
        windows.Changed += () => changes++;

        windows.PreferenceChanged(UserPreferenceCategory.General); // something general changed, not the theme
        Assert.Equal(0, changes);

        value = 0;
        windows.PreferenceChanged(UserPreferenceCategory.Color); // not the category the theme is in
        Assert.Equal(0, changes);
        Assert.True(windows.IsLight);

        windows.PreferenceChanged(UserPreferenceCategory.General);
        Assert.Equal(1, changes);
        Assert.False(windows.IsLight);

        windows.PreferenceChanged(UserPreferenceCategory.General);
        Assert.Equal(1, changes);

        windows.Listen();
        windows.Listen();
        windows.Dispose(); // the static event is let go
        windows.Dispose();
    }

    // ---- the registry, on keys of the tests' own ----

    /// <summary>A key under HKEY_CURRENT_USER\Software\Bosun\Tests that the test makes and removes.</summary>
    private sealed class ScratchKey : IDisposable
    {
        private const string Parent = @"Software\Bosun\Tests";

        public string Path { get; } = Parent + @"\" + Guid.NewGuid().ToString("N");

        public RegistryKey Make(string sub = "")
        {
            return Registry.CurrentUser.CreateSubKey(sub.Length == 0 ? Path : Path + @"\" + sub, writable: true);
        }

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(Path, throwOnMissingSubKey: false);
            using var parent = Registry.CurrentUser.OpenSubKey(Parent);
            var empty = parent is not null && parent.SubKeyCount == 0 && parent.ValueCount == 0;
            parent?.Dispose();
            if (empty) Registry.CurrentUser.DeleteSubKey(Parent, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void The_windows_theme_is_read_from_the_registry_value_and_a_missing_key_or_value_is_null()
    {
        using var scratch = new ScratchKey();
        Assert.Null(WindowsTheme.ReadFrom(scratch.Path)); // no key at all

        using (var key = scratch.Make())
        {
            Assert.Null(WindowsTheme.ReadFrom(scratch.Path)); // a key with no value
            key.SetValue(WindowsTheme.Value, 0, RegistryValueKind.DWord);
            Assert.Equal(0, WindowsTheme.ReadFrom(scratch.Path));
            key.SetValue(WindowsTheme.Value, 1, RegistryValueKind.DWord);
            Assert.Equal(1, WindowsTheme.ReadFrom(scratch.Path));
            key.SetValue(WindowsTheme.Value, "dark", RegistryValueKind.String); // not a number: as missing
            Assert.Null(WindowsTheme.ReadFrom(scratch.Path));
        }
    }

    [Fact]
    public void The_real_run_key_writes_reads_and_removes_a_value_and_reads_windows_start_up_record()
    {
        using var scratch = new ScratchKey();
        var run = scratch.Path + @"\Run";
        var approved = scratch.Path + @"\StartupApproved";
        var key = new RegistryRunKey(run, approved); // the real code, on keys of the test's own: the real Run key is untouched
        var entry = new StartupEntry(key, @"C:\Program Files\Bosun\Bosun.Panel.exe");

        Assert.Null(key.Read(StartupEntry.Name)); // no key yet
        Assert.Equal(Bosun.Core.Optimise.ApprovalState.NoRecord, key.Approval(StartupEntry.Name).State);
        key.Delete(StartupEntry.Name); // nothing to delete is not a fault
        Assert.Equal(StartupState.Off, entry.Read().State);

        Assert.True(entry.Set(true).Succeeded);
        Assert.Equal("\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray", key.Read(StartupEntry.Name));
        using (var raw = Registry.CurrentUser.OpenSubKey(run))
            Assert.Equal(RegistryValueKind.String, raw!.GetValueKind(StartupEntry.Name));
        Assert.Equal(StartupState.On, entry.Read().State);

        // As Windows writes its record: twelve bytes, the first 02 while on and 03 once the person turned it off.
        using (var record = scratch.Make("StartupApproved"))
        {
            record.SetValue(StartupEntry.Name, new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.Equal(Bosun.Core.Optimise.ApprovalState.TurnedOffByWindows, key.Approval(StartupEntry.Name).State);
            Assert.Equal(StartupState.DisabledByWindows, entry.Read().State);
            record.SetValue(StartupEntry.Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
            Assert.Equal(Bosun.Core.Optimise.ApprovalState.On, key.Approval(StartupEntry.Name).State);
            Assert.Equal(StartupState.On, entry.Read().State);

            // Round 2 (D05 T02 §1): a lone 02 is not Windows' record, so Settings says it can't read it, as
            // Diagnostics does, never on.
            record.SetValue(StartupEntry.Name, new byte[] { 2 }, RegistryValueKind.Binary);
            Assert.Equal(Bosun.Core.Optimise.ApprovalState.Unreadable, key.Approval(StartupEntry.Name).State);
            Assert.Equal(StartupState.Unavailable, entry.Read().State);
            record.SetValue(StartupEntry.Name, new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        Assert.True(entry.Set(false).Succeeded);
        Assert.Null(key.Read(StartupEntry.Name));
        Assert.Equal(StartupState.Off, entry.Read().State);

        // And the defaults are the real Run key and Windows' own record, which no test writes to.
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryRunKey.Run);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", RegistryRunKey.Approved_);
    }
}
