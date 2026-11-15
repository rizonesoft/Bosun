using System.IO;

namespace Bosun.Panel.Startup;

/// <summary>
/// Where the panel keeps what is its own (D02 T02 §1): <c>%LOCALAPPDATA%\Bosun</c>, or the folder the environment
/// variable <c>BOSUN_PANEL_DATA</c> names, so a test never writes into the person's own folder.
/// </summary>
public static class PanelPaths
{
    public const string Variable = "BOSUN_PANEL_DATA";

    /// <summary>The panel's data folder for this process.</summary>
    public static string Data() => Data(Environment.GetEnvironmentVariable(Variable));

    /// <summary>
    /// The panel's data folder for a given value of the variable; null or empty means it is not set. A value that
    /// can't be a folder's path (only spaces, a character a path can't hold, too long) gives the default folder: use
    /// <see cref="TryData"/> to tell the person.
    /// </summary>
    public static string Data(string? variable) => TryData(variable, out var folder, out _) ? folder : Default();

    /// <summary>The folder when nothing names another: <c>Bosun</c> under the local application data folder.</summary>
    public static string Default() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bosun");

    /// <summary>
    /// The data folder for a given value of the variable, never throwing: false, with why, when the value can't be a
    /// folder's path, and <paramref name="folder"/> is then the default one, so there is always somewhere to write a
    /// crash report.
    /// </summary>
    public static bool TryData(string? variable, out string folder, out string refusal)
    {
        refusal = "";
        folder = Default();
        if (string.IsNullOrEmpty(variable)) return true;
        try
        {
            if (string.IsNullOrWhiteSpace(variable)) throw new ArgumentException("it holds only spaces");
            folder = Path.GetFullPath(variable);
            return true;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            refusal = $"{Variable} is set to a value that can't be a folder's path ({e.Message}). Clear the variable, or correct it, and start Bosun Panel again.";
            return false;
        }
    }

    /// <summary>Where crash reports are written: <c>crash</c> under the data folder.</summary>
    public static string Crash() => Crash(Data());

    public static string Crash(string data) => Path.Combine(data, "crash");
}
