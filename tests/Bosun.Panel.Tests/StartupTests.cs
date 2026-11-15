using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Bosun.Core;
using Bosun.Panel.Startup;
using Xunit;

namespace Bosun.Panel.Tests;

/// <summary>
/// One panel per user and session (D02 T02 §1): the first launch holds the instance, a later one asks it to come
/// forward and goes away, and a launch that can't do either says so. Every test uses an instance name of its own, so
/// none of them meets the panel the person is running, or each other.
/// </summary>
public class StartupTests
{
    private static readonly ProcessRunner Runner = new();

    private static Func<Task> Counting(int[] count) => () =>
    {
        Interlocked.Increment(ref count[0]);
        return Task.CompletedTask;
    };

    // ---- the instance ----

    [Fact]
    public void The_first_launch_is_first_a_second_is_forwarded_and_the_name_is_free_again_after()
    {
        var name = StartupSupport.Name();
        var activations = new int[1];
        using (var first = SingleInstance.Start(name, activated: Counting(activations)))
        {
            Assert.Equal(InstanceRole.First, first.Role);

            using var second = SingleInstance.Start(name);
            Assert.Equal(InstanceRole.Forwarded, second.Role);
            Assert.Equal(1, activations[0]);

            using var third = SingleInstance.Start(name);
            Assert.Equal(InstanceRole.Forwarded, third.Role);
            Assert.Equal(2, activations[0]);
        }

        using var after = SingleInstance.Start(name);
        Assert.Equal(InstanceRole.First, after.Role);
    }

    [Fact]
    public void A_first_whose_ui_never_takes_the_request_is_not_answering_not_forwarded()
    {
        var name = StartupSupport.Name();
        var asked = 0;
        using var first = SingleInstance.Start(name, activated: () =>
        {
            Interlocked.Increment(ref asked);
            return new TaskCompletionSource().Task; // the UI thread is hung: the request is never taken
        });

        var clock = Stopwatch.StartNew();
        using var second = SingleInstance.Start(name, TimeSpan.FromMilliseconds(300));

        Assert.Equal(InstanceRole.NotAnswering, second.Role);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"it gave up after {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.True(asked >= 1, "the request did reach the first: it was the acknowledgement that never came");
    }

