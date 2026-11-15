using Microsoft.Win32;

namespace Bosun.Panel.Theme;

/// <summary>What Windows says of its own theme: whether apps are light, and when that changes.</summary>
public interface IWindowsTheme
{
    bool IsLight { get; }

    /// <summary>Raised when <see cref="IsLight"/> has changed. On any thread.</summary>
    event Action? Changed;
}

/// <summary>
/// Windows' own app theme (D02 T02 §5): `AppsUseLightTheme` under the user's Personalize key, 0 for dark, and light
/// when it is 1 or missing, as Windows treats it. Windows says a preference changed through
/// <see cref="SystemEvents.UserPreferenceChanged"/>, on a thread of its own; the value is read again then, and
/// <see cref="Changed"/> raised only if it is different.
/// </summary>
public sealed class WindowsTheme : IWindowsTheme, IDisposable
{
    public const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    public const string Value = "AppsUseLightTheme";

    private readonly Func<int?> _read;
    private bool _isLight;
    private bool _listening;

    /// <param name="read">The value, or null when it is missing or can't be read. The registry's own by default.</param>
    public WindowsTheme(Func<int?>? read = null)
    {
        _read = read ?? (() => ReadFrom(Personalize));
        _isLight = Light(_read());
    }

    public bool IsLight => _isLight;

    public event Action? Changed;

    /// <summary>Whether a value of the setting means light: anything but 0.</summary>
    public static bool Light(int? value) => value != 0;

    /// <summary>The setting under a key path of HKEY_CURRENT_USER; null when the key or the value is not there, or the registry refuses.</summary>
    public static int? ReadFrom(string path)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue(Value) is int number ? number : null;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null; // as missing: light, which is what Windows itself falls back to
        }
    }

    /// <summary>Starts listening for Windows' preference changes. <see cref="Dispose"/> stops it.</summary>
    public void Listen()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
    }

    private void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => PreferenceChanged(e.Category);

    /// <summary>
    /// Windows said a preference of this category changed. The app theme is among the general ones; the value is
    /// read again, and a change is passed on. Internal so a test can say it without changing Windows.
    /// </summary>
    internal void PreferenceChanged(UserPreferenceCategory category)
    {
        if (category != UserPreferenceCategory.General) return;
        var light = Light(_read());
        if (light == _isLight) return;
        _isLight = light;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (!_listening) return;
        _listening = false;
        // A static event: left subscribed, it would keep this object, and its listener thread, for the process's life.
        SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
    }
}
