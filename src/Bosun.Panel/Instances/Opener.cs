using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Bosun.Core.Instances;

namespace Bosun.Panel.Instances;

/// <summary>Where an instance's Open folder and Open terminal go: a Windows folder, or a folder inside a WSL distro.</summary>
/// <param name="Distro">The distro, for a WSL folder; null for a Windows one.</param>
/// <param name="Path">The folder as its side writes it: <c>D:\Bosun</c>, or <c>/home/me/app</c> or <c>/mnt/c/src</c>.</param>
public sealed record OpenTarget(string? Distro, string Path)
{
    /// <summary>The folder as Windows reaches it: itself, the drive behind <c>/mnt/&lt;drive&gt;/</c>, or the distro's share.</summary>
    public string WindowsPath => Distro is null ? Path
        : OpenCommands.MountedDrive(Path) is { } windows ? windows
        : $@"\\wsl.localhost\{Distro}{Path.Replace('/', '\\')}";
}

/// <summary>
/// The commands behind Open folder and Open terminal (D03 T02 §2, design/SPEC.md §4), built and never run here, so each
/// side's command is read in a test. A WSL folder is only ever given to WSL as its own Linux path, and to Windows through
/// the drive or the distro's share: never a Windows path handed to a WSL shell.
/// </summary>
public static partial class OpenCommands
{
    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex DistroName();

    public const string NoFolder = "Its folder couldn't be read";

    public const string NoDistro = "Its WSL distro's report could not be read";

    /// <summary>Where a row opens, or why it can't: its <c>cwd</c>, else its repo, on the side and in the distro it runs in.</summary>
    public static (OpenTarget? Target, string? Problem) Resolve(InstanceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var instance = row.Instance;
        var folder = instance.Cwd ?? instance.Repo;
        if (string.IsNullOrWhiteSpace(folder)) return (null, NoFolder);
        if (instance.Side == "windows") return (new OpenTarget(null, folder), null);
        // A WSL instance: in the distro whose report listed it, or for a holder Windows saw whose distro's report was not
        // read, the distro it names, and only with a Linux path.
        var distro = row.Distro ?? instance.GuestDistro;
        // A distro is named as WSL names one, never with characters a command line would read.
        if (distro is null || !DistroName().IsMatch(distro) || !folder.StartsWith('/')) return (null, NoDistro);
        return (new OpenTarget(distro, folder), null);
    }

    /// <summary>The Windows path of a folder under <c>/mnt/&lt;drive&gt;/</c>, or null for any other Linux path.</summary>
    public static string? MountedDrive(string linuxPath)
    {
        ArgumentNullException.ThrowIfNull(linuxPath);
        if (linuxPath.Length < 6 || !linuxPath.StartsWith("/mnt/", StringComparison.Ordinal) || !char.IsAsciiLetterLower(linuxPath[5])) return null;
        if (linuxPath.Length > 6 && linuxPath[6] != '/') return null;
        var rest = linuxPath.Length > 7 ? linuxPath[7..].Replace('/', '\\') : "";
        return $@"{char.ToUpperInvariant(linuxPath[5])}:\{rest}";
    }

    /// <summary>Explorer at the folder, as Windows reaches it.</summary>
    public static ProcessStartInfo Folder(OpenTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(target.WindowsPath);
        return start;
    }

    /// <summary>
    /// A terminal in the folder: Windows Terminal when it is there, otherwise a console of its own (<c>cmd.exe</c>, or
    /// <c>wsl.exe -d &lt;distro&gt;</c>). The folder is the process's working directory and never one of its arguments:
    /// Windows Terminal reads a <c>;</c> in any argument as the start of another command, so a folder named
    /// <c>x;calc.exe</c> would run calc (round 1). <c>wt.exe -d .</c> opens at that directory and <c>wsl.exe</c> starts
    /// in the Linux folder behind it (the drive under <c>/mnt</c>, or the distro's own path through its share).
    /// </summary>
    public static ProcessStartInfo Terminal(OpenTarget target, bool windowsTerminal)
    {
        ArgumentNullException.ThrowIfNull(target);
        ProcessStartInfo start;
        if (windowsTerminal)
        {
            start = new ProcessStartInfo("wt.exe") { UseShellExecute = false, WorkingDirectory = target.WindowsPath };
            start.ArgumentList.Add("-d");
            start.ArgumentList.Add(".");
            if (target.Distro is { } distro)
            {
                start.ArgumentList.Add("wsl.exe");
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(distro);
            }
            return start;
        }
        // A console of its own: shell-executed, so it opens its own window rather than borrowing the panel's.
        if (target.Distro is null) return new ProcessStartInfo("cmd.exe") { UseShellExecute = true, WorkingDirectory = target.Path };
        return new ProcessStartInfo("wsl.exe") { UseShellExecute = true, WorkingDirectory = target.WindowsPath, Arguments = $"-d {target.Distro}" };
    }

    /// <summary>Whether Windows Terminal is there to open: <c>wt.exe</c> on the PATH, its App Execution Alias included.</summary>
    public static bool HasWindowsTerminal(string? path = null)
    {
        foreach (var folder in (path ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(System.IO.Path.Combine(folder.Trim(), "wt.exe"))) return true;
            }
            catch (ArgumentException)
            {
                // A PATH entry with characters a path can't hold: not a folder wt.exe could be in.
            }
        }
        return false;
    }
}

/// <summary>Starts what <see cref="OpenCommands"/> built: the real one, or a test's.</summary>
public interface ILauncher
{
    /// <summary>Starts it, or throws why it couldn't start.</summary>
    void Start(ProcessStartInfo start);
}

/// <summary>Starts the process and lets it go: Explorer and the terminal live on their own.</summary>
public sealed class ProcessLauncher : ILauncher
{
    public void Start(ProcessStartInfo start)
    {
        ArgumentNullException.ThrowIfNull(start);
        try
        {
            using var started = Process.Start(start);
        }
        catch (Win32Exception e)
        {
            throw new InvalidOperationException($"{start.FileName} could not be started: {e.Message}", e);
        }
    }
}
