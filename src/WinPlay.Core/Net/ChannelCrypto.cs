// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WinPlay.Core.Net;

/// <summary>
/// Post-pairing channel encryption (control + event channels): ChaCha20-Poly1305 frames of
/// <c>[2-byte LE plaintext length][ciphertext][16-byte tag]</c>, AAD = the 2 length bytes,
/// per-direction 64-bit counter nonces (little-endian at bytes 4..11 of the 12-byte nonce)
/// starting at 0. Max 1024 plaintext bytes per frame.
/// </summary>
public sealed class ChannelCrypto : IDisposable
{
    public const int MaxFramePlaintext = 0x400;
    private const int TagLength = 16;

    private readonly ChaCha20Poly1305 _outCipher;
    private readonly ChaCha20Poly1305 _inCipher;
    private ulong _outCounter;
    private ulong _inCounter;

    public ChannelCrypto(byte[] outKey, byte[] inKey)
    {
        _outCipher = new ChaCha20Poly1305(outKey);
        _inCipher = new ChaCha20Poly1305(inKey);
    }

    /// <summary>Encrypts arbitrary-length plaintext into one or more frames.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var ms = new MemoryStream();
        int offset = 0;
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> header = stackalloc byte[2];
        while (offset < plaintext.Length || plaintext.Length == 0)
        {
            int chunk = Math.Min(MaxFramePlaintext, plaintext.Length - offset);
            BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)chunk);

            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _outCounter++);

            byte[] cipher = new byte[chunk];
            byte[] tag = new byte[TagLength];
            _outCipher.Encrypt(nonce, plaintext.Slice(offset, chunk), cipher, tag, header);

            ms.Write(header);
            ms.Write(cipher);
            ms.Write(tag);

            offset += chunk;
            if (plaintext.Length == 0) break;
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Consumes complete frames from the front of <paramref name="received"/>, appending
    /// plaintext to <paramref name="plaintextOut"/>. Returns bytes consumed. Throws
    /// <see cref="CryptographicException"/> on tag failure.
    /// </summary>
    public int DecryptFrames(ReadOnlySpan<byte> received, MemoryStream plaintextOut)
    {
        int consumed = 0;
        Span<byte> nonce = stackalloc byte[12];
        while (received.Length - consumed >= 2)
        {
            int len = BinaryPrimitives.ReadUInt16LittleEndian(received[consumed..]);
            if (len > MaxFramePlaintext)
                throw new CryptographicException($"channel frame length {len} exceeds maximum");
            int frameTotal = 2 + len + TagLength;
            if (received.Length - consumed < frameTotal)
                break; // incomplete frame — wait for more bytes

            ReadOnlySpan<byte> header = received.Slice(consumed, 2);
            ReadOnlySpan<byte> cipher = received.Slice(consumed + 2, len);
            ReadOnlySpan<byte> tag = received.Slice(consumed + 2 + len, TagLength);

            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _inCounter);

            byte[] plain = new byte[len];
            _inCipher.Decrypt(nonce, cipher, tag, plain, header);
            _inCounter++;

            plaintextOut.Write(plain);
            consumed += frameTotal;
        }
        return consumed;
    }

    public void Dispose()
    {
        _outCipher.Dispose();
        _inCipher.Dispose();
    }
}
