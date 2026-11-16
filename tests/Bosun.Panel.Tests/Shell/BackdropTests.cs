using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Shell;
using Bosun.Panel.Tests.Render;
using Bosun.Panel.Tests.Settings;
using Bosun.Panel.Theme;
using Xunit;

namespace Bosun.Panel.Tests.Shell;

/// <summary>
/// The window's backdrop (D02 T03 §2): the policy's table, the person's transparency setting as Windows reads it, and
/// a real window's backdrop chosen and changed live through a fake Windows: Mica with its dark or light tint following
/// the theme, solid with transparency off, in a contrast theme (said on another thread) or on an older build, back to
/// Mica when they allow it again, a refused DWM call one WARN line and a solid window, and nothing heard once the
/// window has closed.
/// </summary>
public class BackdropTests
{
    // ---- the policy ----

    [Theory]
    [InlineData(19045, true, false, BackdropKind.Solid)]  // Windows 10
    [InlineData(22000, true, false, BackdropKind.Solid)]  // Windows 11 21H2, before the system backdrops
    [InlineData(22620, true, false, BackdropKind.Solid)]
    [InlineData(22621, true, false, BackdropKind.Mica)]   // 22H2, the first with them
    [InlineData(26100, true, false, BackdropKind.Mica)]
    [InlineData(26100, false, false, BackdropKind.Solid)] // transparency effects off
    [InlineData(26100, true, true, BackdropKind.Solid)]   // a contrast theme
    [InlineData(26100, false, true, BackdropKind.Solid)]
    [InlineData(22000, false, true, BackdropKind.Solid)]
    public void Mica_only_on_22621_or_later_with_transparency_on_and_no_contrast_theme(int build, bool transparency, bool contrast, BackdropKind kind) =>
        Assert.Equal(kind, BackdropPolicy.Choose(build, transparency, contrast));

