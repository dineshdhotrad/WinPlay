// SPDX-License-Identifier: GPL-3.0-or-later
// Portions derived from doubletake (LGPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using System.Security.Cryptography;

namespace WinPlay.Core.Fairplay;

public sealed class FairplayException(string message) : Exception(message);

/// <summary>
/// Result of a completed FairPlay SAP handshake: the per-session stream key material a
/// mirroring session needs. <see cref="Ekey"/> is sent to the receiver in the stream
/// SETUP body; the receiver derives the same <see cref="StreamKey"/> from (m3, ekey).
/// </summary>
public sealed class FairplaySession
{
    /// <summary>The 72-byte FPLY-framed ekey to send in the SETUP stream body ("ekey").</summary>
    public required byte[] Ekey { get; init; }

    /// <summary>Random 16-byte stream IV (sent as "eiv").</summary>
    public required byte[] StreamIv { get; init; }

    /// <summary>Raw unwrapped AES key (before optional pair-verify mixing).</summary>
    public required byte[] AesKey { get; init; }

    /// <summary>
    /// Final stream key: when a pair-verify shared secret is present, SHA-512(aesKey ‖
    /// sharedSecret)[..16]; otherwise the raw unwrapped key.
    /// </summary>
    public required byte[] StreamKey { get; init; }
}

/// <summary>
/// Drives Apple's FairPlay SAP (Session Authentication Protocol) handshake for AirPlay
/// screen mirroring: POST m1 → m2, compute m3 (white-box exchange), POST m3 → m4, then
/// unwrap the stream key. This is the hardest part of the protocol to implement;
/// the whitebox core is a clean-room port of doubletake and is verified offline against
/// golden vectors. Only Apple TV / AirPlay-2 TVs implement /fp-setup — HomePods 404 it.
/// </summary>
public static class FairplayHandshake
{
    /// <summary>Posts a body to /fp-setup (X-Apple-ET: 32) and returns the response body.</summary>
    public delegate Task<byte[]> PostFpSetupAsync(byte[] body, CancellationToken ct);

    // Fixed first message of the SAP exchange.
    private static readonly byte[] M1 = Convert.FromHexString("46504c590301010000000004020003bb");

    public static async Task<FairplaySession> PerformAsync(PostFpSetupAsync post,
        byte[]? pairVerifySharedSecret, CancellationToken ct)
    {
        byte[] m2 = await post(M1, ct).ConfigureAwait(false);
        if (m2.Length < 142)
            throw new FairplayException($"fp-setup m2 too short: {m2.Length} bytes");

        byte[] m3 = FairplaySap.ExchangeM3(m2);
        byte[] m4 = await post(m3, ct).ConfigureAwait(false);
        if (m4.Length < 12)
            throw new FairplayException($"fp-setup m4 too short: {m4.Length} bytes");

        byte[] iv = RandomNumberGenerator.GetBytes(16);
        byte[] ekey = BuildEkey();
        byte[] aesKey = FairplayKdf.UnwrapKey(m3, ekey);

        byte[] streamKey;
        if (pairVerifySharedSecret is { Length: > 0 })
        {
            byte[] hashed = SHA512.HashData([.. aesKey, .. pairVerifySharedSecret]);
            streamKey = hashed[..16];
        }
        else
        {
            streamKey = aesKey;
        }

        return new FairplaySession
        {
            Ekey = ekey,
            StreamIv = iv,
            AesKey = aesKey,
            StreamKey = streamKey,
        };
    }

    /// <summary>
    /// Builds a 72-byte FPLY-framed ekey with per-session random chunks so the unwrapped
    /// key is unique each session (doubletake buildEkey format).
    /// </summary>
    internal static byte[] BuildEkey()
    {
        byte[] ekey = new byte[72];
        "FPLY"u8.CopyTo(ekey);
        ekey[4] = 0x01; ekey[5] = 0x02; ekey[6] = 0x01; ekey[7] = 0x00;
        ekey[11] = 0x3c; // 0x0000003c = 60 remaining bytes
        RandomNumberGenerator.Fill(ekey.AsSpan(16, 16));
        RandomNumberGenerator.Fill(ekey.AsSpan(56, 16));
        return ekey;
    }
}
