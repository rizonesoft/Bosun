using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using SystemCommands = System.Windows.SystemCommands;
using Bosun.Panel.Theme;
using Microsoft.Win32;

namespace Bosun.Panel.Shell;

/// <summary>What the backdrop asks of Windows: the build, the person's transparency setting and the window attributes.</summary>
public interface IBackdropSystem
{
    int Build { get; }

    /// <summary>Whether transparency effects are on (Settings, Personalisation, Colours).</summary>
    bool Transparency { get; }

    /// <summary><c>DwmSetWindowAttribute</c> with a 32-bit value: its HRESULT.</summary>
    int SetAttribute(IntPtr window, int attribute, int value);

    /// <summary><c>DwmGetWindowAttribute</c> with a 32-bit value: its HRESULT.</summary>
    int GetAttribute(IntPtr window, int attribute, out int value);

    /// <summary>
    /// Gives the window its system menu style (<c>WS_SYSMENU</c>) or takes it away. With the frame extended over the
    /// window for Mica, Windows draws its own caption buttons under the panel's while the window has it (found on the
    /// first capture, 2026-11-16: every caption mark drawn twice, and <c>SetWindowThemeAttribute</c>'s
    /// <c>WTNCA_NOSYSMENU</c> did not stop it). Its HRESULT.
    /// </summary>
    int SystemMenuStyle(IntPtr window, bool on);
}

/// <summary>Windows itself: the running build, the person's setting in the registry, and DWM.</summary>
public sealed class WindowsBackdropSystem : IBackdropSystem
{
    public const string PersonalizeKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Func<object?> _readTransparency;

    /// <param name="readTransparency">Reads <c>EnableTransparency</c>; the registry by default. A test's seam.</param>
    public WindowsBackdropSystem(Func<object?>? readTransparency = null) =>
        _readTransparency = readTransparency ?? (() => Registry.GetValue(PersonalizeKey, "EnableTransparency", null));

    /// <summary>The running build (the panel's manifest names Windows 10 and 11, so this is the real one).</summary>
    public int Build => Environment.OSVersion.Version.Build;

    public bool Transparency
    {
        get
        {
            try
            {
                return TransparencyOf(_readTransparency());
            }
            catch (Exception e) when (e is SecurityException or IOException or UnauthorizedAccessException)
            {
                // The value can't be read, so the setting is Windows' own default, which is on: the policy still has
                // its other rules, and the person's change, once readable, is applied at the next settings change.
                return true;
            }
        }
    }

    /// <summary>A missing value, or one that is not a number, is on, as Windows reads it; only 0 is off.</summary>
    public static bool TransparencyOf(object? value) => value is not int on || on != 0;

    public int SetAttribute(IntPtr window, int attribute, int value) => DwmSetWindowAttribute(window, attribute, ref value, sizeof(int));

    public int GetAttribute(IntPtr window, int attribute, out int value)
    {
        value = 0;
        return DwmGetWindowAttribute(window, attribute, out value, sizeof(int));
    }

    private const int GWL_STYLE = -16;
    private const long WS_SYSMENU = 0x00080000L;

    public int SystemMenuStyle(IntPtr window, bool on)
    {
        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLongPtr(window, GWL_STYLE).ToInt64();
        if (style == 0 && Marshal.GetLastPInvokeError() is var read and not 0) return HResult(read);
        var wanted = on ? style | WS_SYSMENU : style & ~WS_SYSMENU;
        if (wanted == style) return 0;
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(window, GWL_STYLE, new IntPtr(wanted)) == IntPtr.Zero && Marshal.GetLastPInvokeError() is var written and not 0)
            return HResult(written);
        return 0;
    }

    private static int HResult(int win32) => unchecked((int)(0x80070000u | (uint)(win32 & 0xFFFF)));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
}

