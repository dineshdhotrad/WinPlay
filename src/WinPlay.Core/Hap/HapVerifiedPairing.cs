// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace WinPlay.Core.Hap;

/// <summary>
/// Long-lived pairing identity for a PIN-protected receiver (Apple TV). Persisted so
/// later connections use the fast pair-verify handshake instead of a new PIN prompt.
/// </summary>
public sealed record HapPairingCredentials
{
    /// <summary>Receiver's long-term Ed25519 public key (from pair-setup M6).</summary>
    public required byte[] ReceiverPublicKey { get; init; }

    /// <summary>Receiver's pairing identifier (from pair-setup M6).</summary>
    public required byte[] ReceiverId { get; init; }

    /// <summary>Our long-term Ed25519 private seed (32 bytes).</summary>
    public required byte[] OurPrivateKey { get; init; }

    /// <summary>Our pairing identifier (UUID string bytes, generated at pair-setup).</summary>
    public required byte[] OurId { get; init; }
}

/// <summary>
/// HAP pairing for PIN-protected receivers (pyatv parity — header
/// X-Apple-HKP: 3):
/// - Pair-setup: POST /pair-pin-start makes the receiver display a PIN, then SRP-6a
/// M1–M4 with that PIN as password, then M5/M6 exchange Ed25519 long-term identities
/// encrypted with ChaCha20-Poly1305 (nonces "PS-Msg05"/"PS-Msg06").
/// - Pair-verify: on every later connection, X25519 ECDH + Ed25519 signatures (nonces
/// "PV-Msg02"/"PV-Msg03"); the 32-byte shared secret drives the same channel-key
/// derivation as transient pairing.
/// </summary>
public static class HapVerifiedPairing
{
    /// <summary>Posts <c>body</c> to an endpoint (e.g. /pair-setup) and returns the response body.</summary>
    public delegate Task<byte[]> PostAsync(string endpoint, byte[] body, CancellationToken ct);

    // ------------------------------------------------------------ pair-setup (PIN)

    /// <summary>
    /// Runs M1–M2 and returns intermediate state; the receiver is now displaying its
    /// PIN. Complete with <see cref="FinishPairSetupAsync"/> once the user has read it.
    /// </summary>
    public static async Task<PinPairingSession> StartPairSetupAsync(PostAsync post, CancellationToken ct)
    {
        await post("/pair-pin-start", [], ct).ConfigureAwait(false);

        byte[] m1 = Tlv8.Encode(
        [
            (TlvType.Method, [0x00]),
            (TlvType.State, [0x01]),
        ]);
        var m2 = Tlv8.Decode(await post("/pair-setup", m1, ct).ConfigureAwait(false));
        ThrowOnError(m2, "M2");

        byte[] salt = Tlv8.Find(m2, TlvType.Salt) ?? throw new HapPairingException("M2 missing SRP salt");
        byte[] serverB = Tlv8.Find(m2, TlvType.PublicKey) ?? throw new HapPairingException("M2 missing server public key");
        return new PinPairingSession(post, salt, serverB);
    }

    public sealed class PinPairingSession(PostAsync post, byte[] salt, byte[] serverB)
    {
        /// <summary>Completes pair-setup with the PIN currently shown on the receiver.</summary>
        public async Task<HapPairingCredentials> FinishAsync(string pin, CancellationToken ct)
        {
            var srp = SrpClient.ForPairSetup(pin);
            srp.SetServerParams(salt, serverB);

            // M3 → M4: SRP proof exchange (wrong PIN fails here with an Authentication error).
            byte[] m3 = Tlv8.Encode(
            [
                (TlvType.State, [0x03]),
                (TlvType.PublicKey, srp.PublicA),
                (TlvType.Proof, srp.ClientProof),
            ]);
            var m4 = Tlv8.Decode(await post("/pair-setup", m3, ct).ConfigureAwait(false));
            ThrowOnError(m4, "M4 (wrong PIN?)");
            byte[] serverProof = Tlv8.Find(m4, TlvType.Proof) ?? throw new HapPairingException("M4 missing server proof");
            if (!srp.VerifyServerProof(serverProof))
                throw new HapPairingException("server SRP proof verification failed");

            // M5: sign (deviceX ‖ ourId ‖ ourLtpk) with a fresh Ed25519 long-term key.
            byte[] deviceX = Hkdf32(srp.SessionKey, "Pair-Setup-Controller-Sign-Salt", "Pair-Setup-Controller-Sign-Info");
            byte[] sessionKey = Hkdf32(srp.SessionKey, "Pair-Setup-Encrypt-Salt", "Pair-Setup-Encrypt-Info");

            byte[] ourSeed = RandomNumberGenerator.GetBytes(32);
            var ourKey = new Ed25519PrivateKeyParameters(ourSeed);
            byte[] ourLtpk = ourKey.GeneratePublicKey().GetEncoded();
            byte[] ourId = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());

