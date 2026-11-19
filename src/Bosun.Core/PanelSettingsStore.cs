using System.Text;
using System.Text.Json;

namespace Bosun.Core;

/// <summary>How the settings came to be what they are.</summary>
public enum SettingsState
{
    /// <summary>Read from the file.</summary>
    Loaded,

    /// <summary>There is no file: the defaults. Not a fault.</summary>
    Defaults,

    /// <summary>The file could not be read as settings: it was moved aside and the defaults are in use. <see cref="PanelSettingsStore.Notice"/> says why.</summary>
    Restored,

    /// <summary>
    /// The file is there and could not be read at all, or could not be moved aside: it is left exactly as it is, the
    /// defaults stand in, and nothing is saved until a <see cref="PanelSettingsStore.Reload"/> has read it.
    /// </summary>
    Unavailable,
}

/// <summary>How a save ended: done, or not, with why.</summary>
public sealed record SaveOutcome(bool Succeeded, string? Reason)
{
    public static SaveOutcome Done { get; } = new(true, null);

    public static SaveOutcome Failed(string reason) => new(false, reason);
}

/// <summary>
/// The store's hands on the disk, apart so a test can make each one fail. What they throw for the disk's own reasons
/// (<see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>) the store turns into an outcome.
/// </summary>
public class PanelSettingsFiles
{
    /// <summary>
    /// Reads at most <paramref name="limit"/> bytes and one more: a caller that gets more than its limit knows the
    /// file is too big without the whole of it ever being in memory. A file that is not there is
    /// <see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/>, and nothing else is: asking
    /// first whether it exists would answer no for a file that is there and may not be looked at.
    /// </summary>
    public virtual byte[] Read(string path, int limit)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[(int)Math.Min(stream.Length, (long)limit + 1)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>Writes a new file, through to the disk, making its folder if it is not there.</summary>
    public virtual void Write(string path, byte[] bytes)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Writes a file that must not be there yet, through to the disk: <see cref="IOException"/> when the path is taken, so an
    /// existing file (or a hard link to another one) is never opened, truncated or written. For a temporary file in a folder
    /// somebody else can write to.
    /// </summary>
    public virtual void WriteNew(string path, byte[] bytes) => WriteNew(path, bytes, null);

    /// <summary>
    /// As <see cref="WriteNew(string, byte[])"/>, bound to where the file really is: once it is created, and before any
    /// content is written, the open handle's final path (links followed) is given to <paramref name="finalPathAllowed"/>.
    /// When that says no the file is deleted through the handle and <see cref="IOException"/> is thrown, so nothing is
    /// ever written to a place the path only led to after it was checked. Where the platform can't ask a handle for its
    /// path the file is created as without a check.
    /// </summary>
    public virtual void WriteNew(string path, byte[] bytes, Func<string, bool>? finalPathAllowed)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        var bound = finalPathAllowed is not null && OperatingSystem.IsWindows();
        using var stream = bound
            ? Optimise.NativeFiles.CreateExclusive(path)
            : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (bound)
        {
            var final = Optimise.NativeFiles.FinalPath(stream.SafeFileHandle);
            if (!finalPathAllowed!(final))
            {
                Optimise.NativeFiles.DeleteOnClose(stream);
                throw new IOException($"'{final}' is outside the folder this write is confined to: nothing was written there");
            }
        }
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Puts one file in another's place in one step: a reader sees the old file or the new one, never half of either.</summary>
    public virtual void Replace(string from, string to) => File.Move(from, to, overwrite: true);

    /// <summary>Puts a file in a place nothing is in: <see cref="IOException"/> when the place is taken, so a file kept is never written over.</summary>
    public virtual void MoveNew(string from, string to) => File.Move(from, to, overwrite: false);

    public virtual void Delete(string path) => File.Delete(path);
}

/// <summary>
/// The panel's settings on disk (D02 T02 §5): `panel.json`, at the path it is given. It names no folder itself.
///
/// Reading: a file that is not there is the defaults. One that can't be read as settings is moved aside as
/// `panel.json.bad` with a notice, and the defaults are used. One that can't be read at all is left alone, and
/// nothing is saved over it.
///
/// Saving: the new settings are written to a temporary file beside `panel.json`, read back and compared, and only
/// then put in its place in one step. Whatever fails, the old file is as it was, <see cref="Current"/> is unchanged
/// and no listener is told. Used from one thread, the panel's UI thread.
/// </summary>
public sealed class PanelSettingsStore
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// The most a settings file can be. The real one is a few hundred bytes; a file is somebody else's input, and one
    /// of a gigabyte must not be read into memory before the panel has a window.
    /// </summary>
    public const int MaxFileBytes = 64 * 1024;

    private readonly PanelSettingsFiles _files;

    public PanelSettingsStore(string path, PanelSettingsFiles? files = null)
    {
        FilePath = path;
        _files = files ?? new PanelSettingsFiles();
        Reload();
    }

    public string FilePath { get; }

