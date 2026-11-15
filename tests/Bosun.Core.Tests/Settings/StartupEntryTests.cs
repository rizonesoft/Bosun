using Xunit;

namespace Bosun.Core.Tests.Settings;

/// <summary>A Run key of the tests' own: values in memory, and each registry call able to fail.</summary>
public sealed class FakeRunKey : IRunKey
{
    public Dictionary<string, string> Values { get; } = [];

    /// <summary>What Windows' start-up record says of each entry; an entry with none is on.</summary>
    public Dictionary<string, bool> ApprovedBy { get; } = [];

    public Exception? FailRead { get; set; }

    public Exception? FailWrite { get; set; }

    /// <summary>A registry that takes a write and does not keep it.</summary>
    public bool ForgetWrites { get; set; }

    public int Writes { get; private set; }

    public string? Read(string name) => FailRead is null ? Values.GetValueOrDefault(name) : throw FailRead;

    public void Write(string name, string value)
    {
        if (FailWrite is not null) throw FailWrite;
        Writes++;
        if (!ForgetWrites) Values[name] = value;
    }

    public void Delete(string name)
    {
        if (FailWrite is not null) throw FailWrite;
        Writes++;
        if (!ForgetWrites) Values.Remove(name);
    }

    public bool? Approved(string name) => FailRead is null ? (ApprovedBy.TryGetValue(name, out var on) ? on : null) : throw FailRead;
}

/// <summary>
/// Start with Windows (D02 T02 §5): the Run value is the setting, read back after every change, and a value Windows
/// has turned off is not read as on.
/// </summary>
public class StartupEntryTests
{
    private const string Program = @"C:\Program Files\Bosun\Bosun.Panel.exe";

    [Fact]
    public void On_writes_the_panel_s_path_quoted_with_tray_and_off_removes_it_each_read_back()
    {
        var key = new FakeRunKey();
        var entry = new StartupEntry(key, Program);
        Assert.Equal(new StartupRead(StartupState.Off), entry.Read());
        Assert.False(entry.Read().Chosen);

        Assert.True(entry.Set(true).Succeeded);
        Assert.Equal("\"C:\\Program Files\\Bosun\\Bosun.Panel.exe\" --tray", key.Values["Bosun Panel"]);
        Assert.Equal(entry.Command, key.Values[StartupEntry.Name]);
        Assert.Equal(StartupState.On, entry.Read().State);
        Assert.True(entry.Read().Chosen);

        Assert.True(entry.Set(false).Succeeded);
        Assert.Empty(key.Values);
        Assert.Equal(StartupState.Off, entry.Read().State);

        // Other programs' entries are theirs.
        key.Values["Another program"] = "x";
        Assert.True(entry.Set(true).Succeeded);
        Assert.True(entry.Set(false).Succeeded);
        Assert.Equal("x", key.Values["Another program"]);
    }

    [Fact]
    public void An_entry_windows_has_turned_off_is_read_as_that_and_not_as_on()
    {
        var key = new FakeRunKey();
        var entry = new StartupEntry(key, Program);
        entry.Set(true);

        key.ApprovedBy[StartupEntry.Name] = false; // Settings, Apps, Startup: off
        var read = entry.Read();
        Assert.Equal(StartupState.DisabledByWindows, read.State);
        Assert.True(read.Chosen); // the person's choice stands; Windows is not acting on it

        key.ApprovedBy[StartupEntry.Name] = true;
        Assert.Equal(StartupState.On, entry.Read().State);
        key.ApprovedBy.Remove(StartupEntry.Name); // no record at all is on
        Assert.Equal(StartupState.On, entry.Read().State);

        key.Values.Clear(); // turned off by Windows, and with no value: just off
        key.ApprovedBy[StartupEntry.Name] = false;
        Assert.Equal(StartupState.Off, entry.Read().State);
    }

    [Fact]
    public void A_value_that_starts_another_copy_is_read_as_that_and_turning_it_on_points_it_here()
    {
        // The completion review: any value at all read as on, so after the panel moved the page said on while
        // Windows started the old path, or nothing.
        var key = new FakeRunKey();
        var entry = new StartupEntry(key, Program);
        const string old = "\"D:\\Old\\Bosun.Panel.exe\" --tray";
        key.Values[StartupEntry.Name] = old;

        var read = entry.Read();
        Assert.Equal((StartupState.OtherProgram, old), (read.State, read.Reason));
        Assert.True(read.Chosen); // the person did choose to start with Windows

        key.ApprovedBy[StartupEntry.Name] = false; // turned off by Windows as well: still the other program first
        Assert.Equal(StartupState.OtherProgram, entry.Read().State);
        key.ApprovedBy.Clear();

        Assert.True(entry.Set(true).Succeeded); // the repair: the same switch, written for this copy
        Assert.Equal(entry.Command, key.Values[StartupEntry.Name]);
        Assert.Equal(StartupState.On, entry.Read().State);

        // The same path in another case, or with a space after it, is this copy.
        key.Values[StartupEntry.Name] = entry.Command.ToUpperInvariant() + " ";
        Assert.Equal(StartupState.On, entry.Read().State);
    }

    [Fact]
    public void A_registry_that_cant_be_read_is_unavailable_with_the_reason_and_one_that_cant_be_written_says_why()
    {
        var key = new FakeRunKey { FailRead = new UnauthorizedAccessException("the key is locked by policy") };
        var entry = new StartupEntry(key, Program);

        var read = entry.Read();
        Assert.Equal(StartupState.Unavailable, read.State);
        Assert.Equal("The Windows start-up entry could not be read: the key is locked by policy", read.Reason);
        Assert.False(read.Chosen);

        key.FailRead = null;
        key.FailWrite = new System.Security.SecurityException("writing is not allowed");
        var on = entry.Set(true);
        Assert.False(on.Succeeded);
        Assert.Equal("Start with Windows could not be turned on: writing is not allowed", on.Reason);
        Assert.Equal("Start with Windows could not be turned off: writing is not allowed", entry.Set(false).Reason);
        Assert.Empty(key.Values);
    }

    [Fact]
    public void A_write_that_does_not_read_back_is_not_called_done()
    {
        var key = new FakeRunKey { ForgetWrites = true };
        var entry = new StartupEntry(key, Program);

        var outcome = entry.Set(true);

        Assert.False(outcome.Succeeded);
        Assert.Contains("did not read back", outcome.Reason);
        Assert.Equal(StartupState.Off, entry.Read().State);
    }
}
