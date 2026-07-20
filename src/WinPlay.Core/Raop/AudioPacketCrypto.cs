// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinPlay.Core.Raop;

/// <summary>
/// Per-packet RTP audio encryption for AirPlay 2 realtime streams:
/// ChaCha20-Poly1305 with the 32-byte audio key (first 32 bytes of the SRP shared
/// secret for transient pairing). Packet layout:
///   [12-byte RTP header][ciphertext][16-byte tag][8-byte nonce suffix]
/// AAD = RTP header bytes 4..12 (timestamp + SSRC). The nonce is derived from the RTP
/// sequence number — [0,0,0,0, seq_lo, seq_hi, 0,0,0,0,0,0] — matching owntone; Apple
/// receivers reconstruct it from the sequence number, so an independent counter fails
/// authentication (symptom: zero audio, session dropped after ~30 s).
/// </summary>
public sealed class AudioPacketCrypto(byte[] audioKey) : IDisposable
{
    private readonly ChaCha20Poly1305 _cipher = new(audioKey);

    public byte[] BuildPacket(ushort sequence, uint timestamp, uint ssrc, bool firstPacket,
        ReadOnlySpan<byte> payload)
    {
        byte[] packet = new byte[12 + payload.Length + 16 + 8];
        Span<byte> header = packet.AsSpan(0, 12);
        header[0] = 0x80;
        header[1] = firstPacket ? (byte)0xE0 : (byte)0x60; // marker | PT=96
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], ssrc);

        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(nonce[4..], sequence);

        _cipher.Encrypt(nonce,
            payload,
            packet.AsSpan(12, payload.Length),
            packet.AsSpan(12 + payload.Length, 16),
            header[4..12]);

        // Trailing 8 bytes = nonce[4..12] (seq_lo, seq_hi, six zeros).
        nonce[4..].CopyTo(packet.AsSpan(12 + payload.Length + 16, 8));
        return packet;
    }

    public void Dispose() => _cipher.Dispose();
}
