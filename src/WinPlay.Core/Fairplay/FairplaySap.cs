// SPDX-License-Identifier: GPL-3.0-or-later
// Portions derived from doubletake (LGPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using static WinPlay.Core.Fairplay.FairplayPrimitives;

namespace WinPlay.Core.Fairplay;

/// <summary>
/// The FairPlay SAP m2→m3 exchange: two white-box substitution/permutation networks
/// keyed by a descriptor derived from the receiver's m2 payload. Ported from
/// doubletake's fpsap.go / fairplay_message.go. Verified against golden vectors.
/// </summary>
internal static class FairplaySap
{
    private static readonly byte[] DescriptorRemainderPrefix =
        [0x9f, 0xa7, 0xc5, 0x13, 0x20, 0xae, 0xa6, 0x2d, 0x29, 0x49, 0x78, 0x6c, 0x87, 0x64, 0x2e, 0x34, 0xba];

    private static readonly byte[] DescriptorSuffix =
        [0x97, 0xb5, 0x0f, 0x84, 0xe2, 0x15, 0x5a, 0x9c, 0x24, 0x99, 0x1c, 0xf4, 0x3a, 0x09, 0x63, 0x55, 0x47];

    private static readonly uint[] DescriptorInitialState =
        [0xd30fe3ad, 0x8670fb82, 0xc1ebdda2, 0x3fb07aa8];

    private static readonly byte[] MaskSuffix =
        [0x57, 0xd8, 0xee, 0xcb, 0xde, 0xfb, 0xcf, 0x59, 0x1c, 0x27, 0xa2, 0xcf, 0xbe, 0xb0, 0x89];

    private static readonly byte[] FixedBlock =
        [0xaf, 0xc2, 0x2b, 0xa0, 0x49, 0xef, 0xfc, 0xfb, 0xfe, 0x67, 0xac, 0x5e, 0xbe, 0xf6, 0xfb, 0xcb];

    private static readonly int[] FirstPositionMap = [0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11];
    private static readonly int[] SecondPositionMap = [0, 13, 10, 7, 4, 1, 14, 11, 8, 5, 2, 15, 12, 9, 6, 3];

    private static readonly byte[] M3Prefix = Convert.FromHexString(
        "46504c590301030000000098038f1a9c991ea22c511e45ba97f1af8dfb0f86f5" +
        "50c54486fe6b3ab233da431ef8e5fc1156dba321fffeabb1b392b09d227e88c7" +
        "12202866eb7bbf310015aa1d19a5df36d5dfd8d3ca1639b376eaece946edfe8b" +
        "7a66cd302d04aac3c1251714019bd5f2d49b543e11eed1646291ec8efd96b691" +
        "01b849fd93a02860d1a0dff5cd4414aa");

    /// <summary>Computes m3 (164 bytes: 144-byte prefix + 20-byte exchange hash) from m2.</summary>
    internal static byte[] ExchangeM3(ReadOnlySpan<byte> m2)
    {
        if (m2.Length < 142)
            throw new ArgumentException($"m2 too short: {m2.Length} bytes, need at least 142", nameof(m2));
        byte[] payload = new byte[128];
        m2.Slice(14, 128).CopyTo(payload);
        byte[] hash = ExchangeStandalone(payload);
        byte[] outp = new byte[M3Prefix.Length + hash.Length];
        M3Prefix.CopyTo(outp, 0);
        hash.CopyTo(outp, M3Prefix.Length);
        return outp;
    }

    internal static byte[] ExchangeStandalone(ReadOnlySpan<byte> payload)
    {
        byte[] dynamicSap = DynamicSap(payload);
        byte[] seed = Descriptor(dynamicSap);
        byte[][] masks = Masks(seed);
        byte[] intermediate = FirstNetwork(masks);
        byte[] left = Digest32(intermediate, FixedBlock);
        byte[] whiteboxOutput = SecondNetwork(left, masks);
        byte[] digest = Digest32(left, whiteboxOutput);

        byte[] outp = new byte[20];
        Array.Copy(whiteboxOutput, 0, outp, 0, 4);
        Array.Copy(digest, 0, outp, 4, 16);
        return outp;
    }

    internal static byte[] DynamicSap(ReadOnlySpan<byte> payload)
    {
        byte[] message = new byte[144];
        message[12] = 3;
        payload[..128].CopyTo(message.AsSpan(16));
        byte[] outp = new byte[128];
        DecryptMessage(message, outp);
        return outp;
    }

