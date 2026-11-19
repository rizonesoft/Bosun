using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bosun.Core.Optimise;

/// <summary>A backup that could not be written, read back or trusted, with why.</summary>
public sealed class BackupException(string message) : Exception(message);

/// <summary>Names one backup: its item and its file name. Never "the newest": a result carries the exact backup of its own change.</summary>
public sealed record BackupRef(string ItemId, string Name);

/// <summary>A backup as the file holds it.</summary>
/// <param name="ItemId">The item it was taken for.</param>
/// <param name="Target">The exact setting it was taken of; the same as <see cref="Proposal"/>'s.</param>
/// <param name="Operation">"apply" or "undo".</param>
/// <param name="Before">The value the target held when the backup was taken.</param>
/// <param name="Proposal">The change it was taken for. Undoing this change restores <see cref="Before"/> and needs the target to hold <see cref="Proposal.After"/>.</param>
/// <param name="Created">When it was taken (UTC, to the millisecond).</param>
/// <param name="Name">The file's name.</param>
public sealed record Backup(string ItemId, string Target, string Operation, string Before, Proposal Proposal, DateTimeOffset Created, string Name)
{
    public BackupRef Ref => new(ItemId, Name);
}

/// <summary>
/// The change framework's backups (D05 T02 §2): <c>&lt;panel data&gt;\backups\&lt;item id&gt;\&lt;UTC time&gt;-&lt;operation&gt;.json</c>.
/// Each is written to a temporary file beside its place, read back, and only then moved in without replacing
/// anything, then read once more as the file it will be; one that does not come back exactly as written is refused.
/// A backup is never overwritten. It names no folder itself: it is given the panel's data folder (the panel passes
/// <c>PanelPaths.Data()</c>, so <c>BOSUN_PANEL_DATA</c> isolates tests).
/// </summary>
public sealed partial class BackupStore
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 64 * 1024;
    public const string Apply = "apply";
    public const string Undo = "undo";

    private readonly string _root;
    private readonly PanelSettingsFiles _files;
    private readonly Func<DateTimeOffset> _clock;

    public BackupStore(string panelData, PanelSettingsFiles? files = null, Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(panelData);
        _root = Path.Combine(panelData, "backups");
        _files = files ?? new PanelSettingsFiles();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The folder an item's backups are kept in.</summary>
    public string Folder(string itemId) => Path.Combine(_root, SafeId(itemId));

    public string PathOf(BackupRef reference) => Path.Combine(Folder(reference.ItemId), SafeName(reference.Name));

    /// <summary>
    /// Writes a backup of <paramref name="proposal"/>'s target and returns the exact reference to it. Throws
    /// <see cref="BackupException"/> when it could not be written and read back: the caller changes nothing then.
    /// </summary>
    public BackupRef Write(string itemId, string operation, Proposal proposal)
    {
        if (operation is not (Apply or Undo)) throw new BackupException($"'{operation}' is not a backup operation");
        var folder = Folder(itemId);
        var created = Truncate(_clock().ToUniversalTime());
        string name, finalPath;
        // A time already taken moves on a millisecond: a backup is never written over another.
        while (File.Exists(finalPath = Path.Combine(folder, name = Format(created) + "-" + operation + ".json"))) created = created.AddMilliseconds(1);

        var record = new Backup(itemId, proposal.Target, operation, proposal.Before, proposal, created, name);
        var bytes = Serialize(record);
        var temporary = Path.Combine(folder, "." + name + ".tmp");
        var moved = false;
        try
        {
            _files.Write(temporary, bytes);
            if (!_files.Read(temporary, MaxFileBytes).AsSpan().SequenceEqual(bytes))
                throw new BackupException("the backup did not read back as it was written");
            _files.MoveNew(temporary, finalPath);
            moved = true;
            var back = Parse(_files.Read(finalPath, MaxFileBytes), itemId, name);
            if (back != record) throw new BackupException("the backup in its place is not the one that was written");
            return record.Ref;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException)
        {
            TryDelete(moved ? finalPath : temporary); // ours, and not a backup anyone can trust
            throw e as BackupException is { } own ? new BackupException($"The backup could not be written: {own.Message}") : new BackupException($"The backup could not be written to {finalPath}: {e.Message}");
        }
    }

    /// <summary>
    /// Reads one backup, refusing (<see cref="BackupException"/>) what is not exactly a well formed backup for this
    /// item, under this name: truncated or foreign JSON, another item's, a target that differs from its proposal's, an
    /// operation that differs from its name's.
    /// </summary>
    public Backup Read(BackupRef reference)
    {
        var path = PathOf(reference);
        byte[] bytes;
        try
        {
            bytes = _files.Read(path, MaxFileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackupException($"The backup {reference.Name} could not be read: {e.Message}");
        }
        return Parse(bytes, reference.ItemId, reference.Name);
    }

    private string OutcomePath(BackupRef reference) => PathOf(reference) + ".outcome.json";

    /// <summary>
    /// Records what the target read after the change this backup belongs to, beside the backup (which stays
    /// immutable): temp, read back, moved in without replacing anything. Throws <see cref="BackupException"/> when it
    /// could not be kept. Written once; a second call for the same backup is refused.
    /// </summary>
    public void WriteOutcome(Backup backup, string observed)
    {
        var path = OutcomePath(backup.Ref);
        var temporary = Path.Combine(Folder(backup.ItemId), "." + backup.Name + ".outcome.tmp");
        var bytes = SerializeOutcome(backup, observed);
        var moved = false;
        try
        {
            _files.Write(temporary, bytes);
            if (!_files.Read(temporary, MaxFileBytes).AsSpan().SequenceEqual(bytes)) throw new BackupException("the outcome did not read back as it was written");
            _files.MoveNew(temporary, path);
            moved = true;
            if (!_files.Read(path, MaxFileBytes).AsSpan().SequenceEqual(bytes)) throw new BackupException("the outcome in its place is not the one that was written");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BackupException)
        {
            TryDelete(moved ? path : temporary);
            throw new BackupException($"The outcome could not be kept: {e.Message}");
        }
    }

    /// <summary>The value recorded for <paramref name="backup"/>'s change, or null when none was recorded or the record is not this backup's own.</summary>
    public string? ReadOutcome(Backup backup)
    {
        byte[] bytes;
        try
        {
            bytes = _files.Read(OutcomePath(backup.Ref), MaxFileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            string? Get(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return root.ValueKind == JsonValueKind.Object && Get("item_id") == backup.ItemId && Get("backup") == backup.Name && Get("target") == backup.Target ? Get("observed") : null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or JsonException)
        {
            return null;
        }
    }

    private static byte[] SerializeOutcome(Backup backup, string observed)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteString("item_id", backup.ItemId);
            json.WriteString("backup", backup.Name);
            json.WriteString("target", backup.Target);
            json.WriteString("observed", observed);
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static string SafeId(string id) =>
        id is not null && IdPattern().IsMatch(id) && !id.EndsWith('.') ? id : throw new BackupException($"'{id}' is not an item id a backup folder can be named after");

    private static string SafeName(string name) =>
        name is not null && NamePattern().IsMatch(name) ? name : throw new BackupException($"'{name}' is not a backup's name");

    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    private static string Format(DateTimeOffset t) => t.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

    private void TryDelete(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays; it is never read as a backup.
        }
    }

    public static byte[] Serialize(Backup backup)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteString("item_id", backup.ItemId);
            json.WriteString("target", backup.Target);
            json.WriteString("operation", backup.Operation);
            json.WriteString("before", backup.Before);
            json.WriteStartObject("proposal");
            json.WriteString("target", backup.Proposal.Target);
            json.WriteString("before", backup.Proposal.Before);
            json.WriteString("after", backup.Proposal.After);
            json.WriteEndObject();
            json.WriteString("created_utc", backup.Created.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads a backup's bytes as the backup <paramref name="name"/> of <paramref name="itemId"/>; refuses anything else.</summary>
    public static Backup Parse(byte[] bytes, string itemId, string name)
    {
        SafeId(itemId);
        SafeName(name);
        if (bytes.Length > MaxFileBytes) throw new BackupException($"{name} is larger than {MaxFileBytes / 1024} KB");
        JsonDocument document;
        try
        {
            var text = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
            document = JsonDocument.Parse(text.ToArray());
        }
        catch (JsonException)
        {
            throw new BackupException($"{name} is not a whole backup (it is not JSON, or it is cut short)");
        }
        using (document)
        {
            try
            {
                return Decode(document, itemId, name);
            }
            catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException or JsonException)
            {
                throw new BackupException($"{name} could not be decoded: {e.Message}");
            }
        }
    }

    private static Backup Decode(JsonDocument document, string itemId, string name)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new BackupException($"{name} is not a JSON object");
        if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SchemaVersion)
            throw new BackupException($"{name} has no schema_version {SchemaVersion}");

        string Text(JsonElement from, string key) =>
            from.ValueKind == JsonValueKind.Object && from.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new BackupException($"{name} has no {key}");

        var item = Text(root, "item_id");
        var target = Text(root, "target");
        var operation = Text(root, "operation");
        var before = Text(root, "before");
        root.TryGetProperty("proposal", out var proposal);
        var change = new Proposal(Text(proposal, "target"), Text(proposal, "before"), Text(proposal, "after"));
        if (!DateTimeOffset.TryParseExact(Text(root, "created_utc"), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created))
            throw new BackupException($"{name} has no valid created_utc");

        if (item != itemId) throw new BackupException($"{name} is a backup for item '{item}', not '{itemId}'");
        if (operation is not (Apply or Undo)) throw new BackupException($"{name} has the operation '{operation}'");
        if (!name.EndsWith("-" + operation + ".json", StringComparison.Ordinal)) throw new BackupException($"{name} is named for another operation than its '{operation}'");
        if (target != change.Target) throw new BackupException($"{name} names a target that is not its proposal's");
        if (before != change.Before) throw new BackupException($"{name} holds a before value that is not its proposal's");
        if (name != Format(created) + "-" + operation + ".json") throw new BackupException($"{name} is not the name its own time and operation give it: it was copied from another backup");
        return new Backup(item, target, operation, before, change, created, name);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d{8}T\d{9}Z-(apply|undo)\.json$")]
    private static partial Regex NamePattern();
}
