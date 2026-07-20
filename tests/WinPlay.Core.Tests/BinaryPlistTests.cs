// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Plist;
using Xunit;

namespace WinPlay.Core.Tests;

public class BinaryPlistTests
{
    [Fact]
    public void Roundtrips_Nested_Session_Setup_Shape()
    {
        var original = new Dictionary<string, object?>
        {
            ["deviceID"] = "AA:BB:CC:DD:EE:FF",
            ["timingPort"] = 32145L,
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 0x60L,
                    ["audioFormat"] = 0x40000L,
                    ["shk"] = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
                    ["latencyMax"] = 88200L,
                    ["rate"] = 44.1,
                },
            },
        };

        byte[] encoded = BinaryPlist.Write(original);
        Assert.StartsWith("bplist00", System.Text.Encoding.ASCII.GetString(encoded[..8]));

        var decoded = BinaryPlist.ReadDictionary(encoded);
        Assert.Equal("AA:BB:CC:DD:EE:FF", decoded["deviceID"]);
        Assert.Equal(32145L, decoded["timingPort"]);
        Assert.Equal(true, decoded["isMultiSelectAirPlay"]);
        Assert.Equal(false, decoded["groupContainsGroupLeader"]);

        var stream = Assert.IsType<Dictionary<string, object?>>(
            Assert.Single(Assert.IsType<List<object?>>(decoded["streams"])));
        Assert.Equal(0x60L, stream["type"]);
        Assert.Equal(0x40000L, stream["audioFormat"]);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, stream["shk"]);
        Assert.Equal(44.1, (double)stream["rate"]!, precision: 10);
    }

    [Fact]
    public void Roundtrips_Large_Collections_And_Long_Strings()
    {
        // >14 entries exercises the extended-count marker; >255 objects exercises 2-byte refs.
        var big = new Dictionary<string, object?>();
        for (int i = 0; i < 300; i++)
            big[$"key{i:D3}"] = (long)i;
        big["text"] = new string('x', 500);
        big["blob"] = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
        big["unicode"] = "salle de séjour — 客厅";

        var decoded = BinaryPlist.ReadDictionary(BinaryPlist.Write(big));
        Assert.Equal(299L, decoded["key299"]);
        Assert.Equal(new string('x', 500), decoded["text"]);
        Assert.Equal(300, ((byte[])decoded["blob"]!).Length);
        Assert.Equal("salle de séjour — 客厅", decoded["unicode"]);
    }

    [Fact]
    public void Roundtrips_Negative_And_Large_Integers()
    {
        var dict = new Dictionary<string, object?>
        {
            ["neg"] = -1L,
            ["big"] = long.MaxValue,
            ["u32"] = 0xFFFFFFFFL,
        };
        var decoded = BinaryPlist.ReadDictionary(BinaryPlist.Write(dict));
        Assert.Equal(-1L, decoded["neg"]);
        Assert.Equal(long.MaxValue, decoded["big"]);
        Assert.Equal(0xFFFFFFFFL, decoded["u32"]);
    }

    [Fact]
    public void Rejects_Garbage()
    {
        Assert.Throws<FormatException>(() => BinaryPlist.Read("not a plist at all"u8.ToArray()));
    }
}