    internal static byte[] Descriptor(ReadOnlySpan<byte> dynamicSap)
    {
        byte[] padded = new byte[192];
        int offset = 0;
        DescriptorRemainderPrefix.CopyTo(padded, offset); offset += DescriptorRemainderPrefix.Length;
        dynamicSap[..128].CopyTo(padded.AsSpan(offset)); offset += 128;
        DescriptorSuffix.CopyTo(padded, offset); offset += DescriptorSuffix.Length;
        padded[offset] = 0x80;
        BinaryPrimitives.WriteUInt64LittleEndian(padded.AsSpan(padded.Length - 8), 290 * 8);

        uint[] state = (uint[])DescriptorInitialState.Clone();
        uint[] firstFinal = new uint[4];
        for (int off = 0; off < padded.Length; off += 64)
        {
            ReadOnlySpan<byte> block = padded.AsSpan(off, 64);
            byte[] add = SapState.Compute(block);
            for (int i = 0; i < 4; i++)
                state[i] += BinaryPrimitives.ReadUInt32LittleEndian(add.AsSpan(i * 4));
            state = Md5Compress(state, block, Md5Mutation.Cycle);
            if (off == padded.Length - 64)
            {
                firstFinal = state;
                state = Md5Compress(state, block, Md5Mutation.Cycle);
            }
        }

        byte[] outp = new byte[20];
        BinaryPrimitives.WriteUInt32BigEndian(outp, firstFinal[0]);
        WordsBigEndian(state).CopyTo(outp.AsSpan(4));
        return outp;
    }

    private static byte[][] Masks(byte[] seed)
    {
        uint[] state = [0x1d4a4587, 0x92f39fcc, 0x1d87d836, 0xcdc86697];
        byte[][] masks = new byte[9][];
        for (int i = 0; i < 9; i++)
        {
            byte[] block = new byte[64];
            Array.Copy(seed, 0, block, 0, 20);
            block[20] = (byte)i;
            Array.Copy(MaskSuffix, 0, block, 21, 15);
            block[36] = 0x80;
            BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56), 0x320);
            masks[i] = WordsBigEndian(Md5Compress(state, block, Md5Mutation.Swap));
        }
        return masks;
    }

    private static byte[] Digest32(byte[] left, byte[] right)
    {
        byte[] block = new byte[64];
        Array.Copy(left, 0, block, 0, 16);
        Array.Copy(right, 0, block, 16, 16);
        block[32] = 0x80;
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(56), 0x100);
        uint[] state = [0xb9f3dcdc, 0xfbdc740b, 0x60f77f86, 0x51907216];
        return WordsBigEndian(Md5Compress(state, block, Md5Mutation.Swap));
    }

    private static byte[] FirstNetwork(byte[][] masks)
    {
        byte[] state = (byte[])FixedBlock.Clone();
        for (int i = 0; i < 16; i++) state[i] ^= FairplaySapTables.FirstInputMask[i];

        for (int bank = 0; bank < 9; bank++)
        {
            byte[] substituted = new byte[16];
            for (int output = 0; output < 16; output++)
            {
                int input = FirstPositionMap[output];
                substituted[output] = FairplaySapTables.FirstRoundSubstitution[bank * 16 + input].Substitute(state[input]);
            }
            Mix(FairplaySapTables.FirstMixColumns, state, substituted);
            for (int i = 0; i < 16; i++) state[i] ^= masks[bank][i];
        }

        byte[] outp = new byte[16];
        for (int output = 0; output < 16; output++)
        {
            int input = FirstPositionMap[output];
            outp[output] = FairplaySapTables.FirstFinalSubstitution[input].Substitute(state[input]);
        }
        return outp;
    }

    private static byte[] SecondNetwork(byte[] initial, byte[][] masks)
    {
        byte[] state = (byte[])initial.Clone();
        for (int bank = 8; bank >= 0; bank--)
        {
            byte[] substituted = new byte[16];
            for (int output = 0; output < 16; output++)
            {
                int input = SecondPositionMap[output];
                substituted[output] = (byte)(FairplaySapTables.SecondRoundSubstitution[bank * 16 + output].Substitute(state[input])
                    ^ masks[bank][output]);
            }
            Mix(FairplaySapTables.SecondMixColumns, state, substituted);
        }

        byte[] outp = new byte[16];
        for (int output = 0; output < 16; output++)
        {
            int input = SecondPositionMap[output];
            outp[output] = (byte)(FairplaySapTables.SecondFinalSubstitution[output].Substitute(state[input])
                ^ FairplaySapTables.SecondOutputMask[output]);
        }
        return outp;
    }

    private static void Mix(ByteLookup[] mixColumns, byte[] state, byte[] substituted)
    {
        for (int word = 0; word < 4; word++)
        {
            int offset = word * 4;
            for (int outputByte = 0; outputByte < 4; outputByte++)
            {
                byte mixed = 0;
                for (int inputByte = 0; inputByte < 4; inputByte++)
                    mixed ^= mixColumns[inputByte * 4 + outputByte].Mix(substituted[offset + inputByte]);
                state[offset + outputByte] = mixed;
            }
        }
    }
}
