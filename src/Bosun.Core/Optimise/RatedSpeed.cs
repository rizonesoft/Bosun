using System.Text.RegularExpressions;

namespace Bosun.Core.Optimise;

/// <summary>
/// A module's rated (XMP or equivalent) speed in MT/s, decoded from its part number. SMBIOS does not carry it: its
/// <c>Speed</c> is the JEDEC speed. Each maker's format is decoded by its published rule, and a part number no rule
/// knows, or a near-match with no speed field (or a JEDEC module's number), gives null, never a guess.
/// </summary>
public static partial class RatedSpeed
{
    private const int Slowest = 1600;
    private const int Fastest = 10000;

    public static int? Of(string? partNumber)
    {
        var p = partNumber?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(p)) return null;

        // Corsair: "Corsair Memory Part Number Decoder" (corsair.com, memory part number guide). CM + family letter +
        // kit size G + generation (X4 is DDR4, X5 is DDR5) + M + module count + speed class letter + MT/s + C + CAS:
        // CMK32GX4M2D3600C18 is 3600, CMK16GX4M2B3200C16 is 3200, and this machine's kit is CMG16GX4M2D3600C18.
        if (Corsair().Match(p) is { Success: true } c) return InRange(int.Parse(c.Groups[1].Value));

        // G.Skill: "G.SKILL part number guide" (gskill.com, How to read G.SKILL memory part numbers). F + DDR generation,
        // then MT/s, C + CAS, a rank/layout letter, kit size and series: F4-3600C16D-32GVKC is 3600, F4-3200C16D-16GVK is 3200.
        if (GSkill().Match(p) is { Success: true } g) return InRange(int.Parse(g.Groups[1].Value));

        // Kingston FURY and HyperX: "Kingston FURY memory part number decoder" (kingston.com, Memory part number guide).
        // KF or HX + DDR generation + a two-digit speed code + C + CAS: KF436C18BBK2/32 is 3600, KF432C16BBK2/16 is 3200,
        // HX436C18FB3K2/32 is 3600, HX432C16PB3K2/16 is 3200. KVR (ValueRAM) numbers are JEDEC modules and are unknown.
        if (Kingston().Match(p) is { Success: true } k) return FromCode(k.Groups[1].Value);

        // Crucial Ballistix: "Crucial Ballistix part number decoder" (crucial.com, How to read a Crucial memory part number).
        // BL + kit module count K + module size G + a two-digit speed code + C + CAS: BL2K16G36C16U4B is 3600, BL2K8G32C16U4B is 3200.
        if (Ballistix().Match(p) is { Success: true } b) return FromCode(b.Groups[1].Value);

        // TeamGroup: "T-FORCE part number guide" (teamgroupinc.com, memory product naming rules). TF3D + DDR generation +
        // module size G + MT/s + a speed-bin letter pair ending in C + CAS: TF3D416G3600HC18J is 3600, TF3D416G3200HC16F is 3200.
        if (TeamGroup().Match(p) is { Success: true } t) return InRange(int.Parse(t.Groups[1].Value));

        return null;
    }

    /// <summary>The two-digit code is hundreds of MT/s, except the JEDEC-derived rates the makers abbreviate.</summary>
    private static int? FromCode(string code)
    {
        var n = int.Parse(code);
        return InRange(n switch
        {
            21 => 2133,
            24 => 2400,
            26 => 2666,
            29 => 2933,
            _ => n * 100,
        });
    }

    private static int? InRange(int mts) => mts is >= Slowest and <= Fastest ? mts : null;

    [GeneratedRegex(@"^CM[A-Z]{1,2}\d{1,3}G[A-Z]\d[A-Z]?M\d{1,2}[A-Z](\d{4})C\d{1,2}[A-Z0-9]*$")]
    private static partial Regex Corsair();

    [GeneratedRegex(@"^F[345]-(\d{4})C\d{1,2}[A-Z]?-\d{1,3}G[A-Z0-9]+$")]
    private static partial Regex GSkill();

    [GeneratedRegex(@"^(?:KF|HX)[345](\d{2})C\d{1,2}[A-Z0-9]+(?:[/-]\d{1,3})?$")]
    private static partial Regex Kingston();

    [GeneratedRegex(@"^BL(?:\d{1,2}K)?\d{1,3}G(\d{2})C\d{1,2}[A-Z0-9]+$")]
    private static partial Regex Ballistix();

    [GeneratedRegex(@"^TF3D[345]\d{1,3}G(\d{4})[A-Z]{1,2}C\d{1,2}[A-Z0-9]*$")]
    private static partial Regex TeamGroup();
}