            byte[] deviceInfo = [.. deviceX, .. ourId, .. ourLtpk];
            byte[] subTlv = Tlv8.Encode(
            [
                (TlvType.Identifier, ourId),
                (TlvType.PublicKey, ourLtpk),
                (TlvType.Signature, Sign(ourKey, deviceInfo)),
            ]);

            byte[] m5 = Tlv8.Encode(
            [
                (TlvType.State, [0x05]),
                (TlvType.EncryptedData, EncryptWithNonce(sessionKey, "PS-Msg05", subTlv)),
            ]);
            var m6 = Tlv8.Decode(await post("/pair-setup", m5, ct).ConfigureAwait(false));
            ThrowOnError(m6, "M6");

            byte[] encrypted = Tlv8.Find(m6, TlvType.EncryptedData) ?? throw new HapPairingException("M6 missing encrypted data");
            var receiverTlv = Tlv8.Decode(DecryptWithNonce(sessionKey, "PS-Msg06", encrypted));

            byte[] receiverId = Tlv8.Find(receiverTlv, TlvType.Identifier) ?? throw new HapPairingException("M6 missing receiver id");
            byte[] receiverLtpk = Tlv8.Find(receiverTlv, TlvType.PublicKey) ?? throw new HapPairingException("M6 missing receiver public key");
            byte[] receiverSig = Tlv8.Find(receiverTlv, TlvType.Signature) ?? throw new HapPairingException("M6 missing receiver signature");

            byte[] receiverX = Hkdf32(srp.SessionKey, "Pair-Setup-Accessory-Sign-Salt", "Pair-Setup-Accessory-Sign-Info");
            byte[] receiverInfo = [.. receiverX, .. receiverId, .. receiverLtpk];
            if (!Verify(receiverLtpk, receiverInfo, receiverSig))
                throw new HapPairingException("receiver M6 signature verification failed");

