namespace Bosun.Core.Memory;

/// <summary>
/// Where the panel finds <c>bosun.exe</c> (D03 T01 §1): the variable <c>BOSUN_EXE</c> when it is set (a test, a run from
/// a checkout), else the installed <c>%LOCALAPPDATA%\Bosun\bin\bosun.exe</c>.
/// </summary>
public static class BosunTool
{
    public const string Variable = "BOSUN_EXE";

    /// <summary>The installed tool, beside the panel's own data (scripts/install.ps1 installs it there).</summary>
    public static string Installed(Func<string, string?> environment) =>
        Path.Combine(environment("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bosun", "bin", "bosun.exe");

    /// <summary>The tool's path, or null with why it is not there.</summary>
    public static string? Locate(out string why, Func<string, string?>? environment = null, Func<string, bool>? exists = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        exists ??= File.Exists;
        var named = environment(Variable);
        var path = string.IsNullOrWhiteSpace(named) ? Installed(environment) : named.Trim();
        if (exists(path))
        {
            why = "";
            return path;
        }
        why = string.IsNullOrWhiteSpace(named)
            ? $"bosun.exe is not installed at {path}. Install it with scripts/install.ps1, or name it with {Variable}."
            : $"{Variable} names {path}, which is not there.";
        return null;
    }
}
