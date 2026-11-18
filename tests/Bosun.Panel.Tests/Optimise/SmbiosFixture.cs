using System.Text;

namespace Bosun.Panel.Tests.Optimise;

/// <summary>A minimal SMBIOS table in the DMTF DSP0134 layout: type 17 structures and an end-of-table structure.</summary>
internal static class SmbiosFixture
{
    public static byte[] Type17(ushort size, string locator, string bank, string maker, string part, ushort speed, ushort configured)
    {
        var body = new byte[0x5C];
        body[0] = 17;
        body[1] = (byte)body.Length;
        BitConverter.GetBytes(size).CopyTo(body, 0x0C);
        body[0x10] = 1;
        body[0x11] = 2;
        BitConverter.GetBytes(speed).CopyTo(body, 0x15);
        body[0x17] = 3;
        body[0x1A] = 4;
        BitConverter.GetBytes(configured).CopyTo(body, 0x20);
        return [.. body, .. Strings(locator, bank, maker, part)];
    }

    public static byte[] Table(params byte[][] structures)
    {
        byte[] end = [127, 4, 0, 0, 0, 0];
        var table = structures.Append(end).SelectMany(s => s).ToArray();
        return [.. new byte[] { 0, 3, 3, 0 }, .. BitConverter.GetBytes((uint)table.Length), .. table];
    }

    private static byte[] Strings(params string[] strings) => [.. strings.SelectMany(s => Encoding.UTF8.GetBytes(s).Append((byte)0)), 0];
}
