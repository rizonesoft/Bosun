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
    public virtual bool Exists(string path) => File.Exists(path);

    public virtual byte[] Read(string path) => File.ReadAllBytes(path);

    /// <summary>Writes a new file, through to the disk, making its folder if it is not there.</summary>
    public virtual void Write(string path, byte[] bytes)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } folder) Directory.CreateDirectory(folder);
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Puts one file in another's place in one step: a reader sees the old file or the new one, never half of either.</summary>
    public virtual void Replace(string from, string to) => File.Move(from, to, overwrite: true);

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
            if (!_files.Exists(FilePath))
            {
                Set(PanelSettings.Defaults, SettingsState.Defaults, null);
                return;
            }
            bytes = _files.Read(FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Set(PanelSettings.Defaults, SettingsState.Unavailable, $"{name} could not be read: {e.Message}");
            return;
        }

        if (TryParse(bytes, out var settings, out var fault))
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
        Current = settings;
        State = state;
        Notice = notice;
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
        if (settings == Current && State == SettingsState.Loaded) return SaveOutcome.Done; // the file already holds them

        var bytes = Serialize(settings);
        try
        {
            _files.Write(TemporaryPath, bytes);
            // Checked before anything is replaced: until the new file is known good, the old one is untouched.
            if (!_files.Read(TemporaryPath).AsSpan().SequenceEqual(bytes))
                throw new IOException("the new settings did not read back as they were written");
            _files.Replace(TemporaryPath, FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            TryDeleteTemporary();
            return SaveOutcome.Failed($"{Path.GetFileName(FilePath)} could not be saved: {e.Message}");
        }

        Set(settings, SettingsState.Loaded, null);
        Changed?.Invoke(settings);
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
            document = JsonDocument.Parse(text.ToArray());
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

            settings = new PanelSettings { Theme = theme, ActivityPaneHeight = height };
            return true;
        }
    }
}
