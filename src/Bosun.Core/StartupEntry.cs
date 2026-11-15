using System.Security;

namespace Bosun.Core;

/// <summary>Whether Windows starts the panel at sign-in.</summary>
public enum StartupState
{
    Off,
    On,

    /// <summary>
    /// The Run value is there, and Windows has it turned off (Settings, Apps, Startup, or Task Manager): Windows
    /// will not start the panel, whatever the value says.
    /// </summary>
    DisabledByWindows,

    /// <summary>
    /// The Run value is there and starts something else: another copy of the panel, at a path it was moved or
    /// updated away from. Windows would start that, or nothing. <see cref="StartupRead.Reason"/> holds the value.
    /// </summary>
    OtherProgram,

    /// <summary>The registry could not be read; <see cref="StartupRead.Reason"/> says why.</summary>
    Unavailable,
}

/// <summary>What reading the start-up entry gave.</summary>
public sealed record StartupRead(StartupState State, string? Reason = null)
{
    /// <summary>The Run value is there: what the person chose, whatever became of it since.</summary>
    public bool Chosen => State is StartupState.On or StartupState.DisabledByWindows or StartupState.OtherProgram;
}

/// <summary>
/// The registry's Run key and Windows' own record of which entries it has turned off, as the start-up entry needs
/// them. The real one is the panel's (it needs Windows); a test brings its own. An implementation throws
/// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="SecurityException"/> when the
/// registry refuses.
/// </summary>
public interface IRunKey
{
    /// <summary>The Run value's text, or null when there is none.</summary>
    string? Read(string name);

    void Write(string name, string value);

    void Delete(string name);

    /// <summary>
    /// What Windows' start-up record (<c>Explorer\StartupApproved\Run</c>) says of the entry: true when it is on,
    /// false when Windows has it turned off, null when Windows has no record of it (which is on).
    /// </summary>
    bool? Approved(string name);
}

/// <summary>
/// Start with Windows, in the tray (D02 T02 §5, design/SPEC.md §15): the `Bosun Panel` value under the user's Run
/// key, holding the panel's own path, quoted, with `--tray`. The value is the setting: it is kept nowhere else, read
/// when it is wanted and read back after every change, so the panel never says on while Windows says off.
/// </summary>
public sealed class StartupEntry(IRunKey key, string programPath)
{
    public const string Name = "Bosun Panel";

    /// <summary>What the Run value holds when the entry is on.</summary>
    public string Command { get; } = $"\"{programPath}\" --tray";

    public StartupRead Read()
    {
        try
        {
            if (key.Read(Name) is not { } value) return new StartupRead(StartupState.Off);
            // What Windows would start, not merely that there is a value: paths differ only by case on Windows.
            if (!string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase)) return new StartupRead(StartupState.OtherProgram, value);
            return new StartupRead(key.Approved(Name) == false ? StartupState.DisabledByWindows : StartupState.On);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new StartupRead(StartupState.Unavailable, $"The Windows start-up entry could not be read: {e.Message}");
        }
    }

    /// <summary>Turns the entry on or off, and reads it back: done only when the registry then holds what was meant.</summary>
    public SaveOutcome Set(bool on)
    {
        try
        {
            if (on) key.Write(Name, Command);
            else key.Delete(Name);

            var now = key.Read(Name);
            if (on ? now == Command : now is null) return SaveOutcome.Done;
            return SaveOutcome.Failed($"Start with Windows could not be turned {(on ? "on" : "off")}: the start-up entry did not read back as it was written.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return SaveOutcome.Failed($"Start with Windows could not be turned {(on ? "on" : "off")}: {e.Message}");
        }
    }
}
