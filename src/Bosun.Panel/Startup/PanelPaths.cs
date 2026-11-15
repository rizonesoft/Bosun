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

    /// <summary>The panel's data folder for a given value of the variable; null or empty means it is not set.</summary>
    public static string Data(string? variable) =>
        string.IsNullOrEmpty(variable)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bosun")
            : Path.GetFullPath(variable);

    /// <summary>Where crash reports are written: <c>crash</c> under the data folder.</summary>
    public static string Crash() => Crash(Data());

    public static string Crash(string data) => Path.Combine(data, "crash");
}
