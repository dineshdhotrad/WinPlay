// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WinPlay.Core.Mirror;

/// <summary>
/// Screen-mirroring video wire format and per-frame encryption (doubletake mirror.go
/// parity). Frames go over the TCP data channel as a fixed 128-byte little-endian
/// header followed by the payload. Apple TV sessions (pair-verify + FairPlay) encrypt
/// video with ChaCha20-Poly1305 keyed by HKDF-SHA512; the 128-byte header is the AEAD
/// associated data. The SPS/PPS "codec" packet is sent once, unencrypted, in avcC form.
/// </summary>
public static class MirrorVideoStream
{
    public const int HeaderLength = 128;

    // Payload types (header[4]).
    private const byte PayloadVideo = 0x00;
    private const byte PayloadCodec = 0x01;

    /// <summary>
    /// HKDF-SHA512 data-stream key for encrypted screen video (Apple's
    /// _GetDataStreamSecurityKeys). IKM is the pair-verify ECDH shared secret when
    /// available, else the raw FairPlay AES key.
    /// </summary>
    public static byte[] DeriveDataStreamKey(byte[] ikm, long streamConnectionId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, outputLength: 32,
            salt: Encoding.ASCII.GetBytes($"DataStream-Salt{(ulong)streamConnectionId}"),
            info: "DataStream-Output-Encryption-Key"u8.ToArray());

    /// <summary>
    /// Legacy AES-128-CTR key/IV (UxPlay path): SHA-512("AirPlayStreamKey&lt;id&gt;" ‖ shk)[..16]
    /// and SHA-512("AirPlayStreamIV&lt;id&gt;" ‖ shk)[..16].
    /// </summary>
    public static (byte[] Key, byte[] Iv) DeriveAesCtrKeys(byte[] shk, long streamConnectionId)
    {
        byte[] key = Sha512Prefixed($"AirPlayStreamKey{(ulong)streamConnectionId}", shk)[..16];
        byte[] iv = Sha512Prefixed($"AirPlayStreamIV{(ulong)streamConnectionId}", shk)[..16];
        return (key, iv);
    }

    private static byte[] Sha512Prefixed(string ascii, byte[] tail)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
        sha.AppendData(Encoding.ASCII.GetBytes(ascii));
        sha.AppendData(tail);
        return sha.GetHashAndReset();
    }

    /// <summary>Builds the 128-byte header for an encrypted H.264 access unit (AVCC).</summary>
    public static byte[] BuildVideoHeader(int payloadLength, bool keyframe, ulong ntpTimestamp)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = PayloadVideo;
        header[5] = keyframe ? (byte)0x10 : (byte)0x00;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpTimestamp);
        return header;
    }

    /// <summary>Builds the 128-byte header for the unencrypted SPS/PPS codec packet.</summary>
    public static byte[] BuildCodecHeader(int payloadLength, ulong ntpTimestamp,
        float sourceWidth, float sourceHeight, float displayWidth, float displayHeight)
    {
        byte[] header = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payloadLength);
        header[4] = PayloadCodec;
        header[5] = 0x00;
        header[6] = 0x16; // H.264 SPS+PPS option
        header[7] = 0x01;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), ntpTimestamp);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(16), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(20), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(40), sourceWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(44), sourceHeight);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(56), displayWidth);
        BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(60), displayHeight);
        return header;
    }
}

/// <summary>
/// Per-frame ChaCha20-Poly1305 sealer for mirror video. Uses a 64-bit little-endian
/// frame counter placed at nonce bytes 4..12 (nonce[0..4]=0), matching the receiver's
/// 64×64 ChaCha state layout, with the 128-byte packet header as AEAD associated data.
/// </summary>
public sealed class MirrorVideoCipher(byte[] key) : IDisposable
{
    private readonly ChaCha20Poly1305 _cipher = new(key);
    private ulong _nonceCounter;

    /// <summary>Encrypts <paramref name="plaintext"/>, returning ciphertext followed by the 16-byte tag.</summary>
    public byte[] Seal(ReadOnlySpan<byte> header, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonceCounter++);

        byte[] output = new byte[plaintext.Length + 16];
        _cipher.Encrypt(nonce, plaintext, output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, 16), header);
        return output;
    }

    public void Dispose() => _cipher.Dispose();
}
