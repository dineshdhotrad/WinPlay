// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace WinPlay.Core.Hap;

/// <summary>Result of a successful pair-setup: the shared secret and derived channel keys.</summary>
public sealed class HapSession
{
    /// <summary>SRP session key K (64 bytes for transient pairing).</summary>
    public required byte[] SharedSecret { get; init; }

    public byte[] ControlWriteKey => Derive("Control-Salt", "Control-Write-Encryption-Key");
    public byte[] ControlReadKey => Derive("Control-Salt", "Control-Read-Encryption-Key");
    public byte[] EventsWriteKey => Derive("Events-Salt", "Events-Write-Encryption-Key");
    public byte[] EventsReadKey => Derive("Events-Salt", "Events-Read-Encryption-Key");

    /// <summary>
    /// Audio payload key: for transient pairing K is 64 bytes but ONLY the first 32 are
    /// used — sending/deriving anything else yields silent audio and a ~30 s session drop
    /// (owntone: AIRPLAY_AUDIO_KEY_LEN = 32).
    /// </summary>
    public byte[] AudioKey => SharedSecret[..32];

    private byte[] Derive(string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, SharedSecret,
            outputLength: 32,
            salt: System.Text.Encoding.UTF8.GetBytes(salt),
            info: System.Text.Encoding.UTF8.GetBytes(info));
}

public sealed class HapPairingException(string message) : Exception(message);

/// <summary>
/// HAP transient pair-setup: SRP-6a over POST /pair-setup with the
/// fixed PIN 3939, Flags=0x10, header X-Apple-HKP: 4. No credentials are stored and no
/// pair-verify step is needed afterward. The transport callback posts a TLV8 body to
/// /pair-setup and returns the response body.
/// </summary>
public static class HapTransientPairing
{
    public const string FixedPin = "3939";
    private const byte MethodPairSetup = 0x00;
    private const byte FlagTransient = 0x10;

    public static async Task<HapSession> PairAsync(
        Func<byte[], CancellationToken, Task<byte[]>> postPairSetup,
        CancellationToken ct)
    {
        // M1 →
        byte[] m1 = Tlv8.Encode(
        [
            (TlvType.Method, [MethodPairSetup]),
            (TlvType.State, [0x01]),
            (TlvType.Flags, [FlagTransient]),
        ]);
        var m2 = Tlv8.Decode(await postPairSetup(m1, ct).ConfigureAwait(false));
        ThrowOnError(m2, "M2");

        byte[] salt = Tlv8.Find(m2, TlvType.Salt) ?? throw new HapPairingException("M2 missing SRP salt");
        byte[] serverB = Tlv8.Find(m2, TlvType.PublicKey) ?? throw new HapPairingException("M2 missing server public key");

        var srp = SrpClient.ForPairSetup(FixedPin);
        srp.SetServerParams(salt, serverB);

        // M3 →
        byte[] m3 = Tlv8.Encode(
        [
            (TlvType.State, [0x03]),
            (TlvType.PublicKey, srp.PublicA),
            (TlvType.Proof, srp.ClientProof),
        ]);
        var m4 = Tlv8.Decode(await postPairSetup(m3, ct).ConfigureAwait(false));
        ThrowOnError(m4, "M4");

        byte[] serverProof = Tlv8.Find(m4, TlvType.Proof) ?? throw new HapPairingException("M4 missing server proof");
        if (!srp.VerifyServerProof(serverProof))
            throw new HapPairingException("server SRP proof (M2) verification failed");

        return new HapSession { SharedSecret = srp.SessionKey };
    }

    private static void ThrowOnError(List<(byte Type, byte[] Value)> tlv, string stage)
    {
        byte[]? err = Tlv8.Find(tlv, TlvType.Error);
        if (err is { Length: > 0 })
            throw new HapPairingException($"pair-setup {stage}: {TlvError.Describe(err[0])}");
    }
}
