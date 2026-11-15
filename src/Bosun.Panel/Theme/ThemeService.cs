using System.Windows;
using System.Windows.Threading;
using Bosun.Core;

namespace Bosun.Panel.Theme;

/// <summary>
/// The panel's theme (D02 T02 §5, design/SPEC.md §1: Follow Windows, Dark or Light, applied live). A theme is one of
/// the two generated dictionaries among the application's merged ones; applying a choice replaces that one
/// dictionary. Every style names the theme's keys with DynamicResource, so everything already on screen repaints and
/// nothing else is reloaded.
/// </summary>
public sealed class ThemeService
{
    public const string Dark = "Dark", Light = "Light";

    private readonly IList<ResourceDictionary> _merged;
    private readonly IWindowsTheme _windows;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string, ResourceDictionary> _load;
    private ThemeChoice _choice = ThemeChoice.Dark;
    private ResourceDictionary? _applied;

    /// <param name="merged">The merged dictionaries that hold the generated theme (the application's).</param>
    /// <param name="load">Loads a generated theme by name; the compiled one by default.</param>
    public ThemeService(IList<ResourceDictionary> merged, IWindowsTheme windows, Dispatcher dispatcher, Func<string, ResourceDictionary>? load = null)
    {
        _merged = merged;
        _windows = windows;
        _dispatcher = dispatcher;
        _load = load ?? (name => (ResourceDictionary)Application.LoadComponent(new Uri($"/Bosun.Panel;component/Theme/Generated/{name}.xaml", UriKind.Relative)));
        // The dictionary that holds the theme now: the one with the theme's colours in it.
        _applied = merged.FirstOrDefault(dictionary => dictionary.Contains("C.Base"));
        Effective = Dark;
        windows.Changed += OnWindowsChanged;
    }

    /// <summary>The theme in force, <see cref="Dark"/> or <see cref="Light"/>: what Follow Windows came to.</summary>
    public string Effective { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Effective"/> has changed.</summary>
    public event Action? EffectiveChanged;

    /// <summary>Applies a choice, now. On the UI thread.</summary>
    public void Apply(ThemeChoice choice)
    {
        _dispatcher.VerifyAccess();
        _choice = choice;
        var name = choice switch
        {
            ThemeChoice.Dark => Dark,
            ThemeChoice.Light => Light,
            _ => _windows.IsLight ? Light : Dark,
        };
        if (name == Effective && _applied is not null) return;

        var theme = _load(name);
        var at = _applied is null ? -1 : _merged.IndexOf(_applied);
        // In the old one's place, so what was merged after it (the control styles) still comes after it.
        if (at >= 0) _merged[at] = theme;
        else _merged.Insert(0, theme);
        _applied = theme;
        if (name == Effective) return;
        Effective = name;
        EffectiveChanged?.Invoke();
    }

    /// <summary>Windows' theme changed, on whatever thread Windows said so: followed on the UI thread, when the choice is to follow.</summary>
    private void OnWindowsChanged() => _dispatcher.BeginInvoke(() =>
    {
        if (_choice == ThemeChoice.FollowWindows) Apply(ThemeChoice.FollowWindows);
    });
}