    /// <summary>Where a file that could not be read as settings is kept.</summary>
    public string BadPath => FilePath + ".bad";

    private string TemporaryPath => FilePath + ".tmp";

    public PanelSettings Current { get; private set; } = PanelSettings.Defaults;

    public SettingsState State { get; private set; }

    /// <summary>Why the settings are not the file's own, for <see cref="SettingsState.Restored"/> and <see cref="SettingsState.Unavailable"/>.</summary>
    public string? Notice { get; private set; }

    /// <summary>Raised after a save that worked, with the settings now in force.</summary>
    public event Action<PanelSettings>? Changed;

    /// <summary>Reads the file again. This is the way out of <see cref="SettingsState.Unavailable"/>.</summary>
    public void Reload()
    {
        var name = Path.GetFileName(FilePath);
        byte[] bytes;
        try
        {
            bytes = _files.Read(FilePath, MaxFileBytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Read, not asked about: only these two say the file is not there. A folder that may not be looked into
            // is refused below, as unavailable, and never taken for a first run.
            Set(PanelSettings.Defaults, SettingsState.Defaults, null);
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Set(PanelSettings.Defaults, SettingsState.Unavailable, $"{name} could not be read: {e.Message}");
            return;
        }

        var fault = "";
        if (bytes.Length > MaxFileBytes) fault = $"it is larger than {MaxFileBytes / 1024} KB";
        else if (TryParse(bytes, out var settings, out fault))
        {
            Set(settings, SettingsState.Loaded, null);
            return;
        }

        // Not settings. Kept, never lost: moved aside so the person can look at it, and so a save can't destroy it.
        try
        {
            _files.Replace(FilePath, BadPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Set(PanelSettings.Defaults, SettingsState.Unavailable, $"{name} is not valid settings ({fault}) and could not be moved aside: {e.Message}");
            return;
        }
        Set(PanelSettings.Defaults, SettingsState.Restored, $"{name} was not valid settings ({fault}). It was kept as {Path.GetFileName(BadPath)}, and the default settings are in use.");
    }

    private void Set(PanelSettings settings, SettingsState state, string? notice)
    {
        var changed = settings != Current;
        Current = settings;
        State = state;
        Notice = notice;
        // A reload that reads different settings (a file that was held and is now released) is a change like a save:
        // what follows the settings, the theme first of all, is told.
        if (changed) Changed?.Invoke(settings);
    }

    /// <summary>
    /// Changes the settings: <paramref name="change"/> is given the latest stored ones and returns them with what it
    /// means to change, so two parts of the panel never overwrite each other's settings. Only a save that worked
    /// changes <see cref="Current"/> and raises <see cref="Changed"/>.
    /// </summary>
    public SaveOutcome Update(Func<PanelSettings, PanelSettings> change) => Save(change(Current));

    /// <summary>Saves these settings whole.</summary>
    public SaveOutcome Save(PanelSettings settings)
    {
        if (State == SettingsState.Unavailable)
            return SaveOutcome.Failed($"Nothing is saved over settings that could not be read. {Notice}");
        if (settings.Fault() is { } wrong) return SaveOutcome.Failed($"Not saved: {wrong}.");
        if (settings == Current && State == SettingsState.Loaded) return SaveOutcome.Done; // the file already holds them

        var bytes = Serialize(settings);
        try
        {
            _files.Write(TemporaryPath, bytes);
            // Checked before anything is replaced: until the new file is known good, the old one is untouched.
            if (!_files.Read(TemporaryPath, MaxFileBytes).AsSpan().SequenceEqual(bytes))
                throw new IOException("the new settings did not read back as they were written");
            _files.Replace(TemporaryPath, FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporary();
            // The whole path: the person who has to fix a read-only file or a full disk needs to know which one,
            // and where (D02 T04 §7).
            return SaveOutcome.Failed($"{FilePath} could not be saved: {e.Message}");
        }

        Set(settings, SettingsState.Loaded, null); // tells the listeners
        return SaveOutcome.Done;
    }

    private void TryDeleteTemporary()
    {
        try
        {
            _files.Delete(TemporaryPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays, and the next save writes over it.
        }
    }

    /// <summary>A true or false setting: its default when the key is missing, a fault when it is anything but true or false.</summary>
    private static bool TryBoolean(JsonElement root, string key, bool missing, out bool value, out string fault)
    {
        value = missing;
        fault = "";
        if (!root.TryGetProperty(key, out var found)) return true;
        if (found.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = found.GetBoolean();
            return true;
        }
        fault = $"its {key} is not true or false";
        return false;
    }

    /// <summary>The settings as the file holds them: UTF-8 JSON with a `schema_version`.</summary>
    public static byte[] Serialize(PanelSettings settings)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteString("theme", settings.Theme.ToString());
            json.WriteNumber("activity_pane_height", settings.ActivityPaneHeight);
            json.WriteNumber("pressure_floor_mb", settings.PressureFloorMb);
            json.WriteBoolean("notify_memory_pressure", settings.NotifyMemoryPressure);
            json.WriteBoolean("tray_notice_said", settings.TrayNoticeSaid);
            json.WriteBoolean("auto_throttle_jobs", settings.AutoThrottleJobs);
            json.WriteNumber("job_limit_mb", settings.JobLimitMb);
            if (settings.Window is { } place)
            {
                json.WriteStartObject("window");
                json.WriteNumber("left", place.Left);
                json.WriteNumber("top", place.Top);
                json.WriteNumber("width", place.Width);
                json.WriteNumber("height", place.Height);
                json.WriteNumber("dpi", place.Dpi);
                json.WriteBoolean("maximised", place.Maximised);
                json.WriteEndObject();
            }
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>
    /// Reads settings from a file's bytes; false, with why, when they are not settings. A key that is missing takes
    /// its default (a file written before the key existed); a value of the wrong kind is a fault; a height outside
    /// its limits is brought inside them.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out PanelSettings settings, out string fault)
    {
        settings = PanelSettings.Defaults;
        fault = "";
        JsonDocument document;
        try
        {
            // The byte order mark an editor may have added is not part of the JSON.
            var text = bytes.StartsWith(Encoding.UTF8.Preamble) ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;
            document = JsonDocument.Parse(text.ToArray()); // at most MaxFileBytes: the read is bounded
        }
        catch (JsonException)
        {
            // The parser's own words (a line and a byte position) are for whoever opens the kept file, not for a
            // notice: the file is kept whole beside this one.
            fault = "it is not JSON";
            return false;
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                fault = "it is not a JSON object";
                return false;
            }
            if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number))
            {
                fault = "it has no schema_version";
                return false;
            }
            if (number != SchemaVersion)
            {
                fault = $"its schema_version is {number}, and this panel reads {SchemaVersion}";
                return false;
            }

            var theme = PanelSettings.Defaults.Theme;
            if (root.TryGetProperty("theme", out var named))
            {
                if (named.ValueKind != JsonValueKind.String || !Enum.TryParse(named.GetString(), ignoreCase: false, out theme) || !Enum.IsDefined(theme))
                {
                    fault = "its theme is not FollowWindows, Dark or Light";
                    return false;
                }
            }

            var height = PanelSettings.DefaultActivityPaneHeight;
            if (root.TryGetProperty("activity_pane_height", out var size))
            {
                if (size.ValueKind != JsonValueKind.Number || !size.TryGetDouble(out height))
                {
                    fault = "its activity_pane_height is not a number";
                    return false;
                }
            }

            var floor = PanelSettings.DefaultPressureFloorMb;
            if (root.TryGetProperty("pressure_floor_mb", out var threshold))
            {
                if (threshold.ValueKind != JsonValueKind.Number || !threshold.TryGetInt64(out floor))
                {
                    fault = "its pressure_floor_mb is not a whole number";
                    return false;
                }
            }

            if (!TryBoolean(root, "notify_memory_pressure", PanelSettings.Defaults.NotifyMemoryPressure, out var notify, out fault)) return false;
            if (!TryBoolean(root, "tray_notice_said", PanelSettings.Defaults.TrayNoticeSaid, out var said, out fault)) return false;

            if (!TryBoolean(root, "auto_throttle_jobs", PanelSettings.Defaults.AutoThrottleJobs, out var throttle, out fault)) return false;
            var jobLimit = PanelSettings.DefaultJobLimitMb;
            if (root.TryGetProperty("job_limit_mb", out var limit))
            {
                if (limit.ValueKind != JsonValueKind.Number || !limit.TryGetInt64(out jobLimit))
                {
                    fault = "its job_limit_mb is not a whole number";
                    return false;
                }
                if (PanelSettings.JobLimitFault(jobLimit) is { } outside)
                {
                    fault = outside;
                    return false;
                }
            }

            WindowPlace? window = null;
            if (root.TryGetProperty("window", out var placed) && placed.ValueKind != JsonValueKind.Null)
            {
                int Whole(string key) => placed.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : throw new FormatException(key);
                try
                {
                    if (placed.ValueKind != JsonValueKind.Object) throw new FormatException("window");
                    if (!placed.TryGetProperty("maximised", out var max) || max.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new FormatException("maximised");
                    window = new WindowPlace(Whole("left"), Whole("top"), Whole("width"), Whole("height"), Whole("dpi"), max.GetBoolean());
                }
                catch (FormatException e)
                {
                    fault = e.Message == "window" ? "its window is not an object" : $"its window's {e.Message} is missing or not what it should be";
                    return false;
                }
                if (window.Fault() is { } wrong)
                {
                    fault = wrong;
                    return false;
                }
            }

            settings = new PanelSettings { Theme = theme, ActivityPaneHeight = height, PressureFloorMb = floor, NotifyMemoryPressure = notify, TrayNoticeSaid = said, AutoThrottleJobs = throttle, JobLimitMb = jobLimit, Window = window };
            return true;
        }
    }
}
