// SPDX-License-Identifier: GPL-3.0-or-later
// Portions derived from doubletake (LGPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using System.Security.Cryptography;
using static WinPlay.Core.Fairplay.FairplayPrimitives;

namespace WinPlay.Core.Fairplay;

/// <summary>
/// FairPlay key-derivation and key-unwrap (fairplay_crypto.go). Derives the AES-128
/// wrapping key from the FairPlay message with the modified-MD5/SAP-hash construction,
/// then unwraps the per-session stream key. Verified against golden vectors.
/// </summary>
internal static class FairplayKdf
{
    internal static byte[] DeriveWrappingKey(ReadOnlySpan<byte> sapTail, ReadOnlySpan<byte> message)
    {
        Span<byte> decrypted = stackalloc byte[128];
        DecryptMessage(message, decrypted);

        byte[] material = new byte[320];
        int offset = 0;
        KdfPrefix.CopyTo(material, offset); offset += KdfPrefix.Length;          // 17
        decrypted.CopyTo(material.AsSpan(offset)); offset += 128;                 // 128
        sapTail[..128].CopyTo(material.AsSpan(offset)); offset += 128;            // 128
        KdfSuffix.CopyTo(material, offset); offset += KdfSuffix.Length;           // 17 → 290
        material[offset] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(material.AsSpan(material.Length - 8), (ulong)offset * 8);

        uint[] state = WordsFromLittleEndian(InitialSessionKey);
        for (int off = 0; off < material.Length; off += 64)
        {
            ReadOnlySpan<byte> block = material.AsSpan(off, 64);
            uint[] modified = Md5Compress(state, block, Md5Mutation.Kdf);
            byte[] hashed = SapState.Compute(block);
            for (int word = 0; word < 4; word++)
                state[word] = modified[word] + BinaryPrimitives.ReadUInt32LittleEndian(hashed.AsSpan(word * 4));
        }
        return WordsBigEndian(state);
    }

    /// <summary>
    /// Unwraps the session AES key: decrypt ekey[56:72] with the wrapping key derived
    /// from m3, then XOR with ekey[16:32]. Sender and receiver compute the same value.
    /// </summary>
    internal static byte[] UnwrapKey(ReadOnlySpan<byte> m3, ReadOnlySpan<byte> ekey)
    {
        byte[] aesKey = DeriveWrappingKey(DefaultSapTail, m3);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = aesKey;

        byte[] keyOut = aes.DecryptEcb(ekey.Slice(56, 16), PaddingMode.None);
        for (int i = 0; i < 16; i++) keyOut[i] ^= ekey[16 + i];
        return keyOut;
    }
}
