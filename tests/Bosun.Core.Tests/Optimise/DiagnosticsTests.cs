using System.Text;
using Bosun.Core.Optimise;
using Xunit;

namespace Bosun.Core.Tests.Optimise;

/// <summary>Builds SMBIOS tables in the DMTF DSP0134 layout, so the parser is read against the documented bytes.</summary>
internal static class SmbiosFixture
{
    /// <summary>A type 17 structure of the given formatted length, its strings after it.</summary>
    public static byte[] Type17(
        ushort size = 8192,
        string? locator = null,
        string? bank = null,
        string? maker = null,
        string? part = null,
        ushort speed = 0,
        ushort configured = 0,
        uint extendedSpeed = 0,
        uint extendedConfigured = 0,
        int length = 0x5C,
        uint extendedSize = 0)
    {
        var body = new byte[length];
        body[0] = 17;
        body[1] = (byte)length;
        var strings = new List<string>();
        byte Index(string? s)
        {
            if (s is null) return 0;
            strings.Add(s);
            return (byte)strings.Count;
        }

        void Word(int at, ushort v)
        {
            if (at + 2 <= length) BitConverter.GetBytes(v).CopyTo(body, at);
        }

        void Dword(int at, uint v)
        {
            if (at + 4 <= length) BitConverter.GetBytes(v).CopyTo(body, at);
        }

        void Str(int at, string? s)
        {
            if (at < length) body[at] = Index(s);
        }

        Word(0x0C, size);
        Dword(0x1C, extendedSize);
        Str(0x10, locator);
        Str(0x11, bank);
        Word(0x15, speed);
        Str(0x17, maker);
        Str(0x1A, part);
        Word(0x20, configured);
        Dword(0x54, extendedSpeed);
        Dword(0x58, extendedConfigured);
        return Join(body, strings);
    }

    public static byte[] Other(byte type, params string[] strings)
    {
        var body = new byte[8];
        body[0] = type;
        body[1] = 8;
        return Join(body, strings);
    }

    private static byte[] Join(byte[] body, IEnumerable<string> strings)
    {
        var list = strings.ToList();
        var bytes = new List<byte>(body);
        if (list.Count == 0) bytes.AddRange([0, 0]);
        else
        {
            foreach (var s in list)
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s));
                bytes.Add(0);
            }

            bytes.Add(0);
        }

        return [.. bytes];
    }

    /// <summary>The RawSMBIOSData header (calling method, 3.3 version, a 32-bit length), the structures and an end-of-table structure.</summary>
    public static byte[] Table(params byte[][] structures)
    {
        var table = structures.Concat([Other(127)]).SelectMany(s => s).ToArray();
        return [.. new byte[] { 0, 3, 3, 0 }, .. BitConverter.GetBytes((uint)table.Length), .. table];
    }
}

/// <summary>
/// The Optimise diagnostics (D05 T02 §1): the pagefile, the WSL cap, the memory modules and the startup entries
/// parsed from the sources' own formats, and the RAM finding from the part number only.
/// </summary>
public class DiagnosticsTests
{
    // This machine's kit, read 2026-11-18 from SMBIOS: four Corsair modules, Speed and Configured Memory Speed 2133.
    private const string KitPart = "CMG16GX4M2D3600C18";

    private static MemoryModule Module(string? part, int? speed = 2133, int? configured = 2133, string locator = "Controller0-ChannelA-DIMM0") =>
        new(locator, "BANK 0", "Corsair", part, 8192, speed, configured);

    // ---- Fact ----

    [Fact]
    public void A_fact_is_a_value_or_a_reason()
    {
        var read = Fact<int>.Read(5);
        Assert.True(read.IsRead);
        Assert.Equal(5, read.Value);
        Assert.Null(read.Reason);

        var gone = Fact<int>.Unavailable("denied");
        Assert.False(gone.IsRead);
        Assert.Equal("denied", gone.Reason);
    }

    // ---- Pagefile ----

    private static Pagefile Pagefile(string[] paging, string[] existing, int? temp = null) =>
        PagefileParser.Parse(new RawPagefile(paging, existing, temp)).Value!;