    [Theory]
    [InlineData(null, true)]   // never set: Windows reads it as on
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData("0", true)]    // not a number: Windows takes only a DWORD 0 as off
    public void The_transparency_setting_reads_as_Windows_reads_it(object? stored, bool on)
    {
        Assert.Equal(on, WindowsBackdropSystem.TransparencyOf(stored));
        Assert.Equal(on, new WindowsBackdropSystem(() => stored).Transparency);
    }

    [Fact]
    public void A_setting_that_can_t_be_read_is_Windows_own_default_on() =>
        Assert.True(new WindowsBackdropSystem(() => throw new SecurityException("denied")).Transparency);

    // ---- a real window, through a fake Windows ----

    /// <summary>Windows as a test sets it: the build, the transparency setting, and every attribute set, one refusable.</summary>
    sealed class FakeSystem : IBackdropSystem
    {
        public int Build { get; set; } = 26100;
        public bool Transparency { get; set; } = true;
        public Dictionary<int, int> Attributes { get; } = [];
        public int Calls { get; private set; }
        public bool? SystemMenu { get; private set; }
        public int RefuseAttribute { get; set; } = -1;

        public int SetAttribute(IntPtr window, int attribute, int value)
        {
            Calls++;
            if (attribute == RefuseAttribute) return unchecked((int)0x80070005);
            Attributes[attribute] = value;
            return 0;
        }

        public int GetAttribute(IntPtr window, int attribute, out int value) => Attributes.TryGetValue(attribute, out value) ? 0 : unchecked((int)0x80070057);

        public int SystemMenuStyle(IntPtr window, bool on)
        {
            Calls++;
            SystemMenu = on;
            return 0;
        }
    }

    sealed class FakeContrast : IHighContrast
    {
        public bool On { get; private set; }

        public SystemPalette Palette => Helpers.ContrastPalettes.NightSky;

        public event Action? Changed;

        public void Set(bool on)
        {
            On = on;
            Changed?.Invoke();
        }
    }

    static ResourceDictionary Generated(string theme)
    {
        using var stream = File.OpenRead(TestPaths.GeneratedTheme(theme + ".xaml"));
        return (ResourceDictionary)XamlReader.Load(stream);
    }

    sealed record Bench(Window Window, UserControl Shell, ThemeService Theme, FakeContrast Contrast, FakeSystem System, Backdrop Backdrop, ActivityLog Log)
    {
        public IntPtr Handle => new WindowInteropHelper(Window).Handle;

        public int Warnings => Log.Entries.Count(e => e.Severity == LogSeverity.Warn);

        /// <summary>What Windows sends every window when a setting such as transparency effects changes.</summary>
        public void SettingChanged() => SendMessage(Handle, 0x001A, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>A window with the panel's chrome and theme, its handle made but never shown, with the backdrop attached as the application attaches it.</summary>
    static Bench Make(FakeSystem? system = null)
    {
        var window = new Window { Width = 400, Height = 300, ShowActivated = false };
        WindowChrome.SetWindowChrome(window, new WindowChrome { CaptionHeight = 44, GlassFrameThickness = new Thickness(0) });
        window.Resources.MergedDictionaries.Add(Generated("Dark"));
        window.Resources.MergedDictionaries.Add(PageRenderer.Controls());
        var shell = new UserControl();
        window.Content = shell;
        var contrast = new FakeContrast();
        var theme = new ThemeService(window.Resources.MergedDictionaries, new FakeWindowsTheme(), Dispatcher.CurrentDispatcher, Generated, contrast);
        theme.Apply(ThemeChoice.Dark);
        var log = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher));
        system ??= new FakeSystem();
        var backdrop = Backdrop.Attach(window, shell, theme, contrast, log, system); // before the handle, as the application does
        new WindowInteropHelper(window).EnsureHandle();
        return new Bench(window, shell, theme, contrast, system, backdrop, log);
    }

    static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    static bool Clear(Bench b) => b.Window.Background == Brushes.Transparent && b.Shell.Background == Brushes.Transparent &&
        WindowChrome.GetWindowChrome(b.Window).GlassFrameThickness == new Thickness(-1);

    static bool SolidBase(Bench b) =>
        b.Window.Background is SolidColorBrush w && b.Shell.Background is SolidColorBrush s &&
        w.Color == (Color)b.Window.FindResource("C.Base") && s.Color == w.Color &&
        WindowChrome.GetWindowChrome(b.Window).GlassFrameThickness == new Thickness(0);

    static void OnWindowThread(Action<Bench> test) => Sta.Run(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        var bench = Make();
        try
        {
            test(bench);
        }
        finally
        {
            bench.Window.Close();
        }
        return 0;
    });

    [Fact]
    public void Attached_as_the_handle_is_made_the_chrome_is_Mica_tinted_as_the_theme_and_follows_it_both_ways()
    {
        OnWindowThread(b =>
        {
            Assert.Equal(BackdropKind.Mica, b.Backdrop.Kind);
            Assert.Equal((Backdrop.DWMSBT_MAINWINDOW, 1, false), (b.System.Attributes[Backdrop.DWMWA_SYSTEMBACKDROP_TYPE], b.System.Attributes[Backdrop.DWMWA_USE_IMMERSIVE_DARK_MODE], b.System.SystemMenu));
            Assert.True(Clear(b), "the window, the shell and the frame are clear under Mica");

            b.Theme.Apply(ThemeChoice.Light);
            Assert.Equal(0, b.System.Attributes[Backdrop.DWMWA_USE_IMMERSIVE_DARK_MODE]); // light Mica
            b.Theme.Apply(ThemeChoice.Dark);
            Assert.Equal(1, b.System.Attributes[Backdrop.DWMWA_USE_IMMERSIVE_DARK_MODE]);
        });
    }

    [Fact]
    public void Transparency_turned_off_makes_it_solid_with_no_restart_and_on_again_Mica()
    {
        OnWindowThread(b =>
        {
            b.System.Transparency = false;
            b.SettingChanged();
            Assert.Equal(BackdropKind.Solid, b.Backdrop.Kind);
            Assert.Equal((Backdrop.DWMSBT_NONE, true), (b.System.Attributes[Backdrop.DWMWA_SYSTEMBACKDROP_TYPE], b.System.SystemMenu));
            Assert.True(SolidBase(b), "the window and the shell are the theme's Base, the frame not extended");

            b.System.Transparency = true;
            b.SettingChanged();
            Assert.Equal(BackdropKind.Mica, b.Backdrop.Kind);
            Assert.True(Clear(b));
        });
    }

    [Fact]
    public void A_contrast_theme_said_on_another_thread_makes_it_solid_and_off_again_Mica()
    {
        OnWindowThread(b =>
        {
            Task.Run(() => b.Contrast.Set(true)).Wait(); // as Windows says it: on a thread of its own
            Assert.True(StartupSupport.Pump(() => b.Backdrop.Kind == BackdropKind.Solid), "contrast on never made it solid");
            Assert.True(SolidBase(b));
            Task.Run(() => b.Contrast.Set(false)).Wait();
            Assert.True(StartupSupport.Pump(() => b.Backdrop.Kind == BackdropKind.Mica), "contrast off never made it Mica again");
            Assert.True(Clear(b));
        });
    }

    [Fact]
    public void A_build_before_22621_is_solid_and_never_asks_for_a_backdrop()
    {
        Sta.Run(() =>
        {
            var b = Make(new FakeSystem { Build = 22000 });
            Assert.Equal(BackdropKind.Solid, b.Backdrop.Kind);
            Assert.False(b.System.Attributes.ContainsKey(Backdrop.DWMWA_SYSTEMBACKDROP_TYPE));
            Assert.True(b.System.SystemMenu);
            Assert.True(SolidBase(b));
            b.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void A_refused_DWM_call_is_one_WARN_line_and_a_solid_window()
    {
        Sta.Run(() =>
        {
            var b = Make(new FakeSystem { RefuseAttribute = Backdrop.DWMWA_SYSTEMBACKDROP_TYPE });
            Assert.Equal(BackdropKind.Solid, b.Backdrop.Kind);
            Assert.True(SolidBase(b));
            b.SettingChanged(); // tried again, refused again: not said again
            Drain();
            var warn = Assert.Single(b.Log.Entries, e => e.Severity == LogSeverity.Warn);
            Assert.Contains("DWMWA_SYSTEMBACKDROP_TYPE failed with 0x80070005", warn.Message);
            b.Window.Close();
            return 0;
        });
    }

    [Fact]
    public void Once_the_window_has_closed_nothing_it_listened_to_reaches_it()
    {
        Sta.Run(() =>
        {
            var b = Make();
            b.Window.Close();
            var calls = b.System.Calls;
            b.Contrast.Set(true);
            b.Theme.Apply(ThemeChoice.Light);
            Drain();
            Assert.Equal(calls, b.System.Calls);
            return 0;
        });
    }

    // ---- the renders ----

    /// <summary>A rendered picture's pixel, as 0xAARRGGBB.</summary>
    static uint[] Pixels(byte[] png, out int width)
    {
        var frame = System.Windows.Media.Imaging.BitmapDecoder.Create(new MemoryStream(png), System.Windows.Media.Imaging.BitmapCreateOptions.None,
            System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        var pixels = new uint[width * converted.PixelHeight];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    /// <summary>Points, at the renders' 1360 x 860: the title bar's empty middle, the rail below its rows, the page's sheet.</summary>
    static readonly (int X, int Y) TitleBar = (700, 20), Rail = (100, 700), Sheet = (800, 300);

    [Theory]
    [MemberData(nameof(RenderThemes))]
    public void Solid_paints_Base_under_the_chrome_and_Mica_leaves_it_clear_with_the_sheet_solid(string theme)
    {
        // On a clear host (plan review: the renderer's own Base behind everything would pass with the fallback gone).
        // Solid is the catalog's shell.overview; drawn here on a clear host too, so its Base is the shell's own.
        var solidCase = RenderCatalog.Cases.Single(c => c.Name == "shell.overview") with { ClearHost = true };
        var solid = Pixels(PageRenderer.Render(solidCase, theme).Png, out var w);
        var mica = Pixels(PageRenderer.Render(RenderCatalog.Cases.Single(c => c.Name == "shell.backdrop-mica"), theme).Png, out _);
        uint At(uint[] p, (int X, int Y) point) => p[point.Y * w + point.X];

        var sheet = At(solid, Sheet);
        Assert.Equal(0xFFu, sheet >> 24);
        Assert.Equal(sheet, At(solid, TitleBar)); // the same Base under the title bar
        Assert.Equal(sheet, At(solid, Rail));     // and the rail
        if (theme != "HighContrast")
        {
            var c = (Color)Generated(theme)["C.Base"];
            Assert.Equal((uint)(0xFF000000 | (uint)c.R << 16 | (uint)c.G << 8 | c.B), sheet);
        }
        Assert.Equal(0u, At(mica, TitleBar) >> 24); // clear for Windows' backdrop
        Assert.Equal(0u, At(mica, Rail) >> 24);
        Assert.Equal(sheet, At(mica, Sheet));       // the page's sheet still solid Base
    }

    public static TheoryData<string> RenderThemes => [.. PageRenderer.Themes];

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
