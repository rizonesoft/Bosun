using System.IO;
using System.Windows.Threading;
using Bosun.Core;
using Bosun.Panel.Settings;
using Bosun.Panel.Shell;
using Bosun.Panel.Theme;

namespace Bosun.Panel.Tests.Settings;

/// <summary>The settings store's hands on a disk that is only memory, each able to fail: no test leaves a file anywhere.</summary>
public sealed class MemoryFiles : PanelSettingsFiles
{
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, every write fails with it: a full disk, a read-only folder.</summary>
    public Exception? FailWrite { get; set; }

    /// <summary>When set, every read fails with it: a file held by another program.</summary>
    public Exception? FailRead { get; set; }

    public override byte[] Read(string path, int limit) =>
        FailRead is not null ? throw FailRead : Files.TryGetValue(path, out var bytes) ? bytes[..(int)Math.Min(bytes.Length, (long)limit + 1)] : throw new FileNotFoundException("no such file", path);

    public override void Write(string path, byte[] bytes)
    {
        if (FailWrite is not null) throw FailWrite;
        Files[path] = bytes;
    }

    public override void WriteNew(string path, byte[] bytes)
    {
        if (FailWrite is not null) throw FailWrite;
        if (!Files.TryAdd(path, bytes)) throw new IOException("the file exists");
    }

    public override void Replace(string from, string to)
    {
        if (FailWrite is not null) throw FailWrite;
        Files[to] = Files[from];
        Files.Remove(from);
    }

    public override void Delete(string path) => Files.Remove(path);

    public void Put(string path, string text) => Files[path] = System.Text.Encoding.UTF8.GetBytes(text);
}

/// <summary>A Run key of the tests' own: values in memory, and each registry call able to fail. The real Run key is never touched.</summary>
public sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = [];

    public Dictionary<string, bool> ApprovedBy { get; } = [];

    public Exception? FailRead { get; set; }

    public Exception? FailWrite { get; set; }

    public string? Read(string name) => FailRead is null ? Values.GetValueOrDefault(name) : throw FailRead;

    public void Write(string name, string value)
    {
        if (FailWrite is not null) throw FailWrite;
        Values[name] = value;
    }

    public void Delete(string name)
    {
        if (FailWrite is not null) throw FailWrite;
        Values.Remove(name);
    }

    /// <summary>A record as the registry would hold it, decoded as the real key decodes it: wins over <c>ApprovedBy</c>.</summary>
    public Dictionary<string, object> RawApproval { get; } = [];

    public Bosun.Core.Optimise.StartupApproval Approval(string name) => FailRead is not null ? throw FailRead
        : RawApproval.TryGetValue(name, out var raw) ? Bosun.Core.Optimise.StartupApproval.FromValue(raw)
        : ApprovedBy.TryGetValue(name, out var on) ? new Bosun.Core.Optimise.StartupApproval(on ? Bosun.Core.Optimise.ApprovalState.On : Bosun.Core.Optimise.ApprovalState.TurnedOffByWindows)
        : Bosun.Core.Optimise.StartupApproval.NoRecord;
}

/// <summary>Windows' theme as a test says it is.</summary>
public sealed class FakeWindowsTheme : IWindowsTheme
{
    public bool IsLight { get; set; }

    public event Action? Changed;

    /// <summary>Windows says its theme changed, on the calling thread.</summary>
    public void Flip()
    {
        IsLight = !IsLight;
        Changed?.Invoke();
    }
}

/// <summary>A Settings page over a settings file and a Run key that are only memory, with what the tests read back.</summary>
public sealed class SettingsFixture
{
    public const string FilePath = @"X:\src\bosun-test\panel.json";
    public const string Program = @"C:\Program Files\Bosun\Bosun.Panel.exe";

    public MemoryFiles Files { get; } = new();
    public FakeRunKey Key { get; } = new();
    public PanelSettingsStore Store { get; private set; } = null!;
    public StartupEntry Startup { get; private set; } = null!;
    public ActivityLog Log { get; private set; } = null!;
    public SettingsPage Page { get; private set; } = null!;
    public int StartupSettingsOpened { get; private set; }

    /// <summary>Makes the store and the page, on this thread, over whatever the files and the key hold now.</summary>
    public SettingsFixture Open()
    {
        Store = new PanelSettingsStore(FilePath, Files);
        Startup = new StartupEntry(Key, Program);
        var tick = 0;
        Log = new ActivityLog(new UiLogSink(Dispatcher.CurrentDispatcher, () => new DateTime(2026, 11, 15, 9, 10, 0, DateTimeKind.Local).AddMinutes(tick++)));
        Page = new SettingsPage(Store, Startup, Log, () => StartupSettingsOpened++);
        return this;
    }

    /// <summary>The panel's own shell over these settings, as the application makes it.</summary>
    public ShellViewModel Shell()
    {
        Store = new PanelSettingsStore(FilePath, Files);
        Startup = new StartupEntry(Key, Program);
        var shell = WindowStart.NewShell(Dispatcher.CurrentDispatcher, Store, Startup, () => StartupSettingsOpened++);
        Log = shell.Log;
        Page = (SettingsPage)shell.Sections.Single(section => section.Key == "settings").Page;
        return shell;
    }

    /// <summary>What the settings file holds now, read as a new panel would read it.</summary>
    public PanelSettings OnDisk() => new PanelSettingsStore(FilePath, Files).Current;

    public ThemeOption Option(ThemeChoice choice) => Page.ThemeOptions.Single(option => option.Value == choice);
}
