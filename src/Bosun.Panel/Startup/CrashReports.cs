using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace Bosun.Panel.Startup;

/// <summary>A report that was written (<see cref="Path"/>), or why it could not be (<see cref="Failure"/>).</summary>
public sealed record CrashReport(string? Path, string? Failure)
{
    public bool Saved => Path is not null;
}

/// <summary>What must never reach a report: the names that identify the person and the machine.</summary>
public sealed record CrashPrivacy(string UserProfile, string UserName, string MachineName)
{
    public static CrashPrivacy OfThisProcess() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);
}

/// <summary>
/// Crash reports (D02 T02 §1): text files in the crash folder, written temporary-file-then-replace. A report holds
/// the time, the panel's version, the Windows and .NET versions, which handler caught the exception, and the exception
/// with its inner exceptions and stacks. It never holds the environment or the command line, and the person's profile
/// path, user name and the machine's name are replaced wherever they appear, since a path in an exception's message
/// carries them. One report is at most <see cref="MaxBytes"/>, and the folder keeps the newest <see cref="Keep"/>, so
/// a crash loop can't fill the disk.
/// </summary>
public static partial class CrashReports
{
    public const int MaxBytes = 256 * 1024;
    public const int Keep = 20;
    public const string CutLine = "[cut: the report was longer than 256 KB]";

    [GeneratedRegex(@"^crash-\d{8}-\d{6}-\d+-(\d+)\.txt$")]
    private static partial Regex ReportName();

    // The last part of a report's name: counted up for the life of the process, so a name freed by pruning is never
    // used again and two reports of one second keep their order.
    private static int _sequence;

    /// <summary>Whether a file in the crash folder is a report of ours (or one half written): nothing else there is touched.</summary>
    public static bool Owns(string fileName) =>
        ReportName().IsMatch(fileName) || (fileName.EndsWith(".tmp", StringComparison.Ordinal) && ReportName().IsMatch(fileName[..^4]));

    /// <summary>
    /// Writes a report for <paramref name="exception"/>, caught by <paramref name="source"/> ("UI thread", "Thread",
    /// "Task"). Never throws: a crash handler that fails must not hide the crash it was reporting.
    /// </summary>
    public static CrashReport Write(string folder, Exception exception, string source, CrashPrivacy? privacy = null)
    {
        string? temporary = null;
        try
        {
            var text = Compose(exception, source, privacy ?? CrashPrivacy.OfThisProcess(), DateTime.UtcNow);
            Directory.CreateDirectory(folder);
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            for (var tries = 0; tries < 1000; tries++)
            {
                var path = System.IO.Path.Combine(folder, $"crash-{stamp}-{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}.txt");
                if (File.Exists(path)) continue; // another process with this id, in this second: take the next number
                temporary = path + ".tmp";
                File.WriteAllBytes(temporary, text);
                File.Move(temporary, path, overwrite: false);
                temporary = null;
                Prune(folder);
                return new CrashReport(path, null);
            }
            return new CrashReport(null, "too many reports were written in one second");
        }
        // Everything, on purpose: this runs while the panel is already failing, and whatever goes wrong here (no disk
        // space, no permission, a file where the folder should be, no memory) is reported as the reason, not thrown.
        catch (Exception e)
        {
            if (temporary is not null) TryDelete(temporary);
            return new CrashReport(null, e.Message);
        }
    }

    /// <summary>The report's bytes: UTF-8, redacted, and no longer than <see cref="MaxBytes"/>.</summary>
    public static byte[] Compose(Exception exception, string source, CrashPrivacy privacy, DateTime utcNow)
    {
        var text = new StringBuilder();
        text.Append("Bosun Panel crash report\n");
        text.Append("Time (UTC): ").Append(utcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        text.Append("Version: ").Append(Version()).Append('\n');
        text.Append("Windows: ").Append(Environment.OSVersion.VersionString).Append('\n');
        text.Append(".NET: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        text.Append("Caught by: ").Append(source).Append('\n');
        text.Append('\n');
        // The type, the message, the stack, and the same for every inner exception.
        text.Append(exception).Append('\n');

        var bytes = Encoding.UTF8.GetBytes(Redact(text.ToString(), privacy));
        if (bytes.Length <= MaxBytes) return bytes;

        var cut = Encoding.UTF8.GetBytes("\n" + CutLine + "\n");
        var keep = MaxBytes - cut.Length;
        while (keep > 0 && (bytes[keep] & 0xC0) == 0x80) keep--; // never in the middle of a character
        var capped = new byte[keep + cut.Length];
        Buffer.BlockCopy(bytes, 0, capped, 0, keep);
        Buffer.BlockCopy(cut, 0, capped, keep, cut.Length);
        return capped;
    }

    /// <summary>The text with the person's profile path, user name and the machine's name replaced.</summary>
    public static string Redact(string text, CrashPrivacy privacy)
    {
        // The path first: it holds the user name, and "%USERPROFILE%" says more than "C:\Users\<user>".
        text = Replace(text, privacy.UserProfile, "%USERPROFILE%", wholeWord: false);
        text = Replace(text, privacy.UserName, "<user>", wholeWord: true);
        text = Replace(text, privacy.MachineName, "<machine>", wholeWord: true);
        return text;
    }

    private static string Replace(string text, string secret, string with, bool wholeWord)
    {
        if (string.IsNullOrWhiteSpace(secret)) return text;
        var pattern = Regex.Escape(secret);
        // As a word of its own: a user called "al" must not turn "internal" into "intern<user>".
        if (wholeWord) pattern = @"(?<![A-Za-z0-9_])" + pattern + @"(?![A-Za-z0-9_])";
        return Regex.Replace(text, pattern, with.Replace("$", "$$", StringComparison.Ordinal), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Version() =>
        typeof(CrashReports).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CrashReports).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>How old a half-written report must be before it is taken for abandoned: a write takes milliseconds.</summary>
    public static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Keeps the newest <see cref="Keep"/> reports, and removes a half-written one (<c>.tmp</c>) that a process left
    /// when it was ended in the middle of a write, once it is old enough not to be another panel's write in progress.
    /// What is not a report of ours is left where it is.
    /// </summary>
    private static void Prune(string folder)
    {
        var files = new DirectoryInfo(folder).GetFiles();
        var cutoff = DateTime.UtcNow - AbandonedAfter;
        foreach (var abandoned in files.Where(file => file.Name.EndsWith(".tmp", StringComparison.Ordinal) && Owns(file.Name) && file.LastWriteTimeUtc < cutoff))
            TryDelete(abandoned.FullName);

        var reports = files
            .Where(file => ReportName().IsMatch(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => long.TryParse(ReportName().Match(file.Name).Groups[1].Value, out var n) ? n : 0)
            .Skip(Keep);
        foreach (var old in reports) TryDelete(old.FullName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Held open by a reader: it stays, and the next write prunes it.
        }
    }
}
