// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

public class DaapMetadataTests
{
    private static (string Code, byte[] Payload) ReadTag(ReadOnlySpan<byte> data, int offset)
    {
        string code = Encoding.ASCII.GetString(data.Slice(offset, 4));
        int len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset + 4, 4));
        return (code, data.Slice(offset + 8, len).ToArray());
    }

    [Fact]
    public void Encode_WrapsFieldsInMlitContainer()
    {
        byte[] body = DaapMetadata.Encode("Song Title", "The Artist", "The Album");

        var (code, mlit) = ReadTag(body, 0);
        Assert.Equal("mlit", code);

        var (c1, p1) = ReadTag(mlit, 0);
        Assert.Equal("minm", c1);
        Assert.Equal("Song Title", Encoding.UTF8.GetString(p1));

        int off = 8 + p1.Length;
        var (c2, p2) = ReadTag(mlit, off);
        Assert.Equal("asar", c2);
        Assert.Equal("The Artist", Encoding.UTF8.GetString(p2));

        off += 8 + p2.Length;
        var (c3, p3) = ReadTag(mlit, off);
        Assert.Equal("asal", c3);
        Assert.Equal("The Album", Encoding.UTF8.GetString(p3));
    }

    [Fact]
    public void Encode_OmitsEmptyFields()
    {
        byte[] body = DaapMetadata.Encode("Only Title", null, "");
        var (_, mlit) = ReadTag(body, 0);
        var (code, payload) = ReadTag(mlit, 0);
        Assert.Equal("minm", code);
        Assert.Equal("Only Title", Encoding.UTF8.GetString(payload));
        Assert.Equal(8 + payload.Length, mlit.Length); // no other tags
    }

    [Fact]
    public void Encode_Utf8MultibyteLengthIsByteCount()
    {
        byte[] body = DaapMetadata.Encode("café", null, null);
        var (_, mlit) = ReadTag(body, 0);
        var (_, payload) = ReadTag(mlit, 0);
        Assert.Equal(5, payload.Length); // 'é' is 2 UTF-8 bytes
        Assert.Equal("café", Encoding.UTF8.GetString(payload));
    }
}