/// <summary>
/// The window's backdrop (D02 T03 §2, design/SPEC.md §1 and §19): Mica under the title bar and rail where
/// <see cref="BackdropPolicy"/> allows it, set through <c>DWMWA_SYSTEMBACKDROP_TYPE</c> with the window's frame extended
/// (through its <see cref="WindowChrome"/>, which owns that) and the window, the shell and the composition target clear;
/// otherwise solid <c>Base</c>, the same layout. The page's sheet is <c>Base</c> in both. The policy is applied again,
/// live, when Windows says a setting changed, when the contrast theme goes on or off (on any thread, taken to the
/// window's), and when the panel's theme changes. A DWM call that fails is one WARN line in the Activity pane, and the
/// window stays solid. Everything it listens to is let go when the window closes.
/// </summary>
public sealed class Backdrop : IDisposable
{
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_NONE = 1;
    public const int DWMSBT_MAINWINDOW = 2;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int WM_THEMECHANGED = 0x031A;

    private readonly Window _window;
    private readonly Control _shell;
    private readonly ThemeService _theme;
    private readonly IHighContrast? _contrast;
    private readonly ActivityLog _log;
    private readonly IBackdropSystem _system;
    private HwndSource? _source;
    private string? _said; // the failure last written, so a repeat of it is not written again
    private bool _disposed;

    private Backdrop(Window window, Control shell, ThemeService theme, IHighContrast? contrast, ActivityLog log, IBackdropSystem system) =>
        (_window, _shell, _theme, _contrast, _log, _system) = (window, shell, theme, contrast, log, system);

    /// <summary>What the chrome sits on now.</summary>
    public BackdropKind Kind { get; private set; } = BackdropKind.Solid;

    /// <summary>
    /// Attaches the backdrop to <paramref name="window"/>, whose content is <paramref name="shell"/>: now, when its
    /// handle exists, or as it is made (<see cref="Window.SourceInitialized"/>), whether it is then shown or not.
    /// </summary>
    public static Backdrop Attach(Window window, Control shell, ThemeService theme, IHighContrast? contrast, ActivityLog log, IBackdropSystem? system = null)
    {
        var backdrop = new Backdrop(window, shell, theme, contrast, log, system ?? new WindowsBackdropSystem());
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero) backdrop.Hook(handle);
        else window.SourceInitialized += backdrop.OnSourceInitialized;
        theme.EffectiveChanged += backdrop.Apply;
        if (contrast is not null) contrast.Changed += backdrop.OnAnyThread;
        SystemParameters.StaticPropertyChanged += backdrop.OnParameterChanged;
        window.Closed += backdrop.OnClosed;
        window.PreviewKeyDown += backdrop.OnKey;
        return backdrop;
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => Hook(new WindowInteropHelper(_window).Handle);

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void Hook(IntPtr handle)
    {
        if (_disposed || _source is not null) return;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(OnMessage);
        Apply();
    }

