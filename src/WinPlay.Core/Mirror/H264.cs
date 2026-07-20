// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace WinPlay.Core.Mirror;

/// <summary>
/// H.264 bitstream helpers for mirroring. Media Foundation emits Annex-B
/// (00 00 00 01 start codes); the AirPlay mirror protocol wants length-prefixed AVCC
/// access units plus an avcC configuration record (ISO/IEC 14496-15) for the codec
/// packet built from the stream's SPS and PPS.
/// </summary>
public static class H264
{
    public const byte NalSps = 7;
    public const byte NalPps = 8;
    public const byte NalIdr = 5;

    /// <summary>Splits an Annex-B buffer into raw NAL units (start codes removed).</summary>
    public static List<byte[]> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        var nals = new List<byte[]>();
        int i = 0, n = data.Length;
        int start = -1;
        while (i + 3 <= n)
        {
            bool sc3 = data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1;
            bool sc4 = i + 4 <= n && data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1;
            if (sc3 || sc4)
            {
                if (start >= 0) nals.Add(TrimTrailingZeros(data[start..i]));
                i += sc4 ? 4 : 3;
                start = i;
            }
            else
            {
                i++;
            }
        }
        if (start >= 0 && start < n) nals.Add(TrimTrailingZeros(data[start..n]));
        return nals;
    }

    private static byte[] TrimTrailingZeros(ReadOnlySpan<byte> nal)
    {
        int end = nal.Length;
        while (end > 0 && nal[end - 1] == 0) end--;
        return nal[..end].ToArray();
    }

    public static byte NalType(ReadOnlySpan<byte> nal) => nal.Length > 0 ? (byte)(nal[0] & 0x1f) : (byte)0;

    public static bool ContainsKeyframe(IEnumerable<byte[]> nals) => nals.Any(n => NalType(n) == NalIdr);

    /// <summary>Concatenates NAL units in AVCC form (4-byte big-endian length prefix each).</summary>
    public static byte[] ToAvcc(IEnumerable<byte[]> nals)
    {
        var list = nals.ToList();
        int total = list.Sum(n => 4 + n.Length);
        byte[] outp = new byte[total];
        int offset = 0;
        foreach (byte[] nal in list)
        {
            BinaryPrimitives.WriteUInt32BigEndian(outp.AsSpan(offset), (uint)nal.Length);
            offset += 4;
            nal.CopyTo(outp, offset);
            offset += nal.Length;
        }
        return outp;
    }

    /// <summary>
    /// Builds an avcC AVCDecoderConfigurationRecord from one SPS and one PPS. Used as the
    /// mirror codec-packet payload (4-byte NAL length size).
    /// </summary>
    public static byte[] BuildAvcCConfig(ReadOnlySpan<byte> sps, ReadOnlySpan<byte> pps)
    {
        if (sps.Length < 4) throw new ArgumentException("SPS too short", nameof(sps));
        byte[] outp = new byte[11 + sps.Length + pps.Length];
        int o = 0;
        outp[o++] = 1;          // configurationVersion
        outp[o++] = sps[1];     // AVCProfileIndication
        outp[o++] = sps[2];     // profile_compatibility
        outp[o++] = sps[3];     // AVCLevelIndication
        outp[o++] = 0xff;       // 6 bits reserved + lengthSizeMinusOne = 3
        outp[o++] = 0xe1;       // 3 bits reserved + numOfSequenceParameterSets = 1
        BinaryPrimitives.WriteUInt16BigEndian(outp.AsSpan(o), (ushort)sps.Length); o += 2;
        sps.CopyTo(outp.AsSpan(o)); o += sps.Length;
        outp[o++] = 1;          // numOfPictureParameterSets
        BinaryPrimitives.WriteUInt16BigEndian(outp.AsSpan(o), (ushort)pps.Length); o += 2;
        pps.CopyTo(outp.AsSpan(o));
        return outp;
    }
}