            return new HapPairingCredentials
            {
                ReceiverPublicKey = receiverLtpk,
                ReceiverId = receiverId,
                OurPrivateKey = ourSeed,
                OurId = ourId,
            };
        }
    }

    // ------------------------------------------------------------ pair-verify

    /// <summary>
    /// Fast re-authentication with stored credentials. Returns the session with the
    /// 32-byte X25519 shared secret (drives the same key derivation as transient mode).
    /// </summary>
    public static async Task<HapSession> PairVerifyAsync(PostAsync post,
        HapPairingCredentials credentials, CancellationToken ct)
    {
        var ephemeral = new X25519PrivateKeyParameters(new SecureRandom());
        byte[] ourPub = ephemeral.GeneratePublicKey().GetEncoded();

        byte[] m1 = Tlv8.Encode(
        [
            (TlvType.State, [0x01]),
            (TlvType.PublicKey, ourPub),
        ]);
        var m2 = Tlv8.Decode(await post("/pair-verify", m1, ct).ConfigureAwait(false));
        ThrowOnError(m2, "verify M2");

        byte[] serverPub = Tlv8.Find(m2, TlvType.PublicKey) ?? throw new HapPairingException("verify M2 missing public key");
        byte[] encrypted = Tlv8.Find(m2, TlvType.EncryptedData) ?? throw new HapPairingException("verify M2 missing encrypted data");

        byte[] shared = new byte[32];
        var agreement = new X25519Agreement();
        agreement.Init(ephemeral);
        agreement.CalculateAgreement(new X25519PublicKeyParameters(serverPub), shared, 0);

        byte[] sessionKey = Hkdf32(shared, "Pair-Verify-Encrypt-Salt", "Pair-Verify-Encrypt-Info");
        var serverTlv = Tlv8.Decode(DecryptWithNonce(sessionKey, "PV-Msg02", encrypted));
        byte[] serverId = Tlv8.Find(serverTlv, TlvType.Identifier) ?? throw new HapPairingException("verify M2 missing identifier");
        byte[] serverSig = Tlv8.Find(serverTlv, TlvType.Signature) ?? throw new HapPairingException("verify M2 missing signature");

        if (!serverId.AsSpan().SequenceEqual(credentials.ReceiverId))
            throw new HapPairingException("receiver identity changed since pairing — re-pair required");
        byte[] serverInfo = [.. serverPub, .. serverId, .. ourPub];
        if (!Verify(credentials.ReceiverPublicKey, serverInfo, serverSig))
            throw new HapPairingException("receiver pair-verify signature invalid");

        byte[] ourInfo = [.. ourPub, .. credentials.OurId, .. serverPub];
        byte[] ourSig = Sign(new Ed25519PrivateKeyParameters(credentials.OurPrivateKey), ourInfo);
        byte[] subTlv = Tlv8.Encode(
        [
            (TlvType.Identifier, credentials.OurId),
            (TlvType.Signature, ourSig),
        ]);

        byte[] m3 = Tlv8.Encode(
        [
            (TlvType.State, [0x03]),
            (TlvType.EncryptedData, EncryptWithNonce(sessionKey, "PV-Msg03", subTlv)),
        ]);
        var m4 = Tlv8.Decode(await post("/pair-verify", m3, ct).ConfigureAwait(false));
        ThrowOnError(m4, "verify M4");

        return new HapSession { SharedSecret = shared };
    }

    // ------------------------------------------------------------ crypto helpers

    private static byte[] Hkdf32(byte[] ikm, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, ikm, outputLength: 32,
            salt: Encoding.UTF8.GetBytes(salt), info: Encoding.UTF8.GetBytes(info));

    /// <summary>HAP string nonces are 8 ASCII bytes placed at offset 4 of the 12-byte nonce.</summary>
    private static byte[] StringNonce(string nonce)
    {
        byte[] n = new byte[12];
        Encoding.ASCII.GetBytes(nonce).CopyTo(n, 4);
        return n;
    }

    internal static byte[] EncryptWithNonce(byte[] key, string nonce, byte[] plaintext)
    {
        byte[] output = new byte[plaintext.Length + 16];
        using var cipher = new ChaCha20Poly1305(key);
        cipher.Encrypt(StringNonce(nonce), plaintext, output.AsSpan(0, plaintext.Length),
            output.AsSpan(plaintext.Length, 16));
        return output;
    }

    internal static byte[] DecryptWithNonce(byte[] key, string nonce, byte[] ciphertextWithTag)
    {
        if (ciphertextWithTag.Length < 16)
            throw new HapPairingException("encrypted pairing payload too short");
        byte[] plaintext = new byte[ciphertextWithTag.Length - 16];
        using var cipher = new ChaCha20Poly1305(key);
        try
        {
            cipher.Decrypt(StringNonce(nonce), ciphertextWithTag.AsSpan(0, plaintext.Length),
                ciphertextWithTag.AsSpan(plaintext.Length, 16), plaintext);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new HapPairingException($"pairing payload failed authentication ({nonce})");
        }
        return plaintext;
    }

    private static byte[] Sign(Ed25519PrivateKeyParameters key, byte[] message)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(message, 0, message.Length);
        return signer.GenerateSignature();
    }

    private static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(publicKey));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static void ThrowOnError(List<(byte Type, byte[] Value)> tlv, string stage)
    {
        byte[]? err = Tlv8.Find(tlv, TlvType.Error);
        if (err is { Length: > 0 })
            throw new HapPairingException($"pairing {stage}: {TlvError.Describe(err[0])}");
    }
}