    [Fact]
    public void A_pagefile_with_sizes_reads_its_initial_and_maximum_and_matches_the_active_file()
    {
        var p = Pagefile([@"C:\pagefile.sys 32768 51200"], [@"\??\C:\pagefile.sys"]);
        var config = Assert.Single(p.Configured);
        Assert.Equal(new PagefileConfig(@"C:\pagefile.sys", 32768, 51200, false), config);
        Assert.Equal([@"C:\pagefile.sys"], p.Active);
        Assert.False(p.PendingRestart);
        Assert.False(p.TemporaryInUse);
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void Windows_choosing_the_drive_and_zero_sizes_are_system_managed()
    {
        var choose = Pagefile([@"?:\pagefile.sys"], [@"\??\C:\pagefile.sys"]).Configured[0];
        Assert.True(choose.SystemManaged);
        Assert.Null(choose.InitialMb);
        Assert.Null(choose.MaximumMb);
        Assert.False(Pagefile([@"?:\pagefile.sys"], [@"\??\C:\pagefile.sys"]).PendingRestart);

        var zero = Pagefile([@"C:\pagefile.sys 0 0"], [@"\??\C:\pagefile.sys"]).Configured[0];
        Assert.Equal(new PagefileConfig(@"C:\pagefile.sys", null, null, true), zero);
    }

    [Fact]
    public void A_move_waiting_on_a_restart_says_so()
    {
        var p = Pagefile([@"C:\pagefile.sys 32768 51200"], [@"\??\D:\pagefile.sys"]);
        Assert.True(p.PendingRestart);
        Assert.Equal(@"C:\pagefile.sys", p.Configured[0].Path);
        Assert.Equal([@"D:\pagefile.sys"], p.Active);
    }

    [Fact]
    public void A_temporary_pagefile_is_its_own_state_apart_from_the_configuration()
    {
        var p = Pagefile([@"C:\pagefile.sys 32768 51200"], [@"\??\C:\pagefile.sys"], temp: 1);
        Assert.True(p.TemporaryInUse);
        Assert.False(p.PendingRestart);
        Assert.False(Pagefile([@"C:\pagefile.sys 32768 51200"], [@"\??\C:\pagefile.sys"], temp: 0).TemporaryInUse);
    }

    [Fact]
    public void An_empty_configuration_is_empty_and_files_still_in_use_wait_on_a_restart()
    {
        var none = Pagefile([], []);
        Assert.True(none.IsEmpty);
        Assert.Empty(none.Active);
        Assert.False(none.PendingRestart);

        var removed = Pagefile([""], [@"\??\C:\pagefile.sys"]);
        Assert.True(removed.IsEmpty);
        Assert.True(removed.PendingRestart);
    }

    [Fact]
    public void A_pagefile_line_that_is_not_a_path_with_sizes_is_a_reason_not_an_exception()
    {
        var fact = PagefileParser.Parse(new RawPagefile([@"C:\pagefile.sys 32768"], [], null));
        Assert.False(fact.IsRead);
        Assert.Contains("C:\\pagefile.sys 32768", fact.Reason);
    }

    // ---- .wslconfig ----

    [Theory]
    [InlineData("[wsl2]\nmemory=14GB\n", 14336)]
    [InlineData("[wsl2]\nmemory=16384MB\n", 16384)]
    [InlineData("[wsl2]\nmemory=15032385536\n", 14336)]
    [InlineData("[wsl2]\r\nMemory = 12gb  # lowered\r\nswap=2GB\r\n", 12288)]
    [InlineData("# top\n[boot]\nmemory=1GB\n[wsl2]\n# memory=2GB\nmemory=\"8GB\"\n", 8192)]
    [InlineData("[wsl2]\nmemory=2097152KB\n", 2048)]
    public void The_configured_cap_is_read_in_mb_whatever_its_spelling(string text, long mb)
    {
        var fact = WslConfigParser.ParseCap(text);
        Assert.Equal(new WslCap(mb), fact.Value);
        Assert.Equal($"{mb} MB", fact.Value!.Describe());
    }

    [Fact]
    public void A_plain_byte_count_equals_the_same_size_in_gb()
    {
        Assert.Equal(WslConfigParser.ParseCap("[wsl2]\nmemory=14GB").Value, WslConfigParser.ParseCap("[wsl2]\nmemory=15032385536").Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[wsl2]\nswap=2GB\n")]
    [InlineData("[boot]\nmemory=4GB\n")]
    [InlineData("[wsl2]\n# memory=4GB\n")]
    public void An_absent_file_or_key_means_no_cap_is_set(string? text)
    {
        var cap = WslConfigParser.ParseCap(text).Value!;
        Assert.True(cap.NoCapSet);
        Assert.Null(cap.Mb);
        Assert.Contains("WSL takes half of memory", cap.Describe());
    }

    [Theory]
    [InlineData("[wsl2]\nmemory=lots\n")]
    [InlineData("[wsl2]\nmemory=0\n")]
    [InlineData("[wsl2]\nmemory=12XB\n")]
    [InlineData("[wsl2]\nmemory=\n")]
    [InlineData("[wsl2]\nmemory=1.5GB\n")]                     // completion review: WSL takes whole numbers
    [InlineData("[wsl2]\nmemory=12.0GB\n")]
    [InlineData("[wsl2]\nmemory=100000000000000000000GB\n")]   // round 1: overflowed and lost all four facts
    [InlineData("[wsl2]\nmemory=79228162514264337593543950335TB\n")]
    public void A_bad_value_is_a_reason_naming_it_never_a_cap_or_zero(string text)
    {
        var fact = WslConfigParser.ParseCap(text);
        Assert.False(fact.IsRead);
        Assert.Contains("not a size WSL accepts", fact.Reason);
    }

    [Theory]
    [InlineData("[wsl2]backup\nmemory=8GB\n", null)]          // round 6: a prefix is no header
    [InlineData("[wsl2x]\nmemory=8GB\n", null)]
    [InlineData("[wsl2] # the VM\nmemory=8GB\n", 8192L)]       // a header with a comment is the section
    [InlineData("[ WSL2 ]\nmemory=8GB\n", 8192L)]
    public void A_section_header_is_the_whole_line_never_a_prefix(string text, long? mb) =>
        Assert.Equal(mb, WslConfigParser.ParseCap(text).Value!.Mb);

    [Theory]
    [InlineData("[wsl2]\nmemory=8GB\"\n")]     // round 7: read as 8 GB
    [InlineData("[wsl2]\nmemory=\"8GB\n")]
    [InlineData("[wsl2]\nmemory=8\"GB\n")]
    public void An_unmatched_quote_is_a_reason_never_a_cap(string text)
    {
        var fact = WslConfigParser.ParseCap(text);
        Assert.False(fact.IsRead);
        Assert.Contains("quote with no partner", fact.Reason);
    }

    [Fact]
    public void A_quoted_value_is_read_inside_its_quotes() =>
        Assert.Equal(8192L, WslConfigParser.ParseCap("[wsl2]\nmemory=\"8GB\"\n").Value!.Mb);

    [Theory]
    [InlineData(new string[] { @"?:\pagefile.sys" }, new string[0], true)]                   // completion review: none active is proved
    [InlineData(new string[] { @"?:\pagefile.sys" }, new string[] { @"D:\pagefile.sys" }, false)]  // Windows chose D:
    [InlineData(new string[] { @"?:\pagefile.sys" }, new string[] { @"D:\other.sys" }, true)]
    [InlineData(new string[] { @"C:\pagefile.sys" }, new string[] { @"C:\pagefile.sys", @"D:\pagefile.sys" }, true)]
    public void A_pending_change_is_proved_against_the_active_files_wildcard_drive_included(string[] configured, string[] active, bool pending) =>
        Assert.Equal(pending, new Pagefile(configured.Select(p => new PagefileConfig(p, null, null, p.StartsWith("?:"))).ToList(), active, false).PendingRestart);

    [Fact]
    public void An_approval_value_that_could_not_be_read_is_unreadable_never_on()
    {
        var approval = StartupApproval.FromValue(new UnreadableValue("access is denied"));
        Assert.Equal(ApprovalState.Unreadable, approval.State);
        Assert.Contains("access is denied", approval.Reason);
    }

    // ---- SMBIOS type 17 ----

    [Fact]
    public void A_memory_device_is_read_by_its_dmtf_fields()
    {
        var raw = SmbiosFixture.Table(
            SmbiosFixture.Other(0, "BIOS"),
            SmbiosFixture.Type17(8192, "Controller0-ChannelA-DIMM0", "BANK 0", "Corsair", "CMG16GX4M2D3600C18  ", speed: 2133, configured: 2133),
            SmbiosFixture.Other(32, "x", "y"));
        var module = Assert.Single(SmbiosParser.ParseModules(raw).Value!);
        Assert.Equal(new MemoryModule("Controller0-ChannelA-DIMM0", "BANK 0", "Corsair", "CMG16GX4M2D3600C18", 8192, 2133, 2133), module);
    }

    [Fact]
    public void An_empty_slot_is_skipped_and_the_end_of_table_stops_the_walk()
    {
        var raw = SmbiosFixture.Table(
            SmbiosFixture.Type17(0, "DIMM0"),
            SmbiosFixture.Type17(16384, "DIMM1", speed: 3200, configured: 3200),
            SmbiosFixture.Other(127),
            SmbiosFixture.Type17(8192, "after the end"));
        var modules = SmbiosParser.ParseModules(raw).Value!;
        Assert.Equal("DIMM1", Assert.Single(modules).DeviceLocator);
    }

    [Fact]
    public void A_speed_of_zero_is_unknown_never_zero()
    {
        var module = SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(speed: 0, configured: 0))).Value![0];
        Assert.Null(module.SpeedMts);
        Assert.Null(module.ConfiguredSpeedMts);
    }

    [Fact]
    public void A_speed_of_65535_takes_the_extended_field_and_zero_extended_is_unknown()
    {
        var fast = SmbiosParser.ParseModules(SmbiosFixture.Table(
            SmbiosFixture.Type17(speed: 0xFFFF, configured: 0xFFFF, extendedSpeed: 70000, extendedConfigured: 66000))).Value![0];
        Assert.Equal(70000, fast.SpeedMts);
        Assert.Equal(66000, fast.ConfiguredSpeedMts);

        var none = SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(speed: 0xFFFF, configured: 0xFFFF))).Value![0];
        Assert.Null(none.SpeedMts);
        Assert.Null(none.ConfiguredSpeedMts);
    }

    [Fact]
    public void A_field_past_the_structure_s_length_is_unknown_not_read_from_the_next_structure()
    {
        // An SMBIOS 2.3 record ends at 0x15: it has no Speed, no part number and no configured speed.
        var raw = SmbiosFixture.Table(
            SmbiosFixture.Type17(4096, "DIMM0", "BANK 0", length: 0x15),
            SmbiosFixture.Type17(8192, "DIMM1", speed: 2666, configured: 2666));
        var modules = SmbiosParser.ParseModules(raw).Value!;
        Assert.Equal(2, modules.Count);
        Assert.Equal(new MemoryModule("DIMM0", "BANK 0", null, null, 4096, null, null), modules[0]);
        Assert.Equal(2666, modules[1].SpeedMts);
    }

    [Fact]
    public void Size_is_read_in_mb_in_kb_and_from_the_extended_field()
    {
        Assert.Equal(2, SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(size: 0x8000 | 2048))).Value![0].SizeMb);
        Assert.Equal(65536, SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(size: 0x7FFF, extendedSize: 65536))).Value![0].SizeMb);
        Assert.Null(SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(size: 0xFFFF))).Value![0].SizeMb);
    }

    [Fact]
    public void Strings_are_by_index_and_index_zero_or_blank_is_none()
    {
        var module = SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Type17(locator: "  ", bank: "B", maker: null, part: "P"))).Value![0];
        Assert.Null(module.DeviceLocator);
        Assert.Equal("B", module.BankLocator);
        Assert.Null(module.Manufacturer);
        Assert.Equal("P", module.PartNumber);
    }

    [Fact]
    public void A_table_with_no_memory_devices_is_an_empty_inventory_not_a_failure()
    {
        var fact = SmbiosParser.ParseModules(SmbiosFixture.Table(SmbiosFixture.Other(0, "BIOS")));
        Assert.True(fact.IsRead);
        Assert.Empty(fact.Value!);
    }

    [Fact]
    public void A_missing_or_truncated_table_is_a_reason_never_an_exception_or_an_empty_inventory()
    {
        Assert.Contains("no SMBIOS table", SmbiosParser.ParseModules(null).Reason);
        Assert.Contains("shorter than its 8-byte header", SmbiosParser.ParseModules([1, 2, 3]).Reason);

        var good = SmbiosFixture.Table(SmbiosFixture.Type17(speed: 2133));
        Assert.Contains("truncated", SmbiosParser.ParseModules(good[..^6]).Reason);

        // The header's length is honest but a structure runs past it, or its strings never end.
        var cut = SmbiosFixture.Type17(speed: 2133, part: "CMG16GX4M2D3600C18");
        var noEnd = cut[..^3];
        var table = new byte[] { 0, 3, 3, 0 }.Concat(BitConverter.GetBytes((uint)noEnd.Length)).Concat(noEnd).ToArray();
        Assert.Contains("truncated", SmbiosParser.ParseModules(table).Reason);

        var longer = new byte[] { 0, 3, 3, 0 }.Concat(BitConverter.GetBytes((uint)0x20)).Concat(cut[..0x20]).ToArray();
        Assert.Contains("truncated", SmbiosParser.ParseModules(longer).Reason);
    }

    // ---- startup approval ----

    [Theory]
    [InlineData(0x02, ApprovalState.On)]
    [InlineData(0x06, ApprovalState.On)]
    [InlineData(0x03, ApprovalState.TurnedOffByWindows)]
    [InlineData(0x07, ApprovalState.TurnedOffByWindows)]
    public void The_approval_record_s_first_byte_says_on_or_turned_off(byte first, ApprovalState state)
    {
        var record = new byte[12];
        record[0] = first;
        Assert.Equal(new StartupApproval(state), StartupApproval.FromRecord(record));
    }

    [Fact]
    public void No_record_is_no_record_and_a_record_that_is_not_one_windows_writes_is_unreadable_never_on()
    {
        Assert.Equal(ApprovalState.NoRecord, StartupApproval.FromRecord(null).State);
        var empty = StartupApproval.FromRecord([]);
        Assert.Equal(ApprovalState.Unreadable, empty.State);
        Assert.NotNull(empty.Reason);
        var odd = new byte[12];
        odd[0] = 0x09;
        Assert.Equal(ApprovalState.Unreadable, StartupApproval.FromRecord(odd).State);
        Assert.Contains("0x09", StartupApproval.FromRecord(odd).Reason);

        // Round 1: a lone 02 and a text value read as on.
        Assert.Equal(ApprovalState.Unreadable, StartupApproval.FromRecord([0x02]).State);
        Assert.Contains("1 bytes", StartupApproval.FromRecord([0x02]).Reason);
        Assert.Equal(ApprovalState.Unreadable, StartupApproval.FromValue("02").State);
        Assert.Contains("String", StartupApproval.FromValue("02").Reason);
        Assert.Equal(ApprovalState.NoRecord, StartupApproval.FromValue(null).State);
    }

    // ---- rated speed ----

    // Two documented part numbers at different ratings for each maker's format.
    [Theory]
    [InlineData("HX437C19FB3A/16", 3733)]   // round 5: 37 is 3733 in Kingston's table, never 37 x 100
    [InlineData("KF437C19RBK2/16", 3733)]
    [InlineData("KF426C16BBK2/16", 2666)]
    [InlineData("BLM2K8G44C19U4B", null)]   // Ballistix MAX's BLM prefix is not the BL format this decoder reads
    [InlineData("BL2K8G44C19U4B", 4400)]
    public void A_speed_code_decodes_only_by_its_maker_s_table(string partNumber, int? rating) =>
        Assert.Equal(rating, RatedSpeed.Of(partNumber));

    [Theory]
    [InlineData("KF435C18BBK2/16")]   // 35 is no code Kingston lists: unknown, never 3500
    [InlineData("HX439C19FB3/16")]
    [InlineData("BL2K8G38C16U4B")]    // 38 is no Ballistix code
    public void A_code_a_maker_s_table_lacks_is_unknown_never_a_multiple(string partNumber) =>
        Assert.Null(RatedSpeed.Of(partNumber));

    [Theory]
    [InlineData("CMK32GX4M2D3600C18", 3600)]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("CMG16GX4M2D3600C18", 3600)]
    [InlineData("CMG16GX4M2D3600C18  ", 3600)]
    [InlineData("F4-3600C16D-32GVKC", 3600)]
    [InlineData("F4-3200C16D-16GVK", 3200)]
    [InlineData("KF436C18BBK2/32", 3600)]
    [InlineData("KF432C16BBK2/16", 3200)]
    [InlineData("HX436C18FB3K2/32", 3600)]
    [InlineData("HX432C16PB3K2/16", 3200)]
    [InlineData("BL2K16G36C16U4B", 3600)]
    [InlineData("BL2K8G32C16U4B", 3200)]
    [InlineData("KF426C16BB/8", 2666)]
    [InlineData("BL8G26C16U4B", 2666)]
    [InlineData("TF3D416G3600HC18J", 3600)]
    [InlineData("TF3D416G3200HC16F", 3200)]
    public void A_documented_part_number_decodes_to_its_rating(string part, int mts) => Assert.Equal(mts, RatedSpeed.Of(part));

    // A near-match of each maker: a prefix with no speed field, a JEDEC module's number, or a different shape.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CMK32GX4M2D")]
    [InlineData("CMK32GX4M2D36")]
    [InlineData("F4-")]
    [InlineData("F4-32GVK")]
    [InlineData("F4-3600C16D")]
    [InlineData("KF436")]
    [InlineData("KF4C18BB/32")]
    [InlineData("KVR32N22S8/16")]
    [InlineData("HX436")]
    [InlineData("HX3C16")]
    [InlineData("BL2K16G")]
    [InlineData("BL2K16GC16U4B")]
    [InlineData("CT16G4DFD8266")]
    [InlineData("TF3D416G")]
    [InlineData("TED416G2666C19")]
    [InlineData("M378A2K43DB1-CTD")]
    [InlineData("CMK32GX4M2D3600")]
    public void A_near_match_or_unknown_part_number_is_unknown(string? part) => Assert.Null(RatedSpeed.Of(part));

    // ---- the RAM finding ----

    [Fact]
    public void This_machine_s_kit_is_a_finding_with_each_module_s_evidence()
    {
        string[] slots = ["Controller0-ChannelA-DIMM0", "Controller0-ChannelA-DIMM1", "Controller0-ChannelB-DIMM0", "Controller0-ChannelB-DIMM1"];
        var raw = SmbiosFixture.Table(slots.Select((s, i) =>
            SmbiosFixture.Type17(8192, s, $"BANK {i / 2}", "Corsair", KitPart + "  ", speed: 2133, configured: 2133)).ToArray());
        var finding = Diagnostics.RamFinding(SmbiosParser.ParseModules(raw).Value!);

        Assert.NotNull(finding);
        Assert.Equal(4, finding.Modules.Count);
        for (var i = 0; i < 4; i++)
        {
            var e = finding.Modules[i];
            Assert.Equal(slots[i], e.DeviceLocator);
            Assert.Equal($"BANK {i / 2}", e.BankLocator);
            Assert.Equal(KitPart, e.PartNumber);
            Assert.Equal(3600, e.RatedMts);
            Assert.Equal(2133, e.ConfiguredMts);
            Assert.Equal(2133, e.MaximumMts);
        }
    }

    [Fact]
    public void No_finding_for_an_unknown_part_number()
    {
        Assert.Null(Diagnostics.RamFinding([Module("KVR32N22S8/16")]));
        Assert.Null(Diagnostics.RamFinding([Module(null)]));
        Assert.Null(Diagnostics.RamFinding([Module(KitPart), Module("KVR32N22S8/16")]));
    }

    [Fact]
    public void No_finding_when_the_configured_speed_is_unknown()
    {
        Assert.Null(Diagnostics.RamFinding([Module(KitPart, configured: null)]));
        Assert.Null(Diagnostics.RamFinding([Module(KitPart), Module(KitPart, configured: null)]));
        var raw = SmbiosFixture.Table(SmbiosFixture.Type17(8192, "DIMM0", part: KitPart, speed: 2133, configured: 0));
        Assert.Null(Diagnostics.RamFinding(SmbiosParser.ParseModules(raw).Value!));
    }

    [Fact]
    public void No_finding_for_a_mixed_kit_an_empty_inventory_or_a_module_at_its_rating()
    {
        Assert.Null(Diagnostics.RamFinding([Module("CMK16GX4M2B3200C16"), Module(KitPart)]));
        Assert.Null(Diagnostics.RamFinding([]));
        Assert.Null(Diagnostics.RamFinding([Module(KitPart, configured: 3600)]));
        Assert.Null(Diagnostics.RamFinding([Module(KitPart, configured: 3600), Module(KitPart, configured: 2133)]));
        Assert.Null(Diagnostics.RamFinding([Module(KitPart, configured: 4000)]));
    }

    [Fact]
    public void A_kit_below_its_rating_reports_a_missing_speed_field_as_null_evidence()
    {
        var finding = Diagnostics.RamFinding([Module(KitPart, speed: null, configured: 2400)]);
        Assert.NotNull(finding);
        Assert.Null(finding.Modules[0].MaximumMts);
        Assert.Equal(2400, finding.Modules[0].ConfiguredMts);
    }

    // ---- reading every source ----

    private sealed class Reader : IMachineReader
    {
        public Func<RawPagefile> Pagefile { get; init; } = () => new RawPagefile([@"C:\pagefile.sys 32768 51200"], [@"\??\C:\pagefile.sys"], null);

        public Func<string?> Wsl { get; init; } = () => "[wsl2]\nmemory=12GB\n";

        public Func<byte[]?> Smbios { get; init; } = () => SmbiosFixture.Table(SmbiosFixture.Type17(8192, "DIMM0", "BANK 0", "Corsair", KitPart, 2133, 2133));

        public Func<StartupInventory> Startup { get; init; } = () => new StartupInventory([], []);

        public RawPagefile ReadPagefile() => Pagefile();

        public string? ReadWslConfig() => Wsl();

        public byte[]? ReadSmbios() => Smbios();

        public StartupInventory ReadStartup() => Startup();
    }

    [Fact]
    public void Reading_gives_all_four_facts_and_the_finding()
    {
        var report = Diagnostics.Read(new Reader());
        Assert.True(report.Pagefile.IsRead);
        Assert.Equal(12288, report.WslCap.Value!.Mb);
        Assert.Single(report.Memory.Value!);
        Assert.True(report.Startup.IsRead);
        Assert.NotNull(report.RamFinding);
    }

    [Fact]
    public void A_source_that_refuses_costs_only_its_own_fact_and_says_why()
    {
        var report = Diagnostics.Read(new Reader { Wsl = () => throw new UnauthorizedAccessException("denied") });
        Assert.False(report.WslCap.IsRead);
        Assert.Contains("denied", report.WslCap.Reason);
        Assert.True(report.Pagefile.IsRead);
        Assert.True(report.Memory.IsRead);
        Assert.True(report.Startup.IsRead);

        var noMemory = Diagnostics.Read(new Reader { Smbios = () => throw new IOException("no table") });
        Assert.False(noMemory.Memory.IsRead);
        Assert.Null(noMemory.RamFinding);
        Assert.True(noMemory.WslCap.IsRead);
    }
}
