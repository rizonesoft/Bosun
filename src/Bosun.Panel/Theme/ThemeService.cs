using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Bosun.Core;
using Microsoft.Win32;

namespace Bosun.Panel.Theme;

/// <summary>Whether Windows draws in a contrast theme, its colours, and when either changes.</summary>
public interface IHighContrast
{
    bool On { get; }

    /// <summary>The contrast theme's colours, read now.</summary>
    SystemPalette Palette { get; }

    /// <summary>Raised when <see cref="On"/> or the colours change. On any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Windows' contrast themes (D02 T03 §10): on while <see cref="SystemParameters.HighContrast"/> is, which WPF says
/// changed through <see cref="SystemParameters.StaticPropertyChanged"/>, with the colours of the moment; a change of
/// colours alone (another contrast theme chosen) comes through <see cref="SystemEvents.UserPreferenceChanged"/>, on a
/// thread of its own.
/// </summary>
public sealed class WindowsHighContrast : IHighContrast, IDisposable
{
    private bool _listening;

    public bool On => SystemParameters.HighContrast;

    public SystemPalette Palette => SystemPalette.Current();

    public event Action? Changed;

    /// <summary>Starts listening for Windows' changes. <see cref="Dispose"/> stops it.</summary>
    public void Listen()
    {
        if (_listening) return;
        _listening = true;
        SystemParameters.StaticPropertyChanged += OnParameterChanged;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private void OnParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) Changed?.Invoke();
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.Accessibility) Changed?.Invoke();
    }

    public void Dispose()
    {
        if (!_listening) return;
        _listening = false;
        // Static events: left subscribed, they would keep this object for the process's life.
        SystemParameters.StaticPropertyChanged -= OnParameterChanged;
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}

/// <summary>
/// The panel's theme (D02 T02 §5, design/SPEC.md §1: Follow Windows, Dark or Light, applied live). A theme is one of
/// the two generated dictionaries among the application's merged ones; applying a choice replaces that one
/// dictionary. Every style names the theme's keys with DynamicResource, so everything already on screen repaints and
/// nothing else is reloaded.
///
/// While Windows draws in a contrast theme (D02 T03 §10), that wins over the person's choice, which is kept: the
/// generated Dark theme is in force for its measures and motion, with the contrast colours
/// (<see cref="HighContrastTheme"/>) merged right after it, and <see cref="IsHighContrast"/> is true for the
/// templates. When contrast is turned off, the person's choice is applied again; when its colours change, they are
/// built again. Every change Windows says, on whatever thread, is applied on the UI thread.
/// </summary>
public sealed class ThemeService
{
    public const string Dark = "Dark", Light = "Light", HighContrast = "HighContrast";

    private readonly IList<ResourceDictionary> _merged;
    private readonly IWindowsTheme _windows;
    private readonly IHighContrast? _contrast;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, ResourceDictionary> _load;
    private ThemeChoice _choice = ThemeChoice.Dark;
    private ResourceDictionary? _applied;
    private ResourceDictionary? _contrastColours;

    /// <param name="merged">The merged dictionaries that hold the generated theme (the application's).</param>
    /// <param name="contrast">Windows' contrast themes; none, and the panel never draws in one.</param>
    /// <param name="load">Loads a generated theme by name; the compiled one by default.</param>
    public ThemeService(IList<ResourceDictionary> merged, IWindowsTheme windows, Dispatcher dispatcher, Func<string, ResourceDictionary>? load = null,
        IHighContrast? contrast = null)
    {
        _merged = merged;
        _windows = windows;
        _contrast = contrast;
        _dispatcher = dispatcher;
        _load = load ?? (name => (ResourceDictionary)Application.LoadComponent(new Uri($"/Bosun.Panel;component/Theme/Generated/{name}.xaml", UriKind.Relative)));
        // The dictionary that holds the theme now: the one with the theme's colours in it.
        _applied = merged.FirstOrDefault(dictionary => dictionary.Contains("C.Base"));
        Effective = Dark;
        windows.Changed += OnWindowsChanged;
        if (contrast is not null) contrast.Changed += OnContrastChanged;
    }

    /// <summary>The theme in force: <see cref="Dark"/> or <see cref="Light"/>, what Follow Windows came to, or <see cref="HighContrast"/>.</summary>
    public string Effective { get; private set; }

    /// <summary>Whether the panel is drawn in Windows' contrast colours now, whatever the person chose.</summary>
    public bool IsHighContrast => Effective == HighContrast;

    /// <summary>The person's choice, kept while a contrast theme is in force.</summary>
    public ThemeChoice Choice => _choice;

    /// <summary>Raised on the UI thread when <see cref="Effective"/> has changed, or the contrast colours were built again.</summary>
    public event Action? EffectiveChanged;

    /// <summary>Applies a choice, now. On the UI thread.</summary>
    public void Apply(ThemeChoice choice)
    {
        _dispatcher.VerifyAccess();
        _choice = choice;
        Refresh(rebuildContrast: false);
    }

    private void Refresh(bool rebuildContrast)
    {
        var contrast = _contrast?.On == true;
        var name = contrast ? HighContrast : _choice switch
        {
            ThemeChoice.Dark => Dark,
            ThemeChoice.Light => Light,
            _ => _windows.IsLight ? Light : Dark,
        };
        if (name == Effective && _applied is not null && !(contrast && rebuildContrast)) return;

        var theme = _load(contrast ? Dark : name);
        var at = _applied is null ? -1 : _merged.IndexOf(_applied);
        // In the old one's place, so what was merged after it (the control styles) still comes after it.
        if (at >= 0) _merged[at] = theme;
        else _merged.Insert(0, theme);
        _applied = theme;

        if (_contrastColours is not null) _merged.Remove(_contrastColours);
        _contrastColours = null;
        if (contrast)
        {
            // Right after the generated theme: a later merged dictionary is looked in first, so its colours win, and
            // the control styles after it still find them.
            _contrastColours = HighContrastTheme.Build(_contrast!.Palette, theme);
            _merged.Insert(_merged.IndexOf(theme) + 1, _contrastColours);
        }

        var changed = name != Effective || contrast;
        Effective = name;
        if (changed) EffectiveChanged?.Invoke();
    }

    /// <summary>Windows' theme changed, on whatever thread Windows said so: followed on the UI thread, when the choice is to follow.</summary>
    private void OnWindowsChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_choice == ThemeChoice.FollowWindows) Refresh(rebuildContrast: false);
    });

    /// <summary>Contrast was turned on or off, or its colours changed: on the UI thread, the colours are built again.</summary>
    private void OnContrastChanged() => _dispatcher.BeginInvoke(() => Refresh(rebuildContrast: true));
}