    [Fact]
    public async Task A_first_that_exits_while_a_second_is_waiting_is_taken_over()
    {
        var name = StartupSupport.Name();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = SingleInstance.Start(name, activated: () =>
        {
            reached.TrySetResult();
            return new TaskCompletionSource().Task;
        });

        var waiting = Task.Run(() => SingleInstance.Start(name, TimeSpan.FromSeconds(8)), TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        first.Dispose(); // the first panel exits

        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.Equal(InstanceRole.First, second.Role);
    }

    [Fact]
    public void A_first_with_nobody_to_take_a_request_acknowledges_none()
    {
        // Round 1: the listener runs from the moment the instance exists, and it used to acknowledge a request it had
        // nobody to give to, so a launch could exit 0 having brought nothing forward. The callback is given with the
        // start now; a first without one never says it was heard.
        var name = StartupSupport.Name();
        using var first = SingleInstance.Start(name);
        Assert.Equal(InstanceRole.First, first.Role);

        using var second = SingleInstance.Start(name, TimeSpan.FromMilliseconds(300));
        Assert.Equal(InstanceRole.NotAnswering, second.Role);
        Assert.Equal("", Raw(name, Encoding.ASCII.GetBytes(SingleInstance.Request + "\n")));
    }

    [Fact]
    public void The_holder_of_the_name_with_no_listener_is_not_answering_within_the_time_given()
    {
        var name = StartupSupport.Name();
        using var held = new Mutex(false, SingleInstance.MutexName(name)); // a panel that holds the name and listens to nothing

        var clock = Stopwatch.StartNew();
        using var second = SingleInstance.Start(name, TimeSpan.FromMilliseconds(300));

        Assert.Equal(InstanceRole.NotAnswering, second.Role);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"it gave up after {clock.Elapsed.TotalSeconds:0.0} s");
        // And it left nothing listening itself.
        using var probe = new NamedPipeClientStream(".", SingleInstance.PipeName(name), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        Assert.Throws<TimeoutException>(() => probe.Connect(200));
    }

    /// <summary>A client that is not a panel: connects, sends what it is given, and reports what came back.</summary>
    private static string Raw(string name, byte[] send)
    {
        using var client = new NamedPipeClientStream(".", SingleInstance.PipeName(name), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        client.Connect(5000);
        try
        {
            if (send.Length > 0)
            {
                client.Write(send, 0, send.Length);
                client.Flush();
            }
            var reply = new byte[16];
            var read = client.Read(reply, 0, reply.Length); // returns 0 when the listener drops the client
            return Encoding.ASCII.GetString(reply, 0, read).Trim();
        }
        catch (IOException)
        {
            return ""; // dropped while this client was still writing
        }
    }

    [Fact]
    public void A_client_that_sends_nothing_too_much_or_another_word_is_dropped_and_the_listener_carries_on()
    {
        var name = StartupSupport.Name();
        var activations = new int[1];
        using var first = SingleInstance.Start(name, activated: Counting(activations));

        void StillListening(int expected)
        {
            using var launch = SingleInstance.Start(name);
            Assert.Equal(InstanceRole.Forwarded, launch.Role);
            Assert.Equal(expected, activations[0]);
        }

        var clock = Stopwatch.StartNew();
        Assert.Equal("", Raw(name, [])); // silent: dropped when its two seconds are up, not waited on for ever
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"the silent client was held for {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(0, activations[0]);
        StillListening(1);

        Assert.Equal("", Raw(name, Encoding.ASCII.GetBytes(new string('a', 1024 * 1024)))); // a megabyte with no newline
        Assert.Equal(1, activations[0]);
        StillListening(2);

        Assert.Equal("", Raw(name, Encoding.ASCII.GetBytes("quit\n")));
        Assert.Equal("", Raw(name, Encoding.ASCII.GetBytes("activate now\n")));
        Assert.Equal("", Raw(name, Encoding.ASCII.GetBytes(new string('a', SingleInstance.MaxRequestBytes) + "\n")));
        Assert.Equal(2, activations[0]);
        StillListening(3);

        // And the word itself, from a client that is not a panel, is the request.
        Assert.Equal(SingleInstance.Acknowledged, Raw(name, Encoding.ASCII.GetBytes(SingleInstance.Request + "\n")));
        Assert.Equal(4, activations[0]);
    }

    // ---- the name ----

    [Fact]
    public void The_instance_is_named_by_the_users_sid_or_by_the_variable_and_a_value_that_cant_be_a_name_is_refused()
    {
        Assert.True(InstanceName.TryResolve(null, out var name, out _));
        Assert.StartsWith("S-1-", name);
        Assert.Equal(InstanceName.Sid(), name);
        Assert.True(InstanceName.TryResolve("", out name, out _));
        Assert.Equal(InstanceName.Sid(), name);

        Assert.True(InstanceName.TryResolve("checkout-2.test", out name, out _));
        Assert.Equal("checkout-2.test", name);

        foreach (var bad in new[] { @"Global\panel", "a/b", "two words", new string('a', 65), "ünï" })
        {
            Assert.False(InstanceName.TryResolve(bad, out name, out var refusal), bad);
            Assert.Equal("", name);
            Assert.Contains(InstanceName.Variable, refusal);
        }
    }

    // ---- the application's start, as App runs it ----

    [Fact]
    public void The_first_launch_makes_its_window_once_and_comes_forward_on_the_ui_thread_when_a_later_launch_asks()
    {
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        Sta.Run(() =>
        {
            var host = new RecordingHost();
            using var startup = new PanelStartup(host.Host(name, data.Path));

            Assert.True(startup.Begin());
            Assert.Equal(InstanceRole.First, startup.Role);
            Assert.Equal(1, host.WindowsMade);
            Assert.Empty(host.Exits);
            Assert.Empty(host.Told);

            // A later launch, from another thread as from another process; this thread runs its dispatcher meanwhile.
            var later = Task.Run(() =>
            {
                using var launch = SingleInstance.Start(name);
                return launch.Role;
            });
            Assert.True(StartupSupport.Pump(() => later.IsCompleted), "the later launch never returned");
            Assert.Equal(InstanceRole.Forwarded, later.Result);
            Assert.Equal([Environment.CurrentManagedThreadId], host.BroughtForwardOn);
            Assert.Equal(1, host.WindowsMade); // brought forward, not made again
        });
    }

    [Fact]
    public void A_window_that_cant_be_brought_forward_is_a_crash_on_the_ui_thread_and_the_launch_is_not_told_it_was_heard()
    {
        // Round 1: the exception used to stay in a task nobody reported. It reaches the crash handler now.
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        Sta.Run(() =>
        {
            var host = new RecordingHost();
            var stock = host.Host(name, data.Path);
            using var startup = new PanelStartup(stock with { BringForward = () => throw new InvalidOperationException("the window is gone") });
            Assert.True(startup.Begin());

            var later = Task.Run(() =>
            {
                using var launch = SingleInstance.Start(name, TimeSpan.FromMilliseconds(500));
                return launch.Role;
            });
            Assert.True(StartupSupport.Pump(() => later.IsCompleted && host.Exits.Count > 0), "the crash was never handled");

            Assert.NotEqual(InstanceRole.Forwarded, later.Result);
            Assert.Equal([1], host.Exits);
            // The launch asks twice (its second try), so the window fails twice: each is reported, and the person is
            // asked once and the panel exits once, not once per exception.
            var reports = Directory.GetFiles(PanelPaths.Crash(data.Path));
            Assert.InRange(reports.Length, 1, 2);
            Assert.All(reports, report =>
            {
                var text = File.ReadAllText(report);
                Assert.Contains("Caught by: UI thread", text, StringComparison.Ordinal);
                Assert.Contains("the window is gone", text, StringComparison.Ordinal);
            });
            var question = Assert.Single(host.Asked);
            Assert.Contains(reports, report => question.Contains(report, StringComparison.Ordinal));
        });
    }

    [Fact]
    public void A_later_launch_is_handed_on_and_exits_0_without_a_notice_or_a_window()
    {
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        var activations = new int[1];
        using var running = SingleInstance.Start(name, activated: Counting(activations));

        Sta.Run(() =>
        {
            var host = new RecordingHost();
            using var startup = new PanelStartup(host.Host(name, data.Path));

            Assert.False(startup.Begin());
            Assert.Equal(InstanceRole.Forwarded, startup.Role);
            Assert.Equal([PanelStartup.ExitForwarded], host.Exits);
            Assert.Equal(0, host.Exits[0]);
            Assert.Empty(host.Told);
            Assert.Empty(host.Asked);
            Assert.Equal(0, host.WindowsMade);
        });
        Assert.Equal(1, activations[0]);
    }

    [Fact]
    public void A_panel_that_is_running_but_not_answering_is_said_so_and_the_launch_exits_3_without_a_window()
    {
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        using var held = new Mutex(false, SingleInstance.MutexName(name));

        Sta.Run(() =>
        {
            var host = new RecordingHost();
            using var startup = new PanelStartup(host.Host(name, data.Path, TimeSpan.FromMilliseconds(200)));

            Assert.False(startup.Begin());
            Assert.Equal(InstanceRole.NotAnswering, startup.Role);
            Assert.Equal([3], host.Exits);
            var notice = Assert.Single(host.Told);
            Assert.Contains("already running", notice);
            Assert.Contains("not answering", notice);
            Assert.Contains("Task Manager", notice); // what to do about it
            Assert.Equal(0, host.WindowsMade);
        });
    }

    [Fact]
    public void An_instance_variable_that_cant_be_a_name_is_said_so_and_the_launch_exits_2_holding_nothing()
    {
        using var data = new TempFolder();
        Sta.Run(() =>
        {
            var host = new RecordingHost();
            using var startup = new PanelStartup(host.Host(@"Global\panel", data.Path));

            Assert.False(startup.Begin());
            Assert.Null(startup.Role); // no instance was asked for, so none is held
            Assert.Equal([2], host.Exits);
            Assert.Contains(InstanceName.Variable, Assert.Single(host.Told));
            Assert.Equal(0, host.WindowsMade);
        });
    }

    // ---- the real program ----

    [Fact]
    public async Task The_real_panel_started_while_one_is_running_hands_on_and_exits_0()
    {
        using var data = new TempFolder();
        var name = StartupSupport.Name();
        var activations = new int[1];
        using var running = SingleInstance.Start(name, activated: Counting(activations));

        // The runner ends the process, and everything it started, if it is still there at the limit.
        var request = new ProcessRequest(StartupSupport.Built("Bosun.Panel", "Bosun.Panel.exe"), [])
        {
            Environment = new Dictionary<string, string?> { [InstanceName.Variable] = name, [PanelPaths.Variable] = data.Path },
            Timeout = TimeSpan.FromSeconds(10),
        };
        var result = await Runner.RunAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(ProcessOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, activations[0]);
        Assert.False(Directory.Exists(PanelPaths.Crash(data.Path)), "it left no crash report");
    }

    // ---- the manifest ----

    [Fact]
    public void The_manifest_runs_the_panel_as_the_person_who_started_it_with_per_monitor_dpi()
    {
        var text = File.ReadAllText(Path.Combine(TestPaths.Panel(), "app.manifest"));
        AssertManifest(text);

        // And the program that was built carries it: a manifest beside the project that nothing embeds does nothing.
        AssertManifest(EmbeddedManifest(StartupSupport.Built("Bosun.Panel", "Bosun.Panel.exe")));
    }

    private static void AssertManifest(string text)
    {
        var manifest = XDocument.Parse(text);
        var level = manifest.Descendants().Single(e => e.Name.LocalName == "requestedExecutionLevel");
        Assert.Equal("asInvoker", (string?)level.Attribute("level"));
        Assert.Equal("false", (string?)level.Attribute("uiAccess"));
        // Not in a comment that could be uncommented, not anywhere an element could carry it.
        Assert.DoesNotContain(manifest.Descendants().Attributes(), a => a.Value.Contains("requireAdministrator", StringComparison.OrdinalIgnoreCase));
        var awareness = manifest.Descendants().Single(e => e.Name.LocalName == "dpiAwareness").Value;
        Assert.StartsWith("permonitorv2", awareness.Trim(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(manifest.Descendants(), e => e.Name.LocalName == "supportedOS" && (string?)e.Attribute("Id") == "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}");
        Assert.Contains(manifest.Descendants(), e => e.Name.LocalName == "assemblyIdentity" && (string?)e.Attribute("name") == "Microsoft.Windows.Common-Controls" && (string?)e.Attribute("version") == "6.0.0.0");
    }

    /// <summary>The manifest resource (type 24, id 1) inside a program file, as Windows reads it at start.</summary>
    private static string EmbeddedManifest(string program)
    {
        const uint asData = 0x00000002; // LOAD_LIBRARY_AS_DATAFILE: mapped, never run
        var module = LoadLibraryEx(program, IntPtr.Zero, asData);
        Assert.True(module != IntPtr.Zero, $"{program} could not be opened: error {Marshal.GetLastWin32Error()}");
        try
        {
            var found = FindResource(module, new IntPtr(1), new IntPtr(24));
            Assert.True(found != IntPtr.Zero, $"{program} has no manifest resource");
            var bytes = new byte[SizeofResource(module, found)];
            Marshal.Copy(LockResource(LoadResource(module, found)), bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        }
        finally
        {
            FreeLibrary(module);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr loaded);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);
}
