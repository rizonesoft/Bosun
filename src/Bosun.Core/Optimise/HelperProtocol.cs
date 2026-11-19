using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bosun.Core.Optimise;

/// <summary>A helper request or result that could not be written, read back or trusted, with why.</summary>
public sealed class HelperProtocolException(string message) : Exception(message);

/// <summary>
/// What the person confirmed, as the panel hands it to the elevated helper (D05 T02 §6): the operation, the item's id
/// (the same as on the command line), the initiating account (its SID and its data folder), and for Apply the
/// confirmed proposal, for Undo the exact backup and the restore value the person saw. The helper derives the target
/// and the current proposal from the registry and refuses any mismatch before it writes.
/// </summary>
/// <param name="Invocation">This invocation's id: 32 lowercase hex digits.</param>
/// <param name="Operation">"apply" or "undo".</param>
/// <param name="ItemId">The item.</param>
/// <param name="InitiatorSid">The SID of the account whose panel wrote this request.</param>
/// <param name="DataFolder">That panel's data folder. Never where the helper writes: it is only checked against the folder the helper resolves for the SID.</param>
/// <param name="Target">The exact setting the person saw.</param>
/// <param name="Proposal">Apply: the whole confirmed proposal.</param>
/// <param name="BackupName">Undo: the exact backup of the change being reversed.</param>
/// <param name="RestoreValue">Undo: the value the person was shown that Undo restores.</param>
public sealed record HelperRequest(string Invocation, string Operation, string ItemId, string InitiatorSid, string DataFolder, string Target, Proposal? Proposal, string? BackupName, string? RestoreValue);

/// <summary>One step the helper recorded, as it happened.</summary>
public sealed record HelperStep(string Step, ChangeLevel Level, string Outcome, string? Backup);

/// <summary>How the change or Undo ended, from <see cref="ChangeResult"/>.</summary>
public sealed record HelperFinal(ChangeStatus Status, string Message, string? Backup, bool Written, string? ValueNow, bool UndoWithheld);

/// <summary>
/// The helper's result file: every step so far, each write replacing the last whole, and the end when there is one.
/// A result with none of <see cref="Final"/>, <see cref="Refusal"/> and <see cref="Fault"/> is a helper that stopped
/// part way: <see cref="Complete"/> is false, and its steps are what recovery has to go on.
/// </summary>
public sealed record HelperResult(string Invocation, string Operation, string ItemId, IReadOnlyList<HelperStep> Steps, HelperFinal? Final, string? Refusal, string? Fault)
{
    public bool Complete => Final is not null || Refusal is not null || Fault is not null;

    /// <summary>The name of the last backup a step names: this invocation's own, the one recovery looks at.</summary>
    public string? LastBackup => Steps.LastOrDefault(s => s.Backup is not null)?.Backup;
}

/// <summary>What reading a result gave: the result bound to the invocation asked for, or why not.</summary>
public sealed record ResultRead(HelperResult? Result, string? Problem);

/// <summary>
/// The helper's two files in the initiating panel's data folder, <c>helper\&lt;invocation&gt;.request.json</c> and
/// <c>helper\&lt;invocation&gt;.result.json</c>. Each is written to a new, uniquely named temporary file, read back, put in place in one
/// step and read once more; a reader sees the old file or the new one, never half.
/// </summary>
public sealed partial class HelperFiles(string data, PanelSettingsFiles? files = null)
{
    public const int SchemaVersion = 1;
    public const int MaxFileBytes = 64 * 1024;
    public const string Apply = BackupStore.Apply;
    public const string Undo = BackupStore.Undo;

    private readonly PanelSettingsFiles _files = files ?? new PanelSettingsFiles();

    public static bool IsInvocation(string? id) => id is not null && InvocationPattern().IsMatch(id);

    public static string NewInvocation() => Guid.NewGuid().ToString("N");

    public string Folder => Path.Combine(data, "helper");

    public string RequestPath(string invocation) => Path.Combine(Folder, Safe(invocation) + ".request.json");

    public string ResultPath(string invocation) => Path.Combine(Folder, Safe(invocation) + ".result.json");

    private static string Safe(string invocation) =>
        IsInvocation(invocation) ? invocation : throw new HelperProtocolException($"'{invocation}' is not an invocation id");

