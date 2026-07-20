// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinPlay.Core.Fairplay;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Layered primitive vectors (doubletake fairplay_vectors_test.go). These isolate the
/// modified-MD5 compressor, the proprietary SAP hash, and the message decryption so a
/// porting error surfaces at its exact layer rather than only in the full exchange.
/// </summary>
public class FairplayPrimitiveTests
{
    private static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexString(b).ToLowerInvariant();

    [Fact]
    public void ModifiedMd5_KdfMutation_Vector()
    {
        byte[] block = new byte[64];
        for (int i = 0; i < 64; i++) block[i] = (byte)(i * 3 + 1);
        byte[] key = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

        uint[] words = FairplayPrimitives.Md5Compress(
            FairplayPrimitives.WordsFromLittleEndian(key), block, FairplayPrimitives.Md5Mutation.Kdf);

        byte[] modified = new byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32LittleEndian(modified.AsSpan(i * 4), words[i]);
        Assert.Equal("f6f728cb5a4397b675664f9291b859aa", Hex(modified));
    }

    [Fact]
    public void SapHash_SingleVector()
    {
        byte[] block = new byte[64];
        for (int i = 0; i < 64; i++) block[i] = (byte)(i * 3 + 1);
        Assert.Equal("75498a4e218773030e9cdf04f0c49367", Hex(SapState.Compute(block)));
    }

    [Fact]
    public void SapHash_Corpus()
    {
        using var sha = SHA256.Create();
        ulong state = 0x6a09e667f3bcc909;
        for (int round = 0; round < 64; round++)
        {
            byte[] block = new byte[64];
            for (int i = 0; i < 64; i++)
            {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                block[i] = (byte)state;
            }
            byte[] digest = SapState.Compute(block);
            sha.TransformBlock(digest, 0, digest.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        Assert.Equal("36ad2a7920076af59452d9f0c91e3b7d1aebc53f9143bd6819e39119d4535c92", Hex(sha.Hash!));
    }

    [Theory]
    [InlineData((byte)0, "b66a3295ffa6b56e02ed1b3d67fef74b90fe148570de65e6773669126a4905d8405644cae0b2f5ed6109c099c7aea7398dac8d623fbd69b87242b374d98f89502bb5a63e29c46a8ed0e98466966191ec1e6c8675087fde21337db1c8fab4c21db824026335f6fc37e2e5b6f53357d06994bd383d6029a0aff654fb1521bcdde4", "f7dd1ccb9e745f7951a6e325d73a1f5f")]
    [InlineData((byte)1, "0f95c6ddc8987eda18577da2db074e7c04715af8b3914a73be1b3d6c111953017ee0a39dfcab3e0d57f2f9fbd59c5e18101788c2ab8e3cbb403bcb48b53f3e5bf74f949e79fa5ca679df4bfcb33a69b1442675d03f948fe5bd0c5ffb64b73a5ab58f46d6baae097b599624147c2487991163ecffc4d966240f9526346a10fdb0", "b44ad891396f097aa309bc132f5b8889")]
    [InlineData((byte)2, "40f18751b44d733e0aa0416401a7d3f40375fad3ce56900602578bca14660909820e6ef3a5e943cafef5370f72c52177d9b82278b414811201a3d99202bedcca26a4d1ad08bc2669f4bae6ca54b8a120d0425edb6082f51f5aecdb547bfdb319099c9ea2729ae6a1c4480827ce9991e273843cf1c7d74ebbebc2657659bcea9f", "d38cd8efecdb20f333273c4312d9b236")]
    [InlineData((byte)3, "70a3c30edf0e1dfa1785ce4336ed547062672a47f714a0c1f89a83d95691103dfe5cf653d4cb8299793faf33fd0d4482ef5333b41ab094a90e1baf996bcf4989783f6918397fbacddaf00a2b97556dd8099841578bc5eb1444912b47298eaf356fdd6701bb3f64e725a80eb4c6f3556195de35c93e7cc703bdd24351468e9847", "769e2fe4c5ad7fbe6fd6772d00f529f4")]
    public void MessageDecrypt_Vectors(byte mode, string decryptedWant, string aesKeyWant)
    {
        byte[] message = new byte[164];
        message[12] = mode;
        for (int i = 16; i < 144; i++) message[i] = (byte)(i * 5 + 7);

        byte[] decrypted = new byte[128];
        FairplayPrimitives.DecryptMessage(message, decrypted);
        Assert.Equal(decryptedWant, Hex(decrypted));

        byte[] aesKey = FairplayKdf.DeriveWrappingKey(FairplayPrimitives.DefaultSapTail, message);
        Assert.Equal(aesKeyWant, Hex(aesKey));
    }
}
