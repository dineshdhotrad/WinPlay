// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using WinPlay.Core.Mirror;
using Xunit;

namespace WinPlay.Core.Tests;

public class MirrorVideoStreamTests
{
    [Fact]
    public void VideoHeader_LayoutMatchesProtocol()
    {
        byte[] h = MirrorVideoStream.BuildVideoHeader(1234, keyframe: true, ntpTimestamp: 0x0102030405060708);
        Assert.Equal(128, h.Length);
        Assert.Equal(1234u, BinaryPrimitives.ReadUInt32LittleEndian(h));
        Assert.Equal(0x00, h[4]);                 // encrypted video payload type
        Assert.Equal(0x10, h[5]);                 // IDR indicator
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(h.AsSpan(8)));

        byte[] nonKey = MirrorVideoStream.BuildVideoHeader(10, keyframe: false, ntpTimestamp: 0);
        Assert.Equal(0x00, nonKey[5]);
    }

    [Fact]
    public void CodecHeader_CarriesDimensionsAndFlags()
    {
        byte[] h = MirrorVideoStream.BuildCodecHeader(64, 0, 1920f, 1080f, 3840f, 2160f);
        Assert.Equal(0x01, h[4]);
        Assert.Equal(0x16, h[6]);
        Assert.Equal(0x01, h[7]);
        Assert.Equal(1920f, BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan(16)));
        Assert.Equal(1080f, BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan(20)));
        Assert.Equal(3840f, BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan(56)));
        Assert.Equal(2160f, BinaryPrimitives.ReadSingleLittleEndian(h.AsSpan(60)));
    }

    [Fact]
    public void DataStreamKey_IsDeterministicAnd32Bytes()
    {
        byte[] ikm = RandomNumberGenerator.GetBytes(32);
        byte[] a = MirrorVideoStream.DeriveDataStreamKey(ikm, 123456789);
        byte[] b = MirrorVideoStream.DeriveDataStreamKey(ikm, 123456789);
        byte[] c = MirrorVideoStream.DeriveDataStreamKey(ikm, 987654321);
        Assert.Equal(32, a.Length);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c); // salt binds the stream id
    }

    [Fact]
    public void AesCtrKeys_Are16BytesAndIdDependent()
    {
        byte[] shk = RandomNumberGenerator.GetBytes(16);
        var (k1, iv1) = MirrorVideoStream.DeriveAesCtrKeys(shk, 1);
        var (k2, iv2) = MirrorVideoStream.DeriveAesCtrKeys(shk, 2);
        Assert.Equal(16, k1.Length);
        Assert.Equal(16, iv1.Length);
        Assert.NotEqual(k1, iv1);
        Assert.NotEqual(k1, k2);
        Assert.NotEqual(iv1, iv2);
    }

    [Fact]
    public void VideoCipher_SealsWithHeaderAsAadAndAdvancesNonce()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] header = MirrorVideoStream.BuildVideoHeader(100, true, 42);
        byte[] plaintext = RandomNumberGenerator.GetBytes(100);

        using var cipher = new MirrorVideoCipher(key);
        byte[] sealed0 = cipher.Seal(header, plaintext);
        byte[] sealed1 = cipher.Seal(header, plaintext);
        Assert.Equal(plaintext.Length + 16, sealed0.Length);
        Assert.NotEqual(sealed0, sealed1); // nonce advanced

        // Verify a receiver can open frame 0 with nonce counter 0 and header AAD.
        using var opener = new ChaCha20Poly1305(key);
        Span<byte> nonce = stackalloc byte[12]; // counter 0
        byte[] decrypted = new byte[100];
        opener.Decrypt(nonce, sealed0.AsSpan(0, 100), sealed0.AsSpan(100, 16), decrypted, header);
        Assert.Equal(plaintext, decrypted);
    }
}

public class H264Tests
{
    [Fact]
    public void SplitAnnexB_HandlesThreeAndFourByteStartCodes()
    {
        byte[] stream =
        [
            0, 0, 0, 1, 0x67, 0xAA, 0xBB,       // SPS (4-byte start)
            0, 0, 1, 0x68, 0xCC,                // PPS (3-byte start)
            0, 0, 0, 1, 0x65, 0x01, 0x02, 0x03, // IDR
        ];
        var nals = H264.SplitAnnexB(stream);
        Assert.Equal(3, nals.Count);
        Assert.Equal(H264.NalSps, H264.NalType(nals[0]));
        Assert.Equal(H264.NalPps, H264.NalType(nals[1]));
        Assert.Equal(H264.NalIdr, H264.NalType(nals[2]));
        Assert.True(H264.ContainsKeyframe(nals));
    }

    [Fact]
    public void ToAvcc_LengthPrefixesEachNal()
    {
        byte[] nal = [0x65, 0x01, 0x02];
        byte[] avcc = H264.ToAvcc([nal]);
        Assert.Equal(4 + 3, avcc.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(avcc));
        Assert.Equal(nal, avcc[4..]);
    }

    [Fact]
    public void BuildAvcCConfig_HasExpectedShape()
    {
        byte[] sps = [0x67, 0x64, 0x00, 0x1f, 0xAA, 0xBB];
        byte[] pps = [0x68, 0xCC];
        byte[] avcc = H264.BuildAvcCConfig(sps, pps);

        Assert.Equal(1, avcc[0]);          // configurationVersion
        Assert.Equal(sps[1], avcc[1]);     // profile
        Assert.Equal(sps[3], avcc[3]);     // level
        Assert.Equal(0xff, avcc[4]);
        Assert.Equal(0xe1, avcc[5]);
        Assert.Equal(sps.Length, BinaryPrimitives.ReadUInt16BigEndian(avcc.AsSpan(6)));
        Assert.Equal(sps, avcc[8..(8 + sps.Length)]);
        int ppsCountOffset = 8 + sps.Length;
        Assert.Equal(1, avcc[ppsCountOffset]);
        Assert.Equal(pps.Length, BinaryPrimitives.ReadUInt16BigEndian(avcc.AsSpan(ppsCountOffset + 1)));
    }
}
