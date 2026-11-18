using System.IO;
using System.Text;
using Bosun.Core.Optimise;
using Bosun.Panel.Optimise;
using Xunit;

namespace Bosun.Panel.Tests.Optimise;

/// <summary>A registry of the tests' own: keys by hive, view and path, each able to refuse. It never touches the real one.</summary>
internal sealed class FakeRegistry : IRegistrySource
{
    public Dictionary<(RunHive, RunView, string), Dictionary<string, object?>> Keys { get; } = [];

    public HashSet<(RunHive, RunView, string)> Denied { get; } = [];

    public List<(RunHive, RunView, string)> Opened { get; } = [];

    public Dictionary<string, object?> Key(RunHive hive, RunView view, string path) =>
        Keys[(hive, view, path)] = Keys.GetValueOrDefault((hive, view, path)) ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?>? ReadKey(RunHive hive, RunView view, string path)
    {
        Opened.Add((hive, view, path));
        if (Denied.Contains((hive, view, path))) throw new UnauthorizedAccessException($"Access to {hive} {view} {path} is denied.");
        return Keys.GetValueOrDefault((hive, view, path));
    }
}

internal sealed class FakeFiles : IProfileFiles
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Exception? Fail { get; set; }

    public List<string> Read { get; } = [];

    public string? ReadText(string path)
    {
        Read.Add(path);
        if (Fail is not null) throw Fail;
        return Files.GetValueOrDefault(path);
    }
}

internal sealed class FakeFirmware : IFirmwareTable
{
    public byte[]? Table { get; set; }

    public Exception? Fail { get; set; }

    public byte[]? ReadSmbios() => Fail is null ? Table : throw Fail;
}

/// <summary>
/// The four facts read on Windows (D05 T02 §1), on controlled registry, file and firmware inputs: exact values, one
/// refusal keeping the other three, and an entry's hive, view and approval kept.
/// </summary>
public class WindowsDiagnosticsTests
{
    private const string Profile = @"C:\Users\Test";
    private const string Part = "CMG16GX4M2D3600C18";
    private static readonly string Mm = WindowsDiagnostics.MemoryManagement;

    private sealed class Machine
    {
        public FakeRegistry Registry { get; } = new();

        public FakeFiles Files { get; } = new();

        public FakeFirmware Firmware { get; } = new();

        public Machine()
        {
            var mm = Registry.Key(RunHive.LocalMachine, RunView.Registry64, Mm);
            mm["PagingFiles"] = new[] { @"C:\pagefile.sys 32768 51200" };
            mm["ExistingPageFiles"] = new[] { @"\??\C:\pagefile.sys" };
            mm["TempPageFile"] = 0;
            Files.Files[Path.Combine(Profile, ".wslconfig")] = "[wsl2]\nmemory=12GB\n";
            Firmware.Table = SmbiosFixture.Table(
                SmbiosFixture.Type17(8192, "DIMM0", "BANK 0", "Corsair", Part, 2133, 2133),
                SmbiosFixture.Type17(8192, "DIMM1", "BANK 0", "Corsair", Part, 2133, 2133));
            Registry.Key(RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.Run)["Discord"] = @"C:\Discord\Update.exe --processStart Discord.exe";
        }

        public WindowsDiagnostics Reader => new(Registry, Files, Firmware, Profile);
    }

    [Fact]
    public void Each_of_the_four_facts_is_read_to_its_exact_value()
    {
        var m = new Machine();
        var report = m.Reader.Read();

        var pagefile = report.Pagefile.Value!;
        Assert.Equal(new PagefileConfig(@"C:\pagefile.sys", 32768, 51200, false), Assert.Single(pagefile.Configured));
        Assert.Equal([@"C:\pagefile.sys"], pagefile.Active);
        Assert.False(pagefile.TemporaryInUse);
        Assert.False(pagefile.PendingRestart);

        Assert.Equal(new WslCap(12288), report.WslCap.Value);
        Assert.Equal(
            [new MemoryModule("DIMM0", "BANK 0", "Corsair", Part, 8192, 2133, 2133), new MemoryModule("DIMM1", "BANK 0", "Corsair", Part, 8192, 2133, 2133)],
            report.Memory.Value);
        var item = Assert.Single(report.Startup.Value!.Items);
        Assert.Equal(new StartupItem("Discord", @"C:\Discord\Update.exe --processStart Discord.exe", RunHive.CurrentUser, RunView.Registry64, StartupApproval.NoRecord), item);
        Assert.NotNull(report.RamFinding);
    }

