using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Bosun.Core;

/// <summary>One tool call: a program and its arguments as a list, never a command line.</summary>
public sealed record ProcessRequest(string Program, IReadOnlyList<string> Arguments)
{
    /// <summary>The folder the program starts in; the caller's own when null.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Variables added to the caller's own environment for this call; a null value removes one.</summary>
    public IReadOnlyDictionary<string, string?>? Environment { get; init; }

    /// <summary>Text given to the program on stdin as UTF-8; stdin is closed at once when null.</summary>
    public string? StandardInput { get; init; }

    /// <summary>How long the program may run before it and everything it started are ended.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How much of stdout, and of stderr, is kept; the rest is read and dropped.</summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024;
}

/// <summary>How a call ended.</summary>
public enum ProcessOutcome
{
    /// <summary>The program ran and exited; <see cref="ProcessResult.ExitCode"/> is set.</summary>
    Exited,

    /// <summary>It ran past <see cref="ProcessRequest.Timeout"/> and was ended.</summary>
    TimedOut,

    /// <summary>The caller cancelled and it was ended.</summary>
    Cancelled,

    /// <summary>Nothing ran; <see cref="ProcessResult.Reason"/> says why.</summary>
    NotStarted,
}

/// <summary>What a call gave back. Every ending a real tool call has is one of these, never an exception.</summary>
public sealed record ProcessResult(ProcessOutcome Outcome)
{
    /// <summary>The exit code, when the program exited by itself.</summary>
    public int? ExitCode { get; init; }

    /// <summary>What was read from stdout, up to the cap, with a last line saying so when it was cut.</summary>
    public string StandardOutput { get; init; } = "";

    /// <summary>What was read from stderr, up to the cap, with a last line saying so when it was cut.</summary>
    public string StandardError { get; init; } = "";

    public bool OutputTruncated { get; init; }

    public bool ErrorTruncated { get; init; }

    /// <summary>False when the program ended or closed stdin before it had taken all of the input.</summary>
    public bool InputDelivered { get; init; } = true;

