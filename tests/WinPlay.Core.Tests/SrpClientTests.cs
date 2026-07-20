// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using WinPlay.Core.Hap;
using Xunit;

namespace WinPlay.Core.Tests;

public class SrpClientTests
{
    // RFC 5054 Appendix B test vector (1024-bit group, SHA-1).
    private static readonly byte[] Salt = Convert.FromHexString("BEB25379D1A8581EB5A727673A2441EE");
    private static readonly byte[] EphemeralA = Convert.FromHexString(
        "60975527035CF2AD1989806F0407210BC81EDC04E2762A56AFD529DDDA2D4393");
    private static readonly byte[] ServerB = Convert.FromHexString(
        "BD0C61512C692C0CB6D041FA01BB152D4916A1E77AF46AE105393011BAF38964" +
        "DC46A0670DD125B95A981652236F99D9B681CBF87837EC996C6DA04453728610" +
        "D0C6DDB58B318885D7D82C7F8DEB75CE7BD4FBAA37089E6F9C6059F388838E7A" +
        "00030B331EB76840910440B1B27AAEAEEB4012B7D7665238A8E3FB004B117B58");
    private static readonly byte[] ExpectedA = Convert.FromHexString(
        "61D5E490F6F1B79547B0704C436F523DD0E560F0C64115BB72557EC44352E890" +
        "3211C04692272D8B2D1A5358A2CF1B6E0BFCF99F921530EC8E39356179EAE45E" +
        "42BA92AEACED825171E1E8B9AF6D9C03E1327F44BE087EF06530E69F66615261" +
        "EEF54073CA11CF5858F0EDFDFE15EFEAB349EF5D76988A3672FAC47B0769447B");
    private static readonly byte[] ExpectedS = Convert.FromHexString(
        "B0DC82BABCF30674AE450C0287745E7990A3381F63B387AAF271A10D233861E3" +
        "59B48220F7C4693C9AE12B0A6F67809F0876E2D013800D6C41BB59B6D5979B5C" +
        "00A172B4A2A5903A0BDCAF8A709585EB2AFAFA8F3499B200210DCC1F10EB3394" +
        "3CD67FC88A2F39A4BE5BEC4EC0A3212DC346D7E474B29EDE8A469FFECA686E5A");

    [Fact]
    public void Rfc5054_Vector_Produces_Expected_A_And_Premaster()
    {
        var client = new SrpClient(SrpGroup.Rfc5054Group1024, HashAlgorithmName.SHA1,
            "alice", "password123", EphemeralA);

        Assert.Equal(ExpectedA, client.PublicA);

        client.SetServerParams(Salt, ServerB);
        Assert.Equal(ExpectedS, client.PremasterSecret);
    }

    [Fact]
    public void PairSetup_Client_Uses_3072_Group_And_64_Byte_Key()
    {
        var client = SrpClient.ForPairSetup("3939");
        Assert.Equal(384, client.PublicA.Length); // padded to N length

        // Simulate a server B (any valid-looking value) just to exercise derivation paths.
        byte[] fakeSalt = RandomNumberGenerator.GetBytes(16);
        byte[] fakeB = RandomNumberGenerator.GetBytes(384);
        fakeB[0] |= 0x01; // ensure non-zero
        client.SetServerParams(fakeSalt, fakeB);

        Assert.Equal(64, client.SessionKey.Length);  // SHA-512
        Assert.Equal(64, client.ClientProof.Length);
    }

    [Fact]
    public void Rejects_Zero_Server_Public_Value()
    {
        var client = SrpClient.ForPairSetup("3939");
        Assert.Throws<CryptographicException>(() =>
            client.SetServerParams(new byte[16], new byte[384]));
    }
}