    [Fact]
    public void The_wslconfig_is_read_as_the_profile_s_file_and_nothing_else_is_asked_for()
    {
        var m = new Machine();
        m.Reader.Read();
        Assert.Equal([Path.Combine(Profile, ".wslconfig")], m.Files.Read);

        m.Files.Files.Clear();
        Assert.True(m.Reader.Read().WslCap.Value!.NoCapSet);
    }

    [Fact]
    public void A_pending_pagefile_move_and_a_temporary_file_are_read_apart_from_the_configuration()
    {
        var m = new Machine();
        var mm = m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, Mm);
        mm["ExistingPageFiles"] = new[] { @"\??\D:\pagefile.sys" };
        mm["TempPageFile"] = 1;
        var p = m.Reader.Read().Pagefile.Value!;
        Assert.True(p.PendingRestart);
        Assert.True(p.TemporaryInUse);
        Assert.Equal(@"C:\pagefile.sys", p.Configured[0].Path);
        Assert.Equal([@"D:\pagefile.sys"], p.Active);
    }

    [Fact]
    public void A_missing_value_is_an_empty_configuration_and_a_missing_key_is_a_reason()
    {
        var m = new Machine();
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, Mm).Clear();
        var empty = m.Reader.Read().Pagefile.Value!;
        Assert.True(empty.IsEmpty);
        Assert.Empty(empty.Active);

        m.Registry.Keys.Remove((RunHive.LocalMachine, RunView.Registry64, Mm));
        Assert.Contains("Memory Management key is not in the registry", m.Reader.Read().Pagefile.Reason);
    }

    [Fact]
    public void A_denied_registry_keeps_the_other_three_facts()
    {
        var m = new Machine();
        m.Registry.Denied.Add((RunHive.LocalMachine, RunView.Registry64, Mm));
        var report = m.Reader.Read();
        Assert.Contains("denied", report.Pagefile.Reason);
        Assert.Equal(12288, report.WslCap.Value!.Mb);
        Assert.Equal(2, report.Memory.Value!.Count);
        Assert.Single(report.Startup.Value!.Items);
    }

    [Fact]
    public void A_denied_wslconfig_keeps_the_other_three_facts()
    {
        var m = new Machine();
        m.Files.Fail = new UnauthorizedAccessException("the file is locked");
        var report = m.Reader.Read();
        Assert.Contains("locked", report.WslCap.Reason);
        Assert.True(report.Pagefile.IsRead);
        Assert.Equal(2, report.Memory.Value!.Count);
        Assert.Single(report.Startup.Value!.Items);
    }

    [Fact]
    public void A_refused_firmware_table_keeps_the_other_three_facts()
    {
        var m = new Machine();
        m.Firmware.Fail = new IOException("no table");
        var report = m.Reader.Read();
        Assert.Contains("no table", report.Memory.Reason);
        Assert.Null(report.RamFinding);
        Assert.True(report.Pagefile.IsRead);
        Assert.Equal(12288, report.WslCap.Value!.Mb);
        Assert.Single(report.Startup.Value!.Items);
    }

    [Fact]
    public void Every_run_key_refused_keeps_the_other_three_facts()
    {
        var m = new Machine();
        m.Registry.Denied.Add((RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.Run));
        m.Registry.Denied.Add((RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run));
        m.Registry.Denied.Add((RunHive.LocalMachine, RunView.Registry32, WindowsDiagnostics.Run));
        var report = m.Reader.Read();
        Assert.False(report.Startup.IsRead);
        Assert.Contains("HKCU Run could not be read", report.Startup.Reason);
        Assert.True(report.Pagefile.IsRead);
        Assert.Equal(12288, report.WslCap.Value!.Mb);
        Assert.Equal(2, report.Memory.Value!.Count);
    }

    [Fact]
    public void One_run_key_refused_names_it_and_keeps_the_entries_of_the_others()
    {
        var m = new Machine();
        m.Registry.Denied.Add((RunHive.LocalMachine, RunView.Registry32, WindowsDiagnostics.Run));
        var inventory = m.Reader.Read().Startup.Value!;
        Assert.Single(inventory.Items);
        Assert.Contains("HKLM Run (32-bit) could not be read", Assert.Single(inventory.Unreadable));
    }

    // ---- startup entries by hive, view and command ----

    private static byte[] Record(byte first)
    {
        var record = new byte[12];
        record[0] = first;
        return record;
    }

    [Fact]
    public void Same_named_entries_are_told_apart_by_hive_view_and_command_with_each_one_s_approval()
    {
        var m = new Machine();
        var run = WindowsDiagnostics.Run;
        m.Registry.Keys.Clear();
        m.Registry.Key(RunHive.CurrentUser, RunView.Registry64, run)["Updater"] = @"C:\user\updater.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, run)["Updater"] = @"C:\Program Files\app\updater.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry32, run)["Updater"] = @"C:\Program Files (x86)\app\updater.exe";
        m.Registry.Key(RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Updater"] = Record(0x02);
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Updater"] = Record(0x03);
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun32)["Updater"] = Record(0x06);

        var items = m.Reader.ReadStartup().Items;
        Assert.Equal(3, items.Count);
        Assert.Equal(new StartupItem("Updater", @"C:\user\updater.exe", RunHive.CurrentUser, RunView.Registry64, new StartupApproval(ApprovalState.On)), items[0]);
        Assert.Equal(new StartupItem("Updater", @"C:\Program Files\app\updater.exe", RunHive.LocalMachine, RunView.Registry64, new StartupApproval(ApprovalState.TurnedOffByWindows)), items[1]);
        Assert.Equal(new StartupItem("Updater", @"C:\Program Files (x86)\app\updater.exe", RunHive.LocalMachine, RunView.Registry32, new StartupApproval(ApprovalState.On)), items[2]);
    }

    [Fact]
    public void The_32_bit_view_takes_its_approval_from_run32_and_not_from_run()
    {
        var m = new Machine();
        m.Registry.Keys.Clear();
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry32, WindowsDiagnostics.Run)["Old"] = @"C:\old.exe";
        // Only the 64-bit record says off: the 32-bit entry must not read it.
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Old"] = Record(0x03);
        Assert.Equal(ApprovalState.NoRecord, Assert.Single(m.Reader.ReadStartup().Items).Approval.State);

        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun32)["old"] = Record(0x03);
        Assert.Equal(ApprovalState.TurnedOffByWindows, Assert.Single(m.Reader.ReadStartup().Items).Approval.State);
    }

    [Fact]
    public void A_pagefile_value_of_the_wrong_type_is_a_reason_never_an_empty_configuration()
    {
        // Round 2: a DWORD PagingFiles read as no pagefile configured.
        var m = new Machine();
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, Mm)["PagingFiles"] = 7;
        var pagefile = m.Reader.Read().Pagefile;
        Assert.False(pagefile.IsRead);
        Assert.Contains("PagingFiles is a Int32", pagefile.Reason);

        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, Mm).Remove("PagingFiles");
        Assert.True(m.Reader.Read().Pagefile.IsRead);                     // absent is still an empty configuration
    }

    [Fact]
    public void An_absent_approval_record_is_told_apart_from_an_unreadable_one()
    {
        var m = new Machine();
        m.Registry.Keys.Clear();
        m.Registry.Key(RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.Run)["Quiet"] = "q.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run)["Walled"] = "w.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run)["Garbled"] = "g.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run)["Texted"] = "t.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run)["Short"] = "s.exe";
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Garbled"] = new byte[] { 0x09 };
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Texted"] = "02";        // round 1: read as on
        m.Registry.Key(RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.ApprovedRun)["Short"] = new byte[] { 0x02 };
        m.Registry.Denied.Add((RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.ApprovedRun));

        var items = m.Reader.ReadStartup().Items;
        var quiet = items.Single(i => i.Name == "Quiet");
        Assert.Equal(ApprovalState.Unreadable, quiet.Approval.State);
        Assert.Contains("denied", quiet.Approval.Reason);

        Assert.Equal(ApprovalState.NoRecord, items.Single(i => i.Name == "Walled").Approval.State);
        Assert.Equal(ApprovalState.Unreadable, items.Single(i => i.Name == "Garbled").Approval.State);
        Assert.Equal(ApprovalState.Unreadable, items.Single(i => i.Name == "Texted").Approval.State);
        Assert.Equal(ApprovalState.Unreadable, items.Single(i => i.Name == "Short").Approval.State);

        // With the key denied, no entry in its scope is ever read as on or as having no record.
        Assert.DoesNotContain(items.Where(i => i.Hive == RunHive.CurrentUser), i => i.Approval.State is ApprovalState.On or ApprovalState.NoRecord);
    }

    [Fact]
    public void A_run_value_that_is_not_text_is_shown_as_such_and_not_dropped()
    {
        var m = new Machine();
        m.Registry.Key(RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.Run)["Odd"] = 5;
        var odd = m.Reader.ReadStartup().Items.Single(i => i.Name == "Odd");
        Assert.Contains("not text", odd.Command);
    }

    [Fact]
    public void Reading_asks_the_registry_for_each_key_it_needs_and_writes_nothing()
    {
        var m = new Machine();
        m.Reader.Read();
        Assert.Contains((RunHive.LocalMachine, RunView.Registry64, Mm), m.Registry.Opened);
        Assert.Contains((RunHive.CurrentUser, RunView.Registry64, WindowsDiagnostics.Run), m.Registry.Opened);
        Assert.Contains((RunHive.LocalMachine, RunView.Registry64, WindowsDiagnostics.Run), m.Registry.Opened);
        Assert.Contains((RunHive.LocalMachine, RunView.Registry32, WindowsDiagnostics.Run), m.Registry.Opened);
    }

    // ---- the readers themselves ----

    [Fact]
    public void A_long_commented_wslconfig_still_gives_its_cap_and_too_many_settings_are_refused()
    {
        // Round 4: a file over 64 KB of comments was refused although its cap was plain.
        var comments = string.Concat(Enumerable.Repeat("# a comment line that says nothing about memory at all\n", 4000));
        var text = ProfileFiles.Settings(new StringReader("[wsl2]\n" + comments + "  ; indented comment\nmemory=4GB\n" + comments), ".wslconfig");
        Assert.Equal("[wsl2]\nmemory=4GB\n", text);
        Assert.Equal(new WslCap(4096), WslConfigParser.ParseCap(text).Value);

        // One comment line longer than the bound is read past, kept nothing.
        Assert.Equal("[wsl2]\n", ProfileFiles.Settings(new StringReader("#" + new string('x', 200_000) + "\n[wsl2]"), ".wslconfig"));

        // Round 6: leading whitespace is never counted, and a file past the scan bound is said, not scanned on.
        Assert.Equal("[wsl2]\nmemory=8GB\n", ProfileFiles.Settings(new StringReader(new string(' ', 65_537) + "\n[wsl2]\n   memory=8GB\n"), ".wslconfig"));
        var huge = Assert.Throws<IOException>(() => ProfileFiles.Settings(new EndlessComment(), ".wslconfig"));
        Assert.Contains("longer than 16 MB", huge.Message);

        var settings = string.Concat(Enumerable.Repeat("key=value\n", 10_000));
        var refused = Assert.Throws<IOException>(() => ProfileFiles.Settings(new StringReader(settings), ".wslconfig"));
        Assert.Contains("more than 64 KB of settings", refused.Message);
    }

    [Fact]
    public void A_firmware_table_that_grows_between_reads_is_read_again_and_one_that_keeps_growing_is_an_IOException()
    {
        // Round 4: an answer larger than the buffer sliced past it and threw past the guard.
        var sizes = new Queue<uint>([4096, 4097, 4097]);
        var table = Win32FirmwareTable.Read((_, _, buffer, size) =>
        {
            var answer = sizes.Dequeue();
            if (buffer is not null && answer <= size) Array.Fill(buffer, (byte)1, 0, (int)answer);
            return answer;
        });
        Assert.Equal(4097, table.Length);

        var growing = 4096u;
        var fault = Assert.Throws<IOException>(() => Win32FirmwareTable.Read((_, _, _, _) => growing++));
        Assert.Contains("kept growing", fault.Message);
    }

    /// <summary>A comment that never ends, as a multi-gigabyte one would be: it stops the test if the bound fails.</summary>
    private sealed class EndlessComment : TextReader
    {
        private long _read;

        public override int Read() => _read++ == 0 ? '#' : _read < 64L * 1024 * 1024 ? 'x' : -1;
    }

    // ---- this machine ----

    [Fact]
    public void This_machine_s_four_facts_are_read_without_elevation_and_printed()
    {
        // Proof of standard-user access only from a standard user (round 3): an elevated run, as a CI runner's is,
        // proves nothing about it, so it is skipped and says why; the controlled tests above run everywhere.
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                Assert.Skip("This process is elevated, so it can't show the facts are read without elevation.");

        var reader = new WindowsDiagnostics(new RegistryKeySource(), new ProfileFiles(), new Win32FirmwareTable(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var report = reader.Read();

        var text = new StringBuilder();
        text.AppendLine($"Pagefile: {(report.Pagefile.IsRead ? string.Join("; ", report.Pagefile.Value!.Configured) + " | active " + string.Join(",", report.Pagefile.Value.Active) + $" | temp {report.Pagefile.Value.TemporaryInUse} | pending {report.Pagefile.Value.PendingRestart}" : report.Pagefile.Reason)}");
        text.AppendLine($"WSL cap: {(report.WslCap.IsRead ? report.WslCap.Value!.Describe() : report.WslCap.Reason)}");
        if (report.Memory.IsRead) foreach (var module in report.Memory.Value!) text.AppendLine($"Module: {module}");
        else text.AppendLine($"Memory: {report.Memory.Reason}");
        if (report.Startup.IsRead)
        {
            // Never the command: its arguments can hold a token, and a test log is no place for one (round 1).
            foreach (var item in report.Startup.Value!.Items) text.AppendLine($"Startup: {item.Hive} {item.View} {item.Name} [{item.Approval.State}]");
            foreach (var why in report.Startup.Value.Unreadable) text.AppendLine($"Startup unreadable: {why}");
        }
        else text.AppendLine($"Startup: {report.Startup.Reason}");
        text.AppendLine($"RAM finding: {(report.RamFinding is { } f ? string.Join("; ", f.Modules) : "none")}");
        TestContext.Current.SendDiagnosticMessage(text.ToString());

        Assert.True(report.Pagefile.IsRead, report.Pagefile.Reason);
        // The WSL cap is the person's own file: a bad or absent value is a reason, which is a right answer (round 3).
        Assert.True(report.WslCap.IsRead || !string.IsNullOrWhiteSpace(report.WslCap.Reason), "the WSL cap is a value or a reason");
        Assert.True(report.Memory.IsRead, report.Memory.Reason);
        Assert.True(report.Startup.IsRead, report.Startup.Reason);
        Assert.NotEmpty(report.Memory.Value!);
        // A part number is optional in SMBIOS (round 1): a machine whose modules give none has no finding, and passes.
        if (report.Memory.Value!.Any(module => string.IsNullOrEmpty(module.PartNumber))) Assert.Null(report.RamFinding);
    }
}