    /// <summary>Why nothing ran, for <see cref="ProcessOutcome.NotStarted"/>.</summary>
    public string? Reason { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>
/// What reading a tool's JSON gave: the value, or which of the ways it can go wrong. Each carries the call's
/// <see cref="ProcessResult"/>, so what a tool said on stderr while succeeding is kept.
/// </summary>
public abstract record JsonResult<T>
{
    private JsonResult(ProcessResult process) => Process = process;

    public ProcessResult Process { get; }

    /// <summary>The tool exited 0 with one JSON object of the supported version, read into the type.</summary>
    public sealed record Ok : JsonResult<T>
    {
        internal Ok(T value, ProcessResult process) : base(process) => Value = value;

        public T Value { get; }
    }

    /// <summary>The object is of another <c>schema_version</c> than the caller reads: a newer or older tool.</summary>
    public sealed record Unsupported : JsonResult<T>
    {
        internal Unsupported(long found, int supported, ProcessResult process) : base(process) => (Found, Supported) = (found, supported);

        public long Found { get; }

        public int Supported { get; }
    }

    /// <summary>The tool exited 0 but stdout is not what was asked for; <see cref="Reason"/> says how.</summary>
    public sealed record Invalid : JsonResult<T>
    {
        internal Invalid(string reason, ProcessResult process) : base(process) => Reason = reason;

        public string Reason { get; }
    }

    /// <summary>The call itself did not succeed: not started, timed out, cancelled, or a nonzero exit.</summary>
    public sealed record Failed : JsonResult<T>
    {
        internal Failed(ProcessResult process) : base(process) { }
    }
}

/// <summary>
/// Runs a tool and reads what it says. Every tool call the panel makes goes through here, so the rules live here:
/// arguments are a list (no shell reads them), no window opens, the call has a time limit, the output kept is
/// bounded, and the program with everything it started is gone when the call returns. The child is created inside a
/// job object, so there is no moment in which it, or anything it starts, runs outside it.
/// </summary>
public sealed class ProcessRunner
{
    private const string CutMarker = "[cut at {0} bytes]";
    private readonly Func<ProcessRequest, ChildProcess> _start;

    public ProcessRunner() : this(ChildProcess.Start) { }

    /// <summary>For the tests: a start that can be made slow, as a PATH folder on a dead network share makes it.</summary>
    internal ProcessRunner(Func<ProcessRequest, ChildProcess> start) => _start = start;

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        if (Refusal(request) is { } refusal) return new(ProcessOutcome.NotStarted) { Reason = refusal };
        if (cancellationToken.IsCancellationRequested) return new(ProcessOutcome.Cancelled) { Elapsed = clock.Elapsed };

        // The time limit and the caller's cancel cover the whole call, the start included: finding the program reads
        // the disk, and a PATH folder on a network share that has gone away can hold that read for a long time. So the
        // start runs off the caller's thread, and a call that ends first returns at once and ends what the start made.
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = Task.Delay(request.Timeout, stop.Token);
        var starting = Task.Run(() => _start(request), CancellationToken.None);
        if (await Task.WhenAny(starting, limit).ConfigureAwait(false) != starting)
        {
            _ = starting.ContinueWith(static late =>
            {
                if (late.IsCompletedSuccessfully)
                {
                    late.Result.EndTree();
                    late.Result.Dispose();
                }
                else _ = late.Exception; // it couldn't start after all: nothing to end
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return new(cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled : ProcessOutcome.TimedOut) { Elapsed = clock.Elapsed };
        }

        ChildProcess child;
        try
        {
            child = await starting.ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(ProcessOutcome.NotStarted) { Reason = e.Message, Elapsed = clock.Elapsed };
        }

        using (child)
        {
            // The three pipes are worked at once, each on its own task: a child that fills one while the other is
            // unread, or that never takes its input, can't hold the call up.
            var output = Task.Run(() => ReadCapped(child.Output, request.MaxOutputBytes), CancellationToken.None);
            var error = Task.Run(() => ReadCapped(child.Error, request.MaxOutputBytes), CancellationToken.None);
            var input = Task.Run(() => WriteInput(child.Input, request.StandardInput), CancellationToken.None);

            await Task.WhenAny(child.Exited, limit).ConfigureAwait(false);
            var outcome = child.Exited.IsCompleted ? ProcessOutcome.Exited
                : cancellationToken.IsCancellationRequested ? ProcessOutcome.Cancelled
                : ProcessOutcome.TimedOut;
            stop.Cancel();

            // However the call ended, nothing it started is left: this ends the tree of a child that timed out or was
            // cancelled, and whatever a child that exited left running, which also lets the reads below finish.
            child.EndTree();
            await child.Exited.ConfigureAwait(false);
            var (stdout, stdoutCut) = await output.ConfigureAwait(false);
            var (stderr, stderrCut) = await error.ConfigureAwait(false);
            var delivered = await input.ConfigureAwait(false);

            return new(outcome)
            {
                ExitCode = outcome == ProcessOutcome.Exited ? child.ExitCode() : null,
                StandardOutput = stdout,
                StandardError = stderr,
                OutputTruncated = stdoutCut,
                ErrorTruncated = stderrCut,
                InputDelivered = delivered,
                Elapsed = clock.Elapsed,
            };
        }
    }

    /// <summary>
    /// Runs a tool that prints one JSON object with an integer <c>schema_version</c>, and reads it into
    /// <typeparamref name="T"/> only when that version is <paramref name="schemaVersion"/>. A version this caller
    /// doesn't read is <see cref="JsonResult{T}.Unsupported"/>, never a value with defaults where fields were missing.
    /// </summary>
    public async Task<JsonResult<T>> RunJsonAsync<T>(ProcessRequest request, JsonTypeInfo<T> typeInfo, int schemaVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var process = await RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (process.Outcome != ProcessOutcome.Exited || process.ExitCode != 0) return new JsonResult<T>.Failed(process);
        if (process.OutputTruncated) return new JsonResult<T>.Invalid($"the output is longer than the {request.MaxOutputBytes} bytes kept, so it was not read", process);

        try
        {
            using var document = JsonDocument.Parse(process.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new JsonResult<T>.Invalid($"the output is JSON but not an object: {Kind(root)}", process);
            if (!root.TryGetProperty("schema_version", out var version)) return new JsonResult<T>.Invalid("the object has no schema_version", process);
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt64(out var found)) return new JsonResult<T>.Invalid($"schema_version is not a whole number: {Kind(version)}", process);
            if (found != schemaVersion) return new JsonResult<T>.Unsupported(found, schemaVersion, process);
            var value = root.Deserialize(typeInfo);
            return value is null ? new JsonResult<T>.Invalid("the object reads as nothing", process) : new JsonResult<T>.Ok(value, process);
        }
        catch (JsonException e)
        {
            return new JsonResult<T>.Invalid(e.Message, process);
        }
    }

    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "an object",
    };

    /// <summary>Why this request can't be run at all, or null when it can be tried.</summary>
    private static string? Refusal(ProcessRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Program)) return "Program is empty: there is nothing to run";
        if (request.Program.Contains('\0')) return "Program holds a null character";
        if (request.Arguments is null) return "Arguments is null";
        for (var i = 0; i < request.Arguments.Count; i++)
        {
            if (request.Arguments[i] is null) return $"Arguments[{i}] is null";
            if (request.Arguments[i].Contains('\0')) return $"Arguments[{i}] holds a null character, which no program can be given";
        }
        if (request.Timeout <= TimeSpan.Zero) return $"Timeout must be more than zero, not {request.Timeout}";
        if (request.Timeout.TotalMilliseconds > int.MaxValue) return $"Timeout must be at most {TimeSpan.FromMilliseconds(int.MaxValue).TotalDays:0} days, not {request.Timeout}";
        if (request.MaxOutputBytes < 0) return $"MaxOutputBytes must be zero or more, not {request.MaxOutputBytes}";
        if (request.Environment is { } environment)
            foreach (var (name, value) in environment)
            {
                if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.IndexOf('=', 1) >= 0) return $"Environment has a variable name that can't be used: '{name}'";
                if (value is not null && value.Contains('\0')) return $"Environment[{name}] holds a null character";
            }
        return null;
    }

    /// <summary>
    /// Reads a stream to its end, keeping the first <paramref name="cap"/> bytes and dropping the rest, so a child
    /// that floods never blocks on a full pipe and never costs more memory than the cap.
    /// </summary>
    private static (string Text, bool Cut) ReadCapped(Stream stream, int cap)
    {
        using var kept = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var cut = false;
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var room = (int)Math.Min(read, cap - kept.Length);
                if (room > 0) kept.Write(buffer, 0, room);
                if (room < read) cut = true;
            }
        }
        catch (IOException)
        {
            // The pipe broke: the writers are gone, which is this stream's end. What was read stands.
        }

        var bytes = kept.GetBuffer().AsSpan(0, (int)kept.Length);
        if (!cut) return (Encoding.UTF8.GetString(bytes), false);
        var text = Encoding.UTF8.GetString(bytes[..WholeCharacters(bytes)]);
        if (text.Length > 0 && !text.EndsWith('\n')) text += "\n";
        return (text + string.Format(System.Globalization.CultureInfo.InvariantCulture, CutMarker, cap), true);
    }

    /// <summary>The length of <paramref name="bytes"/> without a UTF-8 sequence the cut left unfinished at its end.</summary>
    private static int WholeCharacters(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= 4 && back <= bytes.Length; back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80) continue; // a continuation byte: its lead byte is further back
            var needs = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
            return needs > back ? bytes.Length - back : bytes.Length;
        }
        return bytes.Length;
    }

    /// <summary>
    /// Gives the child its input and closes stdin. False when the child ended, or closed stdin, before it had taken
    /// it all: that is the child's choice, and the call's result stands either way.
    /// </summary>
    private static bool WriteInput(Stream stream, string? text)
    {
        using (stream)
        {
            if (string.IsNullOrEmpty(text)) return true;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                return true;
            }
            catch (IOException)
            {
                // The child stopped reading: reported as InputDelivered false, not as a failure of the call.
                return false;
            }
        }
    }

    /// <summary>The command line <c>CommandLineToArgvW</c>, and every C runtime, reads back as exactly these arguments.</summary>
    internal static string CommandLine(string program, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Quote(line, program);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Quote(line, argument);
        }
        return line.ToString();
    }

    private static void Quote(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        for (var i = 0; i < argument.Length; i++)
        {
            var slashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                slashes++;
                i++;
            }

            if (i == argument.Length) line.Append('\\', slashes * 2); // before the closing quote: each doubled
            else if (argument[i] == '"') line.Append('\\', slashes * 2 + 1).Append('"'); // before a quote: doubled, and one to escape it
            else line.Append('\\', slashes).Append(argument[i]);
        }
        line.Append('"');
    }

    /// <summary>
    /// The full path of the program to start. A bare name is looked for on PATH only, never in the current folder,
    /// and a batch file is refused: it would be handed to cmd.exe, which reads its arguments by other rules.
    /// </summary>
    internal static string Resolve(string program)
    {
        string? found;
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar) || program.Contains(':'))
        {
            found = Path.GetFullPath(program);
            if (!File.Exists(found)) throw new FileNotFoundException($"{found} doesn't exist");
        }
        else
        {
            var names = Path.HasExtension(program) ? new[] { program } : [program + ".exe", program];
            found = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(Path.IsPathFullyQualified)
                .SelectMany(folder => names.Select(name => Path.Combine(folder, name)))
                .FirstOrDefault(File.Exists);
            if (found is null) throw new FileNotFoundException($"{program} isn't in any folder on PATH");
        }

        var extension = Path.GetExtension(found);
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{found} is a batch file, which only a shell can run; this runner starts programs without one");
        return found;
    }
}
