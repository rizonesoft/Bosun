using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Bosun.Panel.Startup;

namespace Bosun.Panel.Tests;

/// <summary>A folder of a test's own under the system's temporary folder, removed when the test ends.</summary>
sealed class TempFolder : IDisposable
{
    public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bosun-panel-test-" + Guid.NewGuid().ToString("N"))).FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something still holds a file in it: the system's own clean-up takes the folder later.
        }
    }
}

/// <summary>What the startup tests share: names nothing else uses, the built programs, and a host that records.</summary>
static class StartupSupport
{
    /// <summary>An instance name of this test's own: never the person's panel, never another test's.</summary>
    public static string Name() => "test-" + Guid.NewGuid().ToString("N");

    /// <summary>A built program beside the tests' own output: build/dotnet/bin/&lt;project&gt;/&lt;configuration&gt;/.</summary>
    public static string Built(string project, string file)
    {
        var mine = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        var path = System.IO.Path.Combine(mine.Parent!.Parent!.FullName, project, mine.Name, file);
        return System.IO.File.Exists(path) ? path : throw new FileNotFoundException($"{file} is not built beside the tests", path);
    }

    /// <summary>Runs this thread's dispatcher until <paramref name="done"/> or the limit; false when the limit passed.</summary>
    public static bool Pump(Func<bool> done, TimeSpan? limit = null)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > (limit ?? TimeSpan.FromSeconds(10))) return false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        return true;
    }
}

/// <summary>A startup host that makes no window and shows no message: it writes down what it was asked.</summary>
sealed class RecordingHost
{
    public List<string> Told { get; } = [];
    public List<string> Asked { get; } = [];
    public List<int> Exits { get; } = [];
    public int WindowsMade { get; private set; }
    public List<int> BroughtForwardOn { get; } = [];
    public int Restarts { get; private set; }

    /// <summary>The answer to the crash notice's question.</summary>
    public bool StartAgain { get; set; }

    /// <summary>What a restart does, after it is counted.</summary>
    public Action OnRestart { get; set; } = () => { };

    public StartupHost Host(string? instanceVariable, string dataFolder, TimeSpan? answerWithin = null) => new()
    {
        Dispatcher = Dispatcher.CurrentDispatcher,
        DataFolder = dataFolder,
        InstanceVariable = instanceVariable,
        AnswerWithin = answerWithin ?? TimeSpan.FromSeconds(5),
        MakeWindow = () => WindowsMade++,
        BringForward = () => BroughtForwardOn.Add(Environment.CurrentManagedThreadId),
        Tell = Told.Add,
        Ask = text =>
        {
            Asked.Add(text);
            return StartAgain;
        },
        Restart = () =>
        {
            Restarts++;
            OnRestart();
        },
        Exit = Exits.Add,
    };
}
