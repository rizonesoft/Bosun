using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;

namespace Bosun.Panel.Theme;

/// <summary>Whether Windows' animation effects are on, and when that changes.</summary>
public interface IWindowsAnimation
{
    bool On { get; }

    /// <summary>Raised when <see cref="On"/> has changed.</summary>
    event Action? Changed;
}

/// <summary>
/// Windows' animation effects (Settings, Accessibility, Visual effects, Animation effects), as WPF reads them:
/// <see cref="SystemParameters.ClientAreaAnimation"/>. WPF says the setting changed through
/// <see cref="SystemParameters.StaticPropertyChanged"/>, on the thread that heard Windows say so (the UI thread);
/// <see cref="Changed"/> is raised only when the value is different.
/// </summary>
public sealed class WindowsAnimation : IWindowsAnimation, IDisposable
{
    private readonly Func<bool> _read;
    private bool _on;
    private bool _listening;

    /// <param name="read">The setting; WPF's own by default.</param>
    public WindowsAnimation(Func<bool>? read = null)
    {
        _read = read ?? (() => SystemParameters.ClientAreaAnimation);
        _on = _read();
    }

    public bool On => _on;

    /// <summary>Whether it is listening for the setting's changes.</summary>
    public bool Listening => _listening;

    public event Action? Changed;

    /// <summary>Starts listening for the setting's changes. <see cref="Dispose"/> stops it.</summary>
    public void Listen()
    {
        if (_listening) return;
        _listening = true;
        SystemParameters.StaticPropertyChanged += OnStaticPropertyChanged;
    }

    private void OnStaticPropertyChanged(object? sender, PropertyChangedEventArgs e) => PropertyChanged(e.PropertyName);

    /// <summary>
    /// WPF said one of its system parameters changed. Only the animation setting is read again, and a change passed on.
    /// Internal so a test can say it without changing Windows.
    /// </summary>
    internal void PropertyChanged(string? property)
    {
        if (property != nameof(SystemParameters.ClientAreaAnimation)) return;
        var on = _read();
        if (on == _on) return;
        _on = on;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (!_listening) return;
        _listening = false;
        // A static event: left subscribed, it would keep this object for the process's life.
        SystemParameters.StaticPropertyChanged -= OnStaticPropertyChanged;
    }
}

/// <summary>
/// The one gate every animation in the panel goes through (D02 T03 §1, design/SPEC.md §19): the design's durations,
/// from the generated theme's <c>Motion.*</c> resources, while Windows' animation effects are on, and zero for each
/// while they are off, so every change applies at once; <see cref="Animates"/> is then false, so a repeating pulse is
/// not started at all. Animations are started from code through it: a storyboard in a style is frozen and can't take
/// a <c>DynamicResource</c>, and a duration copied into one would not hear the setting change.
/// </summary>
public sealed class Motion : IDisposable
{
    public const string FastKey = "Motion.Fast", NormalKey = "Motion.Normal", SlowKey = "Motion.Slow", PulseKey = "Motion.Pulse", EaseKey = "Motion.Ease";

    /// <summary>Where the application keeps its one gate, for the views that animate to find (D02 T03 §7).</summary>
    public const string GateKey = "Motion.Gate";

    private static readonly Duration Instant = new(TimeSpan.Zero);
    private readonly ResourceDictionary _resources;
    private readonly IWindowsAnimation _windows;
    private ResourceDictionary? _installedIn;
    private WindowsAnimation? _owned;

    /// <summary>
    /// The application's gate (D02 T03 §7): listens to Windows' animation setting, and is put in
    /// <paramref name="resources"/> under <see cref="GateKey"/>, where a view finds it. Disposing it stops the
    /// listening and takes it out of the resources.
    /// </summary>
    public static Motion Install(ResourceDictionary resources, WindowsAnimation windows)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(windows);
        var gate = new Motion(resources, windows) { _installedIn = resources, _owned = windows };
        windows.Listen();
        resources[GateKey] = gate;
        return gate;
    }

    /// <param name="resources">Where the generated theme is merged: the application's resources.</param>
    /// <param name="windows">Windows' animation setting.</param>
    public Motion(ResourceDictionary resources, IWindowsAnimation windows)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        // Each design duration must be there: a theme without one is a build fault, said now rather than as an
        // animation that silently never runs.
        foreach (var key in new[] { FastKey, NormalKey, SlowKey, PulseKey }) Design(key);
        _ = Ease;
        _windows.Changed += OnWindowsChanged;
    }

    /// <summary>Whether anything animates: false while Windows' animation effects are off.</summary>
    public bool Animates => _windows.On;

    /// <summary>Hover and press feedback: 120 ms by design.</summary>
    public Duration Fast => Effective(FastKey);

    /// <summary>The rail marker, page changes and overlays: 220 ms by design.</summary>
    public Duration Normal => Effective(NormalKey);

    /// <summary>A value changing: 400 ms by design.</summary>
    public Duration Slow => Effective(SlowKey);

    /// <summary>One beat of a live pulse: 2.4 s by design. Not started at all while <see cref="Animates"/> is false.</summary>
    public Duration Pulse => Effective(PulseKey);

    /// <summary>The design's easing, a cubic Bézier, for a spline key frame.</summary>
    public KeySpline Ease => _resources[EaseKey] as KeySpline ?? throw new InvalidOperationException($"the theme has no {EaseKey}");

    /// <summary>Raised when Windows' animation effects are turned on or off: whatever is animating applies at once.</summary>
    public event Action? Changed;

    private Duration Effective(string key) => _windows.On ? Design(key) : Instant;

    private Duration Design(string key) => _resources[key] is Duration duration ? duration : throw new InvalidOperationException($"the theme has no {key}");

    private void OnWindowsChanged() => Changed?.Invoke();

    public void Dispose()
    {
        _windows.Changed -= OnWindowsChanged;
        _owned?.Dispose();
        if (_installedIn is not null && ReferenceEquals(_installedIn[GateKey], this)) _installedIn.Remove(GateKey);
    }
}
