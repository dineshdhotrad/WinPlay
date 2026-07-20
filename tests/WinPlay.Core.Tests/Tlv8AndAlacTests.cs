// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Hap;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

public class Tlv8AndAlacTests
{
    [Fact]
    public void Tlv_Roundtrips_Small_Values()
    {
        byte[] encoded = Tlv8.Encode(
        [
            (TlvType.Method, [0x00]),
            (TlvType.State, [0x01]),
            (TlvType.Flags, [0x10]),
        ]);
        Assert.Equal(new byte[] { 0x00, 1, 0x00, 0x06, 1, 0x01, 0x13, 1, 0x10 }, encoded);

        var decoded = Tlv8.Decode(encoded);
        Assert.Equal(3, decoded.Count);
        Assert.Equal([0x01], Tlv8.Find(decoded, TlvType.State));
    }

    [Fact]
    public void Tlv_Fragments_And_Reassembles_384_Byte_Srp_Key()
    {
        byte[] publicKey = Enumerable.Range(0, 384).Select(i => (byte)i).ToArray();
        byte[] encoded = Tlv8.Encode([(TlvType.PublicKey, publicKey)]);

        // 384 bytes → fragments of 255 + 129, each with its own header.
        Assert.Equal(2 + 255 + 2 + 129, encoded.Length);
        Assert.Equal(TlvType.PublicKey, encoded[0]);
        Assert.Equal(255, encoded[1]);
        Assert.Equal(TlvType.PublicKey, encoded[2 + 255]);
        Assert.Equal(129, encoded[2 + 255 + 1]);

        var decoded = Tlv8.Decode(encoded);
        Assert.Equal(publicKey, Tlv8.Find(decoded, TlvType.PublicKey));
    }

    [Fact]
    public void Tlv_Distinct_Types_Are_Not_Merged()
    {
        byte[] encoded = Tlv8.Encode([(TlvType.Salt, new byte[16]), (TlvType.PublicKey, new byte[32])]);
        var decoded = Tlv8.Decode(encoded);
        Assert.Equal(16, Tlv8.Find(decoded, TlvType.Salt)!.Length);
        Assert.Equal(32, Tlv8.Find(decoded, TlvType.PublicKey)!.Length);
    }

    [Fact]
    public void Alac_Verbatim_Frame_Has_Correct_Size_And_Header_Bits()
    {
        var samples = new short[352 * 2];
        samples[0] = unchecked((short)0xABCD); // L0
        samples[1] = 0x1234;                   // R0

        byte[] frame = AlacFramer.WrapPcmFrame(samples);

        // 23 header bits + 11264 sample bits + 3 end bits = 11290 bits → 1412 bytes.
        Assert.Equal(1412, frame.Length);

        // Header: 001 0000 000000000000 0 00 1 → bytes 0b0010_0000 0b0000_0000 0b0000_001?
        Assert.Equal(0x20, frame[0]);
        Assert.Equal(0x00, frame[1]);
        // Bits 16..22 = 0000001 (last header bit = isNotCompressed), bit 23 = first sample MSB.
        // L0 = 0xABCD = 1010101111001101 → first bit 1 ⇒ byte2 = 0000_0011.
        Assert.Equal(0x03, frame[2]);
        // Remaining 15 bits of L0 (010101111001101) + first bit of R0 (0) → 01010111 10011010
        Assert.Equal(0x57, frame[3]);
        Assert.Equal(0x9A, frame[4]);
    }

    [Fact]
    public void Alac_Rejects_Wrong_Sample_Count()
    {
        Assert.Throws<ArgumentException>(() => AlacFramer.WrapPcmFrame(new short[100]));
    }
}
