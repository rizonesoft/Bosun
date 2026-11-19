using System.Text;
using System.Text.Json;

namespace Bosun.Core.Memory;

/// <summary>
/// What the WSL logger needs of the panel's settings to throttle queued jobs under pressure (D05 T02 §5). The logger
/// can't read `panel.json`, so this is written beside its own files as <see cref="FileName"/> in the logs folder, and the
/// logger reads it at every sample (wsl/bosun_log.py, <c>read_policy</c>). No file, or <see cref="Enabled"/> false, is no
/// action, and a limit it has lowered is restored. The names and units are the contract
/// (tests/fixtures/pressure-policy.json is what <see cref="Serialize"/> writes, and tests/test_bosun_log.py reads that file).
/// </summary>
public sealed record PressurePolicy(bool Enabled, long FloorMb, long RecoveryBandMb, long JobLimitMb)
{
    public const string FileName = "pressure-policy.json";
    public const int SchemaVersion = 1;

    /// <summary>The policy the settings stand for: the floor is the panel's own, the band is the one <see cref="PressureWatch"/> uses.</summary>
    public static PressurePolicy From(PanelSettings settings) =>
        new(settings.AutoThrottleJobs, settings.PressureFloorMb, PressureWatch.Hysteresis, settings.JobLimitMb);

    /// <summary>The file's bytes: UTF-8 JSON, one name per line, with its version.</summary>
    public static byte[] Serialize(PressurePolicy policy)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            json.WriteStartObject();
            json.WriteNumber("schema_version", SchemaVersion);
            json.WriteBoolean("enabled", policy.Enabled);
            json.WriteNumber("floor_mb", policy.FloorMb);
            json.WriteNumber("recovery_band_mb", policy.RecoveryBandMb);
            json.WriteNumber("job_limit_mb", policy.JobLimitMb);
            json.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    /// <summary>Reads a policy from a file's bytes; false, with why, when they are not one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, out PressurePolicy policy, out string fault)
    {
        policy = new PressurePolicy(false, PanelSettings.DefaultPressureFloorMb, PressureWatch.Hysteresis, PanelSettings.DefaultJobLimitMb);
        fault = "";
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { fault = "it is not a JSON object"; return false; }
            // Its kind first: TryGetInt32 throws on a string (round 2), and the contract is false with why.
            if (!root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != SchemaVersion)
            {
                fault = $"its schema_version is not {SchemaVersion}";
                return false;
            }
            if (!root.TryGetProperty("enabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                fault = "its enabled is not true or false";
                return false;
            }
            long Whole(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : throw new FormatException(key);
            var read = new PressurePolicy(enabled.GetBoolean(), Whole("floor_mb"), Whole("recovery_band_mb"), Whole("job_limit_mb"));
            // The rules the WSL reader applies to the same file (wsl/bosun_log.py read_policy), so a policy this accepts is
            // one the logger acts on (round 2: a 512 MB job limit passed here and was refused there).
            if (read.FloorMb <= 0) { fault = "its floor_mb is zero or less"; return false; }
            if (read.RecoveryBandMb < 0) { fault = "its recovery_band_mb is negative"; return false; }
            if (read.JobLimitMb < PanelSettings.MinJobLimitMb)
            {
                fault = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"its job_limit_mb is under the least allowed, {PanelSettings.MinJobLimitMb} MB");
                return false;
            }
            policy = read;
            return true;
        }
        catch (JsonException)
        {
            fault = "it is not JSON";
            return false;
        }
        catch (FormatException e)
        {
            fault = $"its {e.Message} is missing or not a whole number";
            return false;
        }
    }

    /// <summary>
    /// Writes the policy into <paramref name="logsFolder"/> through a new, uniquely named temporary file beside it (never an existing path), read back and compared,
    /// and only then put in its place in one step (as `panel.json` is saved), so the logger reads the old policy or the new
    /// one, never half of either. Whatever fails, the file there is as it was. This is the code the Settings save calls.
    /// </summary>
    public static SaveOutcome Write(string logsFolder, PressurePolicy policy, PanelSettingsFiles? files = null)
    {
        files ??= new PanelSettingsFiles();
        var path = Path.Combine(logsFolder, FileName);
        // A name nobody can predict, made new: the logs folder is shared, so a temporary file at a fixed name could be a link to
        // another file, which a write would truncate.
        var temporary = Path.Combine(logsFolder, $".{FileName}.{Guid.NewGuid():N}.tmp");
        var bytes = Serialize(policy);
        try
        {
            files.WriteNew(temporary, bytes);
            if (!files.Read(temporary, bytes.Length).AsSpan().SequenceEqual(bytes))
                throw new IOException("the policy did not read back as it was written");
            files.Replace(temporary, path);
            // Read back from where the logger reads it: a replace that landed something else is a failure, said.
            if (!files.Read(path, bytes.Length).AsSpan().SequenceEqual(bytes))
                throw new IOException("the policy in place is not the one written");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { files.Delete(temporary); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { /* the next write replaces it */ }
            return SaveOutcome.Failed($"{path} could not be written: {e.Message}");
        }
        return SaveOutcome.Done;
    }

    /// <summary>Writes the policy the settings stand for.</summary>
    public static SaveOutcome Write(string logsFolder, PanelSettings settings, PanelSettingsFiles? files = null) =>
        Write(logsFolder, From(settings), files);

    /// <summary>The text the file holds, for a message or a test.</summary>
    public static string Text(PressurePolicy policy) => Encoding.UTF8.GetString(Serialize(policy));
}
