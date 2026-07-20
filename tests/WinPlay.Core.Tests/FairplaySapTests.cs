// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using WinPlay.Core.Fairplay;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Golden vectors ported verbatim from doubletake (fairplay_vectors_test.go,
/// fpsap_test.go, fairplay_key_test.go). If the C# FairPlay port is byte-for-byte
/// faithful, every one of these passes — no Apple hardware required.
/// </summary>
public class FairplaySapTests
{
    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static byte[] Filled(byte value)
    {
        byte[] p = new byte[128];
        Array.Fill(p, value);
        return p;
    }

    private static byte[] Sparse(int index)
    {
        byte[] p = new byte[128];
        p[index] = 0x42;
        return p;
    }

    [Fact]
    public void TableData_ChecksumMatches()
    {
        const string want = "28d0986abebe30458348dfa2957aa1d52d6f3ad5a9468c5d8a9c4139b7ca2b43";
        using var sha = SHA256.Create();
        int written = 0;
        void Write(byte[] data) { sha.TransformBlock(data, 0, data.Length, null, 0); written += data.Length; }

        Write(FairplaySapTables.FirstInputMask);
        foreach (var table in new[]
                 {
                     (Round: FairplaySapTables.FirstRoundSubstitution, Mix: FairplaySapTables.FirstMixColumns, Final: FairplaySapTables.FirstFinalSubstitution),
                     (Round: FairplaySapTables.SecondRoundSubstitution, Mix: FairplaySapTables.SecondMixColumns, Final: FairplaySapTables.SecondFinalSubstitution),
                 })
        {
            foreach (var lookup in table.Round) // [9][16] row-major
            {
                byte[] expanded = new byte[256];
                for (int v = 0; v < 256; v++) expanded[v] = lookup.Substitute((byte)v);
                Write(expanded);
            }
            for (int inputByte = 0; inputByte < 4; inputByte++) // mixColumns[inputByte][*]
            {
                byte[] expanded = new byte[256 * 4];
                for (int v = 0; v < 256; v++)
                    for (int outputByte = 0; outputByte < 4; outputByte++)
                        expanded[v * 4 + outputByte] = table.Mix[inputByte * 4 + outputByte].Mix((byte)v);
                Write(expanded);
            }
            foreach (var lookup in table.Final)
            {
                byte[] expanded = new byte[256];
                for (int v = 0; v < 256; v++) expanded[v] = lookup.Substitute((byte)v);
                Write(expanded);
            }
        }

        sha.TransformFinalBlock([], 0, 0);
        Assert.Equal(90128, written);
        Assert.Equal(want, Hex(sha.Hash!));
    }

    [Theory]
    [InlineData(-1, "6f627565f3e77f5b5ede91beee7baf92e4241e0b")] // all-zeros
    [InlineData(-2, "dc2cc74f2ed55484f59f95b96082f0f5c017dd17")] // all-ff
    [InlineData(0, "9bfb9556b8659c2ac94b7ef9e587d71e159ea624")]  // 0x42 at index 0
    [InlineData(63, "150d9fa4eb456e73ba48de5779c5c996b16b3b23")]
    [InlineData(64, "a167db30424ff8890d085c0f1c92b2c5cc06fc45")]
    [InlineData(127, "d246ec5e7adc8118994b8df77146529486ac7caf")]
    public void ExchangeStandalone_GoldenVectors(int sparseIndex, string want)
    {
        byte[] payload = sparseIndex switch
        {
            -2 => Filled(0xff),
            -1 => new byte[128],
            _ => Sparse(sparseIndex),
        };
        Assert.Equal(want, Hex(FairplaySap.ExchangeStandalone(payload)));
    }

    [Fact]
    public void ExchangeStandalone_CapturedM2Vector()
    {
        byte[] m2 = Convert.FromHexString(
            "46504c59030102000000008202034a114c26b77d4e2eec2c8f89fdb653b5b32d3576bc176816d110a14c3f53c08dbb936183bfdfe0a4f3c12e85216003b46f738c40c54da6c436d29d1b342d63c7b314309ae79a33bb1787709ef077cbfe4190117a3423e270fd1a2eac44da1a7934f59dc681d1b70783f228c4d077c2d495f5285c3bf8df586fc2ebfe17fb5b65");
        byte[] payload = new byte[128];
        Array.Copy(m2, 14, payload, 0, 128);
        Assert.Equal("4b911e48af23d8406368aeafbb61bfcd569e3e55", Hex(FairplaySap.ExchangeStandalone(payload)));
    }

    [Theory]
    [InlineData("zero", -1, "7e38958ffe4ed433743919fe7eb16376afa4eb9e")]
    [InlineData("one-at-zero", 0, "ea46797d726c6a9be43ffa72385ff97ce1c54f1b")]
    public void Descriptor_GoldenVectors(string name, int oneAt, string want)
    {
        _ = name;
        byte[] payload = new byte[128];
        if (oneAt >= 0) payload[oneAt] = 1;
        byte[] dynamicSap = FairplaySap.DynamicSap(payload);
        Assert.Equal(want, Hex(FairplaySap.Descriptor(dynamicSap)));
    }

    [Fact]
    public void ExchangeM3_HasFplyHeaderAndHash()
    {
        byte[] m2 = new byte[142];
        byte[] m3 = FairplaySap.ExchangeM3(m2);
        Assert.Equal(164, m3.Length);
        Assert.Equal("FPLY", System.Text.Encoding.ASCII.GetString(m3, 0, 4));
        Assert.Equal("6f627565f3e77f5b5ede91beee7baf92e4241e0b", Hex(m3[144..]));
        Assert.Throws<ArgumentException>(() => FairplaySap.ExchangeM3(new byte[141]));
    }

    [Fact]
    public void KeyUnwrap_GoldenVector()
    {
        byte[] m3 = new byte[164];
        m3[12] = 3;
        for (int i = 16; i < 144; i++) m3[i] = (byte)(i * 5 + 7);
        byte[] ekey = new byte[72];
        for (int i = 0; i < ekey.Length; i++) ekey[i] = (byte)(i * 7 + 3);
        Assert.Equal("903e5be94732428e9965afb262b193a4", Hex(FairplayKdf.UnwrapKey(m3, ekey)));
    }
}
