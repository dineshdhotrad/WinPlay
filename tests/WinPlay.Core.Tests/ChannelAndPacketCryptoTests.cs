// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinPlay.Core.Net;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

public class ChannelAndPacketCryptoTests
{
    private static (ChannelCrypto a, ChannelCrypto b) Pair()
    {
        byte[] k1 = RandomNumberGenerator.GetBytes(32);
        byte[] k2 = RandomNumberGenerator.GetBytes(32);
        return (new ChannelCrypto(outKey: k1, inKey: k2), new ChannelCrypto(outKey: k2, inKey: k1));
    }

    [Fact]
    public void Frames_Roundtrip_Including_Multi_Chunk()
    {
        var (alice, bob) = Pair();
        byte[] message = RandomNumberGenerator.GetBytes(3000); // > 2 × 1024 chunks

        byte[] wire = alice.Encrypt(message);
        var output = new MemoryStream();
        int consumed = bob.DecryptFrames(wire, output);

        Assert.Equal(wire.Length, consumed);
        Assert.Equal(message, output.ToArray());
    }

    [Fact]
    public void Partial_Frames_Wait_For_More_Bytes()
    {
        var (alice, bob) = Pair();
        byte[] wire = alice.Encrypt("hello receiver"u8.ToArray());

        var output = new MemoryStream();
        int consumed = bob.DecryptFrames(wire.AsSpan(0, wire.Length - 5), output);
        Assert.Equal(0, consumed);
        Assert.Equal(0, output.Length);

        consumed = bob.DecryptFrames(wire, output);
        Assert.Equal(wire.Length, consumed);
        Assert.Equal("hello receiver"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public void Tampering_Fails_Authentication()
    {
        var (alice, bob) = Pair();
        byte[] wire = alice.Encrypt("secret"u8.ToArray());
        wire[^1] ^= 0xFF;
        Assert.ThrowsAny<CryptographicException>(() => bob.DecryptFrames(wire, new MemoryStream()));
    }

    [Fact]
    public void Sequential_Messages_Use_Advancing_Counters()
    {
        var (alice, bob) = Pair();
        var output = new MemoryStream();
        bob.DecryptFrames(alice.Encrypt("one"u8.ToArray()), output);
        bob.DecryptFrames(alice.Encrypt("two"u8.ToArray()), output);
        Assert.Equal("onetwo"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public void Audio_Packet_Layout_And_Decryptability()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        using var crypto = new AudioPacketCrypto(key);
        byte[] payload = RandomNumberGenerator.GetBytes(1412);

        byte[] packet = crypto.BuildPacket(sequence: 0x1234, timestamp: 0xAABBCCDD,
            ssrc: 0x55667788, firstPacket: true, payload);

        Assert.Equal(12 + 1412 + 16 + 8, packet.Length);
        Assert.Equal(0x80, packet[0]);
        Assert.Equal(0xE0, packet[1]); // first packet: marker + PT 96
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8)));

        // Receiver-side decrypt reconstructs the nonce FROM THE SEQUENCE NUMBER
        // (owntone convention) — trailing bytes must agree.
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(nonce[4..], 0x1234);
        Assert.True(nonce[4..].SequenceEqual(packet.AsSpan(^8)));
        byte[] plain = new byte[1412];
        using var receiver = new ChaCha20Poly1305(key);
        receiver.Decrypt(nonce, packet.AsSpan(12, 1412), packet.AsSpan(12 + 1412, 16), plain,
            packet.AsSpan(4, 8));
        Assert.Equal(payload, plain);

        // Second packet: marker cleared, nonce follows the new sequence number.
        byte[] second = crypto.BuildPacket(0x1235, 0xAABBCEDD, 0x55667788, false, payload);
        Assert.Equal(0x60, second[1]);
        Assert.Equal(0x1235, BinaryPrimitives.ReadUInt16LittleEndian(second.AsSpan(^8)));
    }
}
