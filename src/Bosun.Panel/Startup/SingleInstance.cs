using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace Bosun.Panel.Startup;

/// <summary>What a launch turned out to be.</summary>
public enum InstanceRole
{
    /// <summary>The only panel of this user and session: it holds the instance and listens for later launches.</summary>
    First,

    /// <summary>Another panel is running and acknowledged the request to come forward.</summary>
    Forwarded,

    /// <summary>Another panel holds the instance and did not acknowledge in the time given, tried twice.</summary>
    NotAnswering,
}

/// <summary>
/// One panel per user and logon session (D02 T02 §1). The first launch creates a named mutex and listens on a pipe;
/// a later launch finds the mutex, asks the first to come forward over the pipe, and goes away.
///
/// The first is whoever created the mutex, never whoever owns it: a panel that crashed holds no handle, so it never
/// blocks the next one and nothing waits on an abandoned mutex.
///
/// A request is acknowledged: the first answers only after the task of its <c>activated</c> callback has completed,
/// which its owner completes on the UI thread. A first panel whose UI is hung therefore reads as not answering, not
/// as forwarded. The callback is given when the instance is started, before anything is listened for, so no request
/// can be acknowledged that nobody took.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The one word a later launch sends, and the one the first answers with.</summary>
    public const string Request = "activate", Acknowledged = "ok";

    /// <summary>The most a request may be, and how long a connected client has to send it.</summary>
    public const int MaxRequestBytes = 64;
    public static readonly TimeSpan RequestWithin = TimeSpan.FromSeconds(2);

    /// <summary>How long a later launch waits for the first to acknowledge, per try.</summary>
    public static readonly TimeSpan DefaultAnswerWithin = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stop = new();
    private readonly string _pipe;
    private readonly TimeSpan _answerWithin;
    private Mutex? _mutex;
    private Task? _listening;

    private readonly Func<Task>? _activated;

    private SingleInstance(InstanceRole role, string pipe, TimeSpan answerWithin, Mutex? mutex, Func<Task>? activated)
    {
        Role = role;
        _pipe = pipe;
        _answerWithin = answerWithin;
        _mutex = mutex;
        _activated = activated;
        if (role == InstanceRole.First) _listening = Task.Run(() => ListenAsync(_stop.Token));
    }

    public InstanceRole Role { get; }

    /// <summary>The name of the mutex for an instance name: local to the logon session.</summary>
    public static string MutexName(string name) => @"Local\Bosun.Panel." + name;

    /// <summary>The name of the pipe for an instance name: pipes are machine-wide, so the session is in the name.</summary>
    public static string PipeName(string name) => $"Bosun.Panel.{name}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Becomes the first instance of <paramref name="name"/>, or asks the one there is to come forward. When that one
    /// does not acknowledge within <paramref name="answerWithin"/>, one more try is made to be the first (it may have
    /// been on its way out), and after that the answer is <see cref="InstanceRole.NotAnswering"/>.
    ///
    /// <paramref name="activated"/> is what the first does when a later launch asks it to come forward: its task
    /// completes when the request has been taken (on the UI thread, by the panel), and only then is the later launch
    /// told it was heard. A first started without one acknowledges nothing.
    /// </summary>
    public static SingleInstance Start(string name, TimeSpan? answerWithin = null, Func<Task>? activated = null)
    {
        var within = answerWithin ?? DefaultAnswerWithin;
        var pipe = PipeName(name);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var mutex = new Mutex(initiallyOwned: false, MutexName(name), out var created);
            if (created) return new SingleInstance(InstanceRole.First, pipe, within, mutex, activated);
            mutex.Dispose();
            if (Forward(pipe, within)) return new SingleInstance(InstanceRole.Forwarded, pipe, within, null, null);
        }
        return new SingleInstance(InstanceRole.NotAnswering, pipe, within, null, null);
    }

    /// <summary>Asks the first instance to come forward; true when it acknowledged in time.</summary>
    private static bool Forward(string pipe, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect(Math.Max(1, (int)within.TotalMilliseconds));

            // Windows refuses a background process the foreground. This process was just started by the person, so
            // it may hand that right to the panel that is about to come forward.
            if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var first)) AllowSetForegroundWindow(first);

            var left = within - clock.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            using var limit = new CancellationTokenSource(left);
            client.WriteAsync(Encoding.ASCII.GetBytes(Request + "\n"), limit.Token).AsTask().GetAwaiter().GetResult();
            client.FlushAsync(limit.Token).GetAwaiter().GetResult();
            return ReadWordAsync(client, limit.Token).GetAwaiter().GetResult() == Acknowledged;
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException or ObjectDisposedException or Win32Exception)
        {
            // No listener, a listener that went away, one that is not this user's, or one that did not answer in time.
            return false;
        }
    }

    /// <summary>
    /// The first instance's listener. Everything a client sends is hostile until it is the one word: at most
    /// <see cref="MaxRequestBytes"/> are read, for at most <see cref="RequestWithin"/>, and a client that sends nothing,
    /// too much or anything else is dropped, with the listener carrying on.
    /// </summary>
    private async Task ListenAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipe, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);

                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop);
                limit.CancelAfter(RequestWithin);
                if (await ReadWordAsync(server, limit.Token).ConfigureAwait(false) != Request) continue;

                // Taken by the panel, on its UI thread, before the launch is told so. A UI that never takes it is
                // given up on after the time a launch waits, so one hung request can't hold the listener for ever.
                // With nobody to take it, it is not acknowledged: the launch reads that as not answering.
                if (_activated is null) continue;
                await _activated().WaitAsync(_answerWithin + RequestWithin, stop).ConfigureAwait(false);

                using var reply = CancellationTokenSource.CreateLinkedTokenSource(stop);
                reply.CancelAfter(RequestWithin);
                await server.WriteAsync(Encoding.ASCII.GetBytes(Acknowledged + "\n"), reply.Token).ConfigureAwait(false);
                await server.FlushAsync(reply.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or TimeoutException or ObjectDisposedException or UnauthorizedAccessException)
            {
                // That client is dropped (it went away, sent nothing in time, or the panel did not take the request);
                // the next one is listened for.
            }
            catch (Exception) when (!stop.IsCancellationRequested)
            {
                // The panel could not take the request (its callback threw, or its task failed). That failure is the
                // panel's to report where it happened: PanelStartup lets it reach the UI thread's crash handler. Here
                // the launch is simply not acknowledged, and the listener outlives it, or one bad activation would
                // end single-instance for the session.
            }
        }
    }

    /// <summary>One word of at most <see cref="MaxRequestBytes"/> ASCII bytes, ended by a newline or the end of the stream; null when it is longer.</summary>
    private static async Task<string?> ReadWordAsync(Stream stream, CancellationToken limit)
    {
        var buffer = new byte[MaxRequestBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), limit).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
            var end = Array.IndexOf(buffer, (byte)'\n', 0, read);
            if (end >= 0) return Encoding.ASCII.GetString(buffer, 0, end).Trim();
        }
        // The stream ended (a word with no newline), or the buffer is full with no newline in it: too long to be a word.
        return read < buffer.Length ? Encoding.ASCII.GetString(buffer, 0, read).Trim() : null;
    }

    /// <summary>
    /// Gives the instance up. The mutex goes first, so a launch that was waiting on this one finds the name free the
    /// moment its connection breaks; then the listener is stopped.
    /// </summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _mutex, null)?.Dispose();
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        // Waited for, never observed: WaitAny returns when the listener has ended or the time is up, and throws for
        // neither, so giving the instance up can't fail on what the listener was doing.
        if (Interlocked.Exchange(ref _listening, null) is { } listening) Task.WaitAny([listening], TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
