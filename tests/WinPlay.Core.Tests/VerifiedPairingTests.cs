// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Hap;
using Xunit;

namespace WinPlay.Core.Tests;

public class VerifiedPairingTests
{
    [Fact]
    public void StringNonceCipher_RoundTrips()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;
        byte[] plaintext = "hello pairing"u8.ToArray();

        byte[] encrypted = HapVerifiedPairing.EncryptWithNonce(key, "PS-Msg05", plaintext);
        Assert.Equal(plaintext.Length + 16, encrypted.Length);
        Assert.Equal(plaintext, HapVerifiedPairing.DecryptWithNonce(key, "PS-Msg05", encrypted));
    }

    [Fact]
    public void StringNonceCipher_WrongNonceFailsAuthentication()
    {
        byte[] key = new byte[32];
        byte[] encrypted = HapVerifiedPairing.EncryptWithNonce(key, "PS-Msg05", [1, 2, 3]);
        Assert.Throws<HapPairingException>(
            () => HapVerifiedPairing.DecryptWithNonce(key, "PS-Msg06", encrypted));
    }

    [Fact]
    public void CredentialStore_RoundTripsAndRemoves()
    {
        string path = Path.Combine(Path.GetTempPath(), $"winplay-test-{Guid.NewGuid():N}.dat");
        try
        {
            var store = new CredentialStore(path);
            Assert.Null(store.Load("AA:BB:CC:DD:EE:FF"));

            var credentials = new HapPairingCredentials
            {
                ReceiverPublicKey = [1, 2, 3, 4],
                ReceiverId = "atv-id"u8.ToArray(),
                OurPrivateKey = [9, 8, 7],
                OurId = "our-id"u8.ToArray(),
            };
            store.Save("AA:BB:CC:DD:EE:FF", credentials);

            // Device-id normalization: separators and case are irrelevant.
            var loaded = new CredentialStore(path).Load("aabbccddeeff");
            Assert.NotNull(loaded);
            Assert.Equal(credentials.ReceiverPublicKey, loaded.ReceiverPublicKey);
            Assert.Equal(credentials.ReceiverId, loaded.ReceiverId);
            Assert.Equal(credentials.OurPrivateKey, loaded.OurPrivateKey);
            Assert.Equal(credentials.OurId, loaded.OurId);

            // File on disk must not contain the raw private key (DPAPI-wrapped).
            byte[] raw = File.ReadAllBytes(path);
            Assert.DoesNotContain(Convert.ToHexString(credentials.OurPrivateKey),
                Convert.ToHexString(raw));

            store.Remove("AA-BB-CC-DD-EE-FF");
            Assert.Null(store.Load("AABBCCDDEEFF"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
