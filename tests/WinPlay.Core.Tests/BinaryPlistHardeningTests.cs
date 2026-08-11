// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using WinPlay.Core.Plist;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Hostile-input tests for the binary-plist parser. Every AirPlay response WinPlay parses —
/// <c>GET /info</c>, every SETUP reply, the 2 s <c>/feedback</c> keep-alive, event-channel
/// messages — is attacker-influenced data from a device on the LAN.
///
/// <para>Two properties are pinned here. First, a declared element count must never drive an
/// allocation: a few hundred bytes claiming billions of elements previously caused a
/// multi-gigabyte allocation attempt. Second, EVERY malformed input must surface as
/// <see cref="FormatException"/> — callers guard on exactly that type, and a stray range
/// exception escaping instead killed the background loop doing the parsing, which silently
/// ended playback.</para>
/// </summary>
public class BinaryPlistHardeningTests
{
    /// <summary>Builds a bplist00 with a valid trailer around a caller-supplied object body.</summary>
    private static byte[] Build(byte[] objectBody, int numObjects = 1, int topObject = 0)
    {
        var buf = new List<byte>();
        buf.AddRange(Encoding.ASCII.GetBytes("bplist00"));
        int objectStart = buf.Count;
        buf.AddRange(objectBody);

        int offsetTableOffset = buf.Count;
        buf.Add((byte)objectStart);              // 1-byte offset for object 0

        byte[] trailer = new byte[32];
        trailer[6] = 1;                          // offsetIntSize
        trailer[7] = 1;                          // refSize
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(8), (ulong)numObjects);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(16), (ulong)topObject);
        BinaryPrimitives.WriteUInt64BigEndian(trailer.AsSpan(24), (ulong)offsetTableOffset);
        buf.AddRange(trailer);
        return [.. buf];
    }

    [Fact]
    public void An_Array_Claiming_Billions_Of_Elements_Does_Not_Allocate()
    {
        // Marker 0xAF = array with the "long count follows" escape, then int32 0x7FFFFFFF.
        // Unbounded, this became `new List<object?>(2147483647)`.
        byte[] body = [0xAF, 0x12, 0x7F, 0xFF, 0xFF, 0xFF];
        var ex = Record.Exception(() => BinaryPlist.Read(Build(body)));
        Assert.IsType<FormatException>(ex);
    }

    [Fact]
    public void A_Dictionary_Claiming_Billions_Of_Entries_Does_Not_Allocate()
    {
        byte[] body = [0xDF, 0x12, 0x7F, 0xFF, 0xFF, 0xFF];
        Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(Build(body))));
    }

    [Fact]
    public void An_Oversized_Data_Length_Is_Rejected_As_FormatException()
    {
        // Declares 0x7FFFFFFF bytes of data in a buffer that holds a handful.
        byte[] body = [0x4F, 0x12, 0x7F, 0xFF, 0xFF, 0xFF];
        Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(Build(body))));
    }

    [Fact]
    public void An_Oversized_String_Length_Is_Rejected_As_FormatException()
    {
        byte[] body = [0x5F, 0x12, 0x7F, 0xFF, 0xFF, 0xFF];   // ASCII string
        Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(Build(body))));

        byte[] utf16 = [0x6F, 0x12, 0x40, 0x00, 0x00, 0x00];  // UTF-16, count*2 overflows the buffer
        Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(Build(utf16))));
    }

    [Fact]
    public void A_Truncated_Length_Field_Is_Rejected_As_FormatException()
    {
        // Escape marker says "int32 count follows" but the buffer ends immediately.
        byte[] body = [0xAF, 0x12];
        Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(Build(body))));
    }

    [Theory]
    [InlineData(new byte[] { })]                                  // empty
    [InlineData(new byte[] { 0x00 })]                             // too short for a trailer
    [InlineData(new byte[] { 0x62, 0x6F, 0x67, 0x75, 0x73 })]     // wrong magic
    public void Garbage_Input_Is_Rejected_As_FormatException(byte[] raw)
        => Assert.IsType<FormatException>(Record.Exception(() => BinaryPlist.Read(raw)));

    [Fact]
    public void Random_Fuzz_Never_Escapes_As_Anything_But_FormatException()
    {
        // The guarantee callers depend on: whatever a device sends, the failure is a
        // FormatException they already handle — never a range exception that kills their loop.
        var rng = new Random(20260811); // fixed seed → reproducible
        byte[] buf = new byte[256];
        for (int i = 0; i < 2000; i++)
        {
            rng.NextBytes(buf);
            Encoding.ASCII.GetBytes("bplist00").CopyTo(buf, 0); // get past the magic check
            var ex = Record.Exception(() => BinaryPlist.Read(buf));
            if (ex is not null)
                Assert.IsType<FormatException>(ex);
        }
    }

    [Fact]
    public void A_Valid_Plist_Still_Round_Trips()
    {
        // The hardening must not reject legitimate data.
        byte[] encoded = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["name"] = "Living Room",
            ["port"] = 7000L,
            ["on"] = true,
        });
        var dict = BinaryPlist.ReadDictionary(encoded);
        Assert.Equal("Living Room", dict["name"]);
        Assert.Equal(7000L, dict["port"]);
        Assert.Equal(true, dict["on"]);
    }
}