    private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Transparency effects turned on or off, and Windows' theme changing, both come as these.
        if (message is WM_SETTINGCHANGE or WM_THEMECHANGED) Apply();
        return IntPtr.Zero;
    }

    /// <summary>
    /// Alt+Space, which Windows answers with the system menu only while the window has its style (and, without it, never
    /// turns into the menu's own request): in Mica the menu is opened here, under the title bar's left end, so the
    /// keyboard loses nothing.
    /// </summary>
    private void OnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // A system key of Space is Alt+Space and nothing else (F10 makes F10 the system key).
        if (Kind != BackdropKind.Mica || e.Key != System.Windows.Input.Key.System || e.SystemKey != System.Windows.Input.Key.Space) return;
        var scale = VisualTreeHelper.GetDpi(_window);
        var corner = _window.PointToScreen(new Point(8, 44));
        SystemCommands.ShowSystemMenu(_window, new Point(corner.X / scale.DpiScaleX, corner.Y / scale.DpiScaleY));
        e.Handled = true;
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) OnAnyThread();
    }

    /// <summary>A change said on any thread, applied on the window's.</summary>
    private void OnAnyThread()
    {
        if (_disposed) return;
        if (_window.Dispatcher.CheckAccess()) Apply();
        else _window.Dispatcher.BeginInvoke(Apply);
    }

    /// <summary>Chooses and applies the backdrop now. On the window's thread; nothing before the window has a handle.</summary>
    public void Apply()
    {
        if (_disposed || _source is null) return;
        var handle = _source.Handle;
        var wanted = BackdropPolicy.Choose(_system.Build, _system.Transparency, _theme.IsHighContrast || _contrast?.On == true);
        if (wanted == BackdropKind.Mica)
        {
            // Dark or light Mica, as the panel's theme is; then Mica itself. A refusal of either leaves it solid.
            var dark = _theme.Effective == ThemeService.Light ? 0 : 1;
            var failed = Failure("DWMWA_USE_IMMERSIVE_DARK_MODE", _system.SetAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, dark))
                ?? Failure("DWMWA_SYSTEMBACKDROP_TYPE", _system.SetAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_MAINWINDOW));
            if (failed is not null)
            {
                if (failed != _said) _log.Warn($"The window's Mica backdrop couldn't be set ({failed}), so the window is drawn solid.");
                _said = failed;
                wanted = BackdropKind.Solid;
            }
            else
            {
                _said = null;
            }
        }
        if (wanted == BackdropKind.Solid && Kind == BackdropKind.Mica)
            _system.SetAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE); // taken back; solid paints over it regardless
        // In Mica the window goes without its system menu style, so Windows draws no caption buttons of its own in the
        // extended frame under the panel's; solid, it has it again. A refusal leaves the window solid.
        if (wanted == BackdropKind.Mica && Failure("WS_SYSMENU", _system.SystemMenuStyle(handle, on: false)) is { } refused)
        {
            if (refused != _said) _log.Warn($"The window's Mica backdrop couldn't be set ({refused}), so the window is drawn solid.");
            _said = refused;
            wanted = BackdropKind.Solid;
            _system.SetAttribute(handle, DWMWA_SYSTEMBACKDROP_TYPE, DWMSBT_NONE);
        }
        if (wanted == BackdropKind.Solid) _system.SystemMenuStyle(handle, on: true);
        Paint(_window, _shell, wanted, _source);
        Kind = wanted;
    }

    private static string? Failure(string what, int hresult) => hresult >= 0 ? null : $"{what} failed with 0x{hresult:X8}";

    /// <summary>
    /// Paints the chrome for <paramref name="kind"/>: in Mica the window, the shell and the composition target are clear
    /// and the window's frame is extended over all of it, so Windows' backdrop shows under the title bar and rail; in
    /// solid, both are the theme's <c>Base</c> and the frame is not extended. The sheet is the shell's own, <c>Base</c>
    /// in both. Public for the renders, which draw the shell without a window.
    /// </summary>
    public static void Paint(Window? window, Control shell, BackdropKind kind, HwndSource? source = null)
    {
        if (kind == BackdropKind.Mica)
        {
            if (window is not null) window.Background = Brushes.Transparent;
            shell.Background = Brushes.Transparent;
            if (source?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        }
        else
        {
            window?.SetResourceReference(Control.BackgroundProperty, "B.Base");
            shell.SetResourceReference(Control.BackgroundProperty, "B.Base");
        }
        if (window is not null && WindowChrome.GetWindowChrome(window) is { } chrome)
            chrome.GlassFrameThickness = kind == BackdropKind.Mica ? new Thickness(-1) : new Thickness(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source?.RemoveHook(OnMessage);
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _window.PreviewKeyDown -= OnKey;
        _theme.EffectiveChanged -= Apply;
        if (_contrast is not null) _contrast.Changed -= OnAnyThread;
        // A static event: left subscribed, it would keep this object, and its window, for the process's life.
        SystemParameters.StaticPropertyChanged -= OnParameterChanged;
    }
}