    public void WriteRequest(HelperRequest request) => WriteWhole(RequestPath(request.Invocation), SerializeRequest(request), "request");

    public void WriteResult(HelperResult result) => WriteWhole(ResultPath(result.Invocation), SerializeResult(result), "result");

    private void WriteWhole(string path, byte[] bytes, string what)
    {
        // A name nothing else can have made: the file is created new (never opened, truncated or written through an
        // existing file or a link to one), so an elevated write can't be steered onto another file.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            _files.WriteNew(temporary, bytes);
            if (!_files.Read(temporary, MaxFileBytes).AsSpan().SequenceEqual(bytes)) throw new HelperProtocolException($"the {what} did not read back as it was written");
            _files.Replace(temporary, path);
            if (!_files.Read(path, MaxFileBytes).AsSpan().SequenceEqual(bytes)) throw new HelperProtocolException($"the {what} in its place is not the one that was written");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HelperProtocolException)
        {
            try
            {
                _files.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // It stays beside the file, under a name nothing reads.
            }
            throw e is HelperProtocolException own ? new HelperProtocolException($"The {what} could not be kept: {own.Message}") : new HelperProtocolException($"The {what} could not be written to {path}: {e.Message}");
        }
    }

    /// <summary>Removes a request that will not be used (the prompt was never opened). A request that can't be removed stays, unread by anything.</summary>
    public void DeleteRequest(string invocation)
    {
        try
        {
            _files.Delete(RequestPath(invocation));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // It stays under an invocation id nothing will ever start.
        }
    }

    /// <summary>True when this folder holds a request file for the invocation.</summary>
    public bool HasRequest(string invocation) => File.Exists(RequestPath(invocation));

    /// <summary>Reads a request, refusing (<see cref="HelperProtocolException"/>) anything that is not a whole, well formed one.</summary>
    public HelperRequest ReadRequest(string invocation)
    {
        byte[] bytes;
        try
        {
            bytes = _files.Read(RequestPath(invocation), MaxFileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new HelperProtocolException($"the request could not be read: {e.Message}");
        }
        return ParseRequest(bytes);
    }

    /// <summary>
    /// Reads the result for <paramref name="invocation"/>, accepting only a whole file bound to it, for this operation
    /// and item: a missing, truncated, stale or unrelated result is a <see cref="ResultRead.Problem"/>, never a result.
    /// </summary>
    public ResultRead ReadResult(string invocation, string operation, string itemId)
    {
        byte[] bytes;
        try
        {
            bytes = _files.Read(ResultPath(invocation), MaxFileBytes);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(null, "there is no result file");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, $"the result could not be read: {e.Message}");
        }
        HelperResult result;
        try
        {
            result = ParseResult(bytes);
        }
        catch (HelperProtocolException e)
        {
            return new(null, e.Message);
        }
        if (result.Invocation != invocation) return new(null, "the result belongs to another invocation");
        if (result.Operation != operation || result.ItemId != itemId) return new(null, "the result is for another operation or item");
        return new(result, null);
    }

    // ---- serialization ----

    private static Utf8JsonWriter Writer(MemoryStream buffer) => new(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public static byte[] SerializeRequest(HelperRequest request)
    {
        using var buffer = new MemoryStream();
        using (var json = Writer(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteString("invocation", request.Invocation);
            json.WriteString("operation", request.Operation);
            json.WriteString("item_id", request.ItemId);
            json.WriteString("initiator_sid", request.InitiatorSid);
            json.WriteString("data_folder", request.DataFolder);
            json.WriteString("target", request.Target);
            if (request.Proposal is { } proposal)
            {
                json.WriteStartObject("proposal");
                json.WriteString("target", proposal.Target);
                json.WriteString("before", proposal.Before);
                json.WriteString("after", proposal.After);
                json.WriteEndObject();
            }
            if (request.BackupName is not null) json.WriteString("backup", request.BackupName);
            if (request.RestoreValue is not null) json.WriteString("restore_value", request.RestoreValue);
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    public static HelperRequest ParseRequest(byte[] bytes)
    {
        using var document = Open(bytes, "request");
        var root = document.RootElement;
        var invocation = Text(root, "invocation");
        var operation = Text(root, "operation");
        if (!IsInvocation(invocation)) throw new HelperProtocolException("the request has no valid invocation id");
        if (operation is not (Apply or Undo)) throw new HelperProtocolException($"the request has the operation '{operation}'");
        Proposal? proposal = null;
        if (root.TryGetProperty("proposal", out var p)) proposal = new Proposal(Text(p, "target"), Text(p, "before"), Text(p, "after"));
        return new HelperRequest(invocation, operation, Text(root, "item_id"), Text(root, "initiator_sid"), Text(root, "data_folder"), Text(root, "target"), proposal, OptionalText(root, "backup"), OptionalText(root, "restore_value"));
    }

    public static byte[] SerializeResult(HelperResult result)
    {
        using var buffer = new MemoryStream();
        using (var json = Writer(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteString("invocation", result.Invocation);
            json.WriteString("operation", result.Operation);
            json.WriteString("item_id", result.ItemId);
            json.WriteStartArray("steps");
            foreach (var step in result.Steps)
            {
                json.WriteStartObject();
                json.WriteString("step", step.Step);
                json.WriteString("level", step.Level.ToString());
                json.WriteString("outcome", step.Outcome);
                if (step.Backup is not null) json.WriteString("backup", step.Backup);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (result.Final is { } final)
            {
                json.WriteStartObject("final");
                json.WriteString("status", final.Status.ToString());
                json.WriteString("message", final.Message);
                if (final.Backup is not null) json.WriteString("backup", final.Backup);
                json.WriteBoolean("written", final.Written);
                if (final.ValueNow is not null) json.WriteString("value_now", final.ValueNow);
                json.WriteBoolean("undo_withheld", final.UndoWithheld);
                json.WriteEndObject();
            }
            if (result.Refusal is not null) json.WriteString("refusal", result.Refusal);
            if (result.Fault is not null) json.WriteString("fault", result.Fault);
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    public static HelperResult ParseResult(byte[] bytes)
    {
        using var document = Open(bytes, "result");
        var root = document.RootElement;
        var steps = new List<HelperStep>();
        if (!root.TryGetProperty("steps", out var list) || list.ValueKind != JsonValueKind.Array) throw new HelperProtocolException("the result has no steps");
        foreach (var step in list.EnumerateArray())
            steps.Add(new HelperStep(Text(step, "step"), Level(Text(step, "level")), Text(step, "outcome"), OptionalText(step, "backup")));
        HelperFinal? final = null;
        if (root.TryGetProperty("final", out var f))
        {
            if (!Enum.TryParse<ChangeStatus>(Text(f, "status"), out var status) || !Enum.IsDefined(status)) throw new HelperProtocolException("the result has an unknown status");
            final = new HelperFinal(status, Text(f, "message"), OptionalText(f, "backup"), Flag(f, "written"), OptionalText(f, "value_now"), Flag(f, "undo_withheld"));
        }
        return new HelperResult(Text(root, "invocation"), Text(root, "operation"), Text(root, "item_id"), steps, final, OptionalText(root, "refusal"), OptionalText(root, "fault"));
    }

    private static ChangeLevel Level(string text) =>
        Enum.TryParse<ChangeLevel>(text, out var level) && Enum.IsDefined(level) ? level : throw new HelperProtocolException($"the result has the level '{text}'");

    private static JsonDocument Open(byte[] bytes, string what)
    {
        if (bytes.Length > MaxFileBytes) throw new HelperProtocolException($"the {what} is larger than {MaxFileBytes / 1024} KB");
        JsonDocument document;
        try
        {
            var text = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes;
            document = JsonDocument.Parse(text.ToArray());
        }
        catch (JsonException)
        {
            throw new HelperProtocolException($"the {what} is not whole (it is not JSON, or it is cut short)");
        }
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SchemaVersion)
        {
            document.Dispose();
            throw new HelperProtocolException($"the {what} is not a version {SchemaVersion} object");
        }
        return document;
    }

    private static string Text(JsonElement from, string key) =>
        from.ValueKind == JsonValueKind.Object && from.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new HelperProtocolException($"it has no {key}");

    private static string? OptionalText(JsonElement from, string key) =>
        from.TryGetProperty(key, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : throw new HelperProtocolException($"its {key} is not text")) : null;

    private static bool Flag(JsonElement from, string key) =>
        from.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new HelperProtocolException($"it has no {key}");

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex InvocationPattern();
}
