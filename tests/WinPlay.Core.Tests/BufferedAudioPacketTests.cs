// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Proves the AirPlay 2 buffered-audio frame (Task C4) is exactly what a receiver expects, by
/// parsing and decrypting it the same way airplay2-receiver's RTP_BUFFERED does: 24-bit sequence
/// in bytes 1–3, timestamp/SSRC, ChaCha20-Poly1305 with a 12-byte nonce of four zeros + the
/// 8-byte trailer, AAD = the timestamp+SSRC header bytes. A full build→parse→decrypt round-trip
/// recovering the original ALAC payload is the strongest verification possible without a receiver.
/// </summary>
public class BufferedAudioPacketTests
{
    private static byte[] Key()
    {
        byte[] k = new byte[32];
        for (int i = 0; i < k.Length; i++) k[i] = (byte)(i * 7 + 1);
        return k;
    }

    [Fact]
    public void Frame_Length_Prefix_Matches_The_Whole_Frame()
    {
        using var builder = new BufferedAudioPacket(Key());
        byte[] alac = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        byte[] frame = builder.BuildFrame(sequence24: 5, timestamp: 1000, ssrc: 0, alac);

        int prefix = BinaryPrimitives.ReadUInt16BigEndian(frame);
        Assert.Equal(frame.Length, prefix);
        Assert.Equal(BufferedAudioPacket.FrameLength(alac.Length), frame.Length);
    }

    [Fact]
    public void Header_Encodes_A_24Bit_Sequence_Timestamp_And_Ssrc()
    {
        using var builder = new BufferedAudioPacket(Key());
        byte[] frame = builder.BuildFrame(sequence24: 0x123456, timestamp: 0xDEADBEEF, ssrc: 0x11223344, [0xAA, 0xBB]);
        Span<byte> rtp = frame.AsSpan(2);

        Assert.Equal(0x80, rtp[0]);
        int seq = (rtp[1] << 16) | (rtp[2] << 8) | rtp[3]; // exactly how RTP_BUFFERED reads it
        Assert.Equal(0x123456, seq);
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32BigEndian(rtp[4..]));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(rtp[8..]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(352)]
    [InlineData(4096)]
    public void RoundTrips_Through_The_Receivers_Exact_Parse_And_Decrypt(int payloadLength)
    {
        byte[] key = Key();
        byte[] alac = new byte[payloadLength];
        RandomNumberGenerator.Fill(alac);

        using var builder = new BufferedAudioPacket(key);
        uint seq = 0x0A0B0C, ts = 0x00100000, ssrc = 0x22446688;
        byte[] frame = builder.BuildFrame(seq, ts, ssrc, alac);

        // --- Parse exactly as airplay2-receiver RTP_BUFFERED does ---
        int declaredLen = BinaryPrimitives.ReadUInt16BigEndian(frame);
        byte[] data = frame[2..declaredLen];             // the RTP packet (recv(data_len - 2))

        Assert.Equal(2, data[0] >> 6);                   // RTP version 2
        int parsedSeq = (data[1] << 16) | (data[2] << 8) | data[3];
        uint parsedTs = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        uint parsedSsrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));

        ReadOnlySpan<byte> nonce8 = data.AsSpan(data.Length - 8);       // data[-8:]
        ReadOnlySpan<byte> tag = data.AsSpan(data.Length - 24, 16);     // data[-24:-8]
        ReadOnlySpan<byte> aad = data.AsSpan(4, 8);                     // data[4:12]
        ReadOnlySpan<byte> ciphertext = data.AsSpan(12, data.Length - 24 - 12); // data[12:-24]

        // Receiver reconstructs the 12-byte nonce as four zero bytes + the 8-byte trailer.
        Span<byte> nonce12 = stackalloc byte[12];
        nonce12.Clear();
        nonce8.CopyTo(nonce12[4..]);

        byte[] recovered = new byte[ciphertext.Length];
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Decrypt(nonce12, ciphertext, tag, recovered, aad); // throws on auth failure

        Assert.Equal((int)seq, parsedSeq);
        Assert.Equal(ts, parsedTs);
        Assert.Equal(ssrc, parsedSsrc);
        Assert.Equal(alac, recovered);
    }

    [Fact]
    public void Tampering_With_The_Ciphertext_Fails_Authentication()
    {
        byte[] key = Key();
        using var builder = new BufferedAudioPacket(key);
        byte[] frame = builder.BuildFrame(1, 1000, 0, [9, 9, 9, 9]);
        byte[] data = frame[2..];
        data[13] ^= 0xFF; // flip a ciphertext byte

        byte[] nonce12 = new byte[12];
        Array.Copy(data, data.Length - 8, nonce12, 4, 8);
        byte[] tag = data[(data.Length - 24)..(data.Length - 8)];
        byte[] aad = data[4..12];
        byte[] ciphertext = data[12..(data.Length - 24)];

        byte[] outBuf = new byte[ciphertext.Length];
        using var cipher = new ChaCha20Poly1305(key);
        Assert.Throws<AuthenticationTagMismatchException>(() =>
            cipher.Decrypt(nonce12, ciphertext, tag, outBuf, aad));
    }
}
