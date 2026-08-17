// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WinPlay.Core.Hap;

public sealed record SrpGroup(BigInteger N, BigInteger G, int PrimeBytes)
{
    /// <summary>RFC 5054 3072-bit group (g=5) — the group HomeKit pair-setup uses with SHA-512.</summary>
    public static readonly SrpGroup Rfc5054Group3072 = FromHex(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF",
        generator: 5);

    /// <summary>RFC 5054 1024-bit group (g=2) — used only for the RFC test vector.</summary>
    public static readonly SrpGroup Rfc5054Group1024 = FromHex(
        "EEAF0AB9ADB38DD69C33F80AFA8FC5E86072618775FF3C0B9EA2314C9C256576" +
        "D674DF7496EA81D3383B4813D692C6E0E0D5D8E250B98BE48E495C1D6089DAD1" +
        "5DC7D7B46154D6B6CE8EF4AD69B15D4982559B297BCF1885C529F566660E57EC" +
        "68EDBC3C05726CC02FD4CBF4976EAA9AFD5138FE8376435B9FC61D2FC0EB06E3",
        generator: 2);

    private static SrpGroup FromHex(string hexN, int generator)
    {
        byte[] n = Convert.FromHexString(hexN);
        return new SrpGroup(new BigInteger(n, isUnsigned: true, isBigEndian: true), generator, n.Length);
    }
}

/// <summary>
/// SRP-6a client (RFC 5054 conventions: k and u computed over N-length–padded operands).
/// HomeKit pair-setup uses <see cref="SrpGroup.Rfc5054Group3072"/> with SHA-512,
/// username "Pair-Setup", and the fixed PIN "3939" for transient pairing.
///
/// M1/M2/K use the csrp-lineage convention every known-good AirPlay 2 sender ships:
/// K = H(S) (S unpadded)
/// M1 = H( H(N)⊕H(g) | H(I) | s | A | B | K ) (A, B, g unpadded)
/// M2 = H( A | M1 | K )
/// The core math (x, k, u, S) is validated against the RFC 5054 appendix-B vector.
/// </summary>
public sealed class SrpClient
{
    private readonly SrpGroup _group;
    private readonly HashAlgorithmName _hash;
    private readonly BigInteger _a;
    private readonly BigInteger _bigA;
    private readonly string _username;
    private readonly string _password;

    private byte[]? _sessionKey;
    private byte[]? _clientProof;
    private byte[]? _premaster;

    public SrpClient(SrpGroup group, HashAlgorithmName hash, string username, string password,
        byte[]? ephemeralA = null)
    {
        _group = group;
        _hash = hash;
        _username = username;
        _password = password;

        byte[] aBytes = ephemeralA ?? RandomNumberGenerator.GetBytes(32);
        _a = new BigInteger(aBytes, isUnsigned: true, isBigEndian: true);
        _bigA = BigInteger.ModPow(group.G, _a, group.N);
    }

    public static SrpClient ForPairSetup(string pin) =>
        new(SrpGroup.Rfc5054Group3072, HashAlgorithmName.SHA512, "Pair-Setup", pin);

    /// <summary>Client public value A, left-padded to the group prime length for the wire.</summary>
    public byte[] PublicA => Pad(_bigA);

    /// <summary>Session key K = H(S). 64 bytes with SHA-512.</summary>
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException("call SetServerParams first");

    /// <summary>Client proof M1 to send in pair-setup M3.</summary>
    public byte[] ClientProof => _clientProof ?? throw new InvalidOperationException("call SetServerParams first");

    internal byte[] PremasterSecret => _premaster ?? throw new InvalidOperationException("call SetServerParams first");

    public void SetServerParams(byte[] salt, byte[] serverB)
    {
        var bigB = new BigInteger(serverB, isUnsigned: true, isBigEndian: true);
        if ((bigB % _group.N).IsZero)
            throw new CryptographicException("SRP: illegal server public value (B mod N == 0)");

        BigInteger u = FromHash(Hash(Pad(_bigA), Pad(bigB)));
        if (u.IsZero)
            throw new CryptographicException("SRP: u == 0");

        BigInteger k = FromHash(Hash(Pad(_group.N), Pad(_group.G)));
        BigInteger x = FromHash(Hash(salt, Hash(Encoding.UTF8.GetBytes($"{_username}:{_password}"))));

        // S = (B - k·g^x) ^ (a + u·x) mod N
        BigInteger gx = BigInteger.ModPow(_group.G, x, _group.N);
        BigInteger baseVal = ((bigB % _group.N) + _group.N - (k * gx % _group.N)) % _group.N;
        BigInteger s = BigInteger.ModPow(baseVal, _a + u * x, _group.N);

        _premaster = Unpad(s);
        _sessionKey = Hash(_premaster);

        byte[] hn = Hash(Unpad(_group.N));
        byte[] hg = Hash(Unpad(_group.G));
        byte[] hxor = new byte[hn.Length];
        for (int i = 0; i < hn.Length; i++) hxor[i] = (byte)(hn[i] ^ hg[i]);

        _clientProof = Hash(hxor, Hash(Encoding.UTF8.GetBytes(_username)), salt,
            Unpad(_bigA), Unpad(bigB), _sessionKey);
    }

    /// <summary>Verifies the server proof M2 = H(A | M1 | K) from pair-setup M4.</summary>
    public bool VerifyServerProof(byte[] serverProof)
    {
        byte[] expected = Hash(Unpad(_bigA), ClientProof, SessionKey);
        return CryptographicOperations.FixedTimeEquals(expected, serverProof);
    }

    private byte[] Pad(BigInteger v)
    {
        byte[] raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (raw.Length == _group.PrimeBytes) return raw;
        if (raw.Length > _group.PrimeBytes) throw new CryptographicException("value larger than N");
        byte[] padded = new byte[_group.PrimeBytes];
        raw.CopyTo(padded, _group.PrimeBytes - raw.Length);
        return padded;
    }

    private static byte[] Unpad(BigInteger v) => v.ToByteArray(isUnsigned: true, isBigEndian: true);

    private static BigInteger FromHash(byte[] hash) => new(hash, isUnsigned: true, isBigEndian: true);

    private byte[] Hash(params byte[][] parts)
    {
        using IncrementalHash h = IncrementalHash.CreateHash(_hash);
        foreach (byte[] part in parts) h.AppendData(part);
        return h.GetHashAndReset();
    }
}
