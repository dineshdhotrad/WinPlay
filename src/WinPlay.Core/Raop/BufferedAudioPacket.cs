// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinPlay.Core.Raop;

/// <summary>
/// Builds one AirPlay 2 <em>buffered</em>-audio TCP frame (stream type 103). Unlike the realtime
/// UDP stream, buffered audio flows over a TCP data connection where each RTP packet is preceded
/// by a big-endian <c>uint16</c> giving the whole frame's length (prefix included), and the RTP
/// header carries a <b>24-bit</b> sequence number in bytes 1–3 (byte 0 = 0x80), rather than
/// realtime's 16-bit sequence + payload-type byte.
///
/// <para>The encryption is identical to the realtime path (that convention is verified against
/// real receivers): ChaCha20-Poly1305 over the ALAC payload, a 12-byte IETF nonce of four zero
/// bytes followed by the 8-byte trailer, AAD = the timestamp+SSRC bytes (4..12), and the packet
/// ends with the 16-byte tag then the 8-byte nonce trailer. The receiver reconstructs the nonce
/// as <c>0000 + trailer</c> and reads the AAD from the header, matching
/// <c>airplay2-receiver</c>'s <c>RTP_BUFFERED</c> parsing exactly.</para>
///
/// <para>Frame layout: <c>[len:uint16 BE][0x80][seq:24 BE][ts:32 BE][ssrc:32 BE][ciphertext][tag:16][nonce:8]</c></para>
/// </summary>
public sealed class BufferedAudioPacket(byte[] audioKey) : IDisposable
{
    private readonly ChaCha20Poly1305 _cipher = new(audioKey);

    /// <summary>Number of bytes a frame occupies for a payload of <paramref name="alacLength"/> bytes.</summary>
    public static int FrameLength(int alacLength) => 2 + 12 + alacLength + 16 + 8;

    /// <summary>
    /// Builds the length-prefixed buffered frame. The 8-byte nonce trailer encodes the sequence
    /// (little-endian), so a receiver that reads the trailer and one that reconstructs the nonce
    /// from the sequence agree.
    /// </summary>
    public byte[] BuildFrame(uint sequence24, uint timestamp, uint ssrc, ReadOnlySpan<byte> alac,
        ulong nonceCounter = 0)
    {
        int rtpLength = 12 + alac.Length + 16 + 8;
        int frameLength = 2 + rtpLength;
        byte[] frame = new byte[frameLength];

        // uint16 length prefix = the entire frame length (prefix included).
        BinaryPrimitives.WriteUInt16BigEndian(frame, (ushort)frameLength);

        Span<byte> rtp = frame.AsSpan(2);
        rtp[0] = 0x80;
        rtp[1] = (byte)(sequence24 >> 16);   // 24-bit sequence, big-endian, in bytes 1..3
        rtp[2] = (byte)(sequence24 >> 8);
        rtp[3] = (byte)sequence24;
        BinaryPrimitives.WriteUInt32BigEndian(rtp[4..], timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(rtp[8..], ssrc);

        // 12-byte IETF nonce: four zero bytes + the 8-byte trailer (here, the sequence, LE).
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        // The receiver reconstructs the nonce from the packet trailer, so its value only has to
        // be unique per key — a 64-bit counter never wraps, where a sequence-derived nonce
        // repeats after 2²³ packets. Falls back to the sequence when no counter is supplied
        // (test fixtures).
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], nonceCounter != 0 ? nonceCounter : sequence24);

        _cipher.Encrypt(
            nonce,
            alac,
            rtp.Slice(12, alac.Length),                 // ciphertext
            rtp.Slice(12 + alac.Length, 16),            // tag
            rtp[4..12]);                                // AAD = timestamp + SSRC

        nonce[4..].CopyTo(rtp[(12 + alac.Length + 16)..]); // 8-byte nonce trailer
        return frame;
    }

    public void Dispose() => _cipher.Dispose();
}
