// SPDX-License-Identifier: GPL-3.0-or-later
// Portions derived from doubletake (LGPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using System.Buffers.Binary;
using System.Numerics;

namespace WinPlay.Core.Fairplay;

/// <summary>
/// Low-level primitives of Apple's FairPlay SAP (Session Authentication Protocol),
/// ported byte-for-byte from omarroth/doubletake's clean-room Go implementation
/// (internal/airplay/fairplay_*.go). None of this is a standard cryptographic hash —
/// it is Apple's proprietary obfuscated construction, reproduced here for same-LAN
/// interop with Apple TV screen mirroring. Verified against doubletake's golden
/// vectors (see FairplaySapTests). Every arithmetic op wraps as a byte, exactly as
/// Go's uint8 arithmetic does, so casts are explicit at each step.
/// </summary>
internal static partial class FairplayPrimitives
{
    // ---- scalar helpers (byte→byte, wrapping) ----

    internal static byte RotateLeft8(byte v, int count) => (byte)BitOperations.RotateLeft((uint)v | ((uint)v << 8) | ((uint)v << 16) | ((uint)v << 24), count & 7);

    internal static byte RotateOrZero(byte input, byte count) =>
        count == 0 ? (byte)0 : RotateLeft8(input, count);

    internal static byte WideSeed(byte input, byte count)
    {
        if (count == 0) return SapSeed[0];
        int shifted = (input << count) | (input >> (8 - count));
        return SapSeed[shifted % SapSeed.Length];
    }

    internal static byte Majority(byte a, byte b, byte c) => (byte)(a ^ ((a ^ b) & (a ^ c)));
    internal static byte SelectBits(byte mask, byte ifSet, byte ifClear) => (byte)(ifClear ^ ((ifSet ^ ifClear) & mask));
    internal static byte Square(byte v) => (byte)(v * v);
    internal static byte Cube(byte v) => (byte)(v * v * v);

    // ---- modified MD5 compression (big-endian words, message mutated after round 31) ----

    internal enum Md5Mutation { Swap, Cycle, Kdf }

    private static readonly int[] Md5Shift =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    private static readonly uint[] Md5Constant =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee, 0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be, 0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa, 0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed, 0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c, 0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05, 0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039, 0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1, 0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    internal static uint[] Md5Compress(uint[] state, ReadOnlySpan<byte> block, Md5Mutation mutation)
    {
        Span<uint> message = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            message[i] = BinaryPrimitives.ReadUInt32BigEndian(block[(i * 4)..]);

        uint a = state[0], b = state[1], c = state[2], d = state[3];
        for (int round = 0; round < 64; round++)
        {
            uint f;
            int word;
            if (round < 16) { f = (b & c) | (~b & d); word = round; }
            else if (round < 32) { f = (d & b) | (~d & c); word = (5 * round + 1) & 15; }
            else if (round < 48) { f = b ^ c ^ d; word = (3 * round + 5) & 15; }
            else { f = c ^ (b | ~d); word = (7 * round) & 15; }

            uint rotated = BitOperations.RotateLeft(a + f + Md5Constant[round] + message[word], Md5Shift[round]);
            (a, b, c, d) = (d, b + rotated, b, c);

            if (round == 31)
                MutateMessage(message, a, b, c, d, mutation);
        }

        return [state[0] + a, state[1] + b, state[2] + c, state[3] + d];
    }

    private static void Swap(Span<uint> message, int i, int j) => (message[i], message[j]) = (message[j], message[i]);

    private static void MutateMessage(Span<uint> message, uint a, uint b, uint c, uint d, Md5Mutation mutation)
    {
        switch (mutation)
        {
            case Md5Mutation.Swap:
            {
                int[] indices =
                [
                    (int)(a & 15), (int)(b & 15), (int)(c & 15), (int)(d & 15),
                    (int)((a >> 4) & 15), (int)((b >> 4) & 15), (int)((c >> 4) & 15), (int)((d >> 4) & 15),
                ];
                for (int i = 0; i < indices.Length; i++) Swap(message, i, indices[i]);
                break;
            }
            case Md5Mutation.Cycle:
            {
                int[] indices =
                [
                    (int)(a & 15), (int)(b & 15), (int)(c & 15), (int)(d & 15),
                    (int)((a >> 4) & 15), (int)((b >> 4) & 15), (int)((c >> 4) & 15), (int)((d >> 4) & 15),
                ];
                uint first = message[indices[0]];
                for (int i = 0; i < indices.Length - 1; i++) message[indices[i]] = message[indices[i + 1]];
                message[indices[^1]] = first;
                break;
            }
            case Md5Mutation.Kdf:
                Swap(message, (int)(a & 15), (int)(b & 15));
                Swap(message, (int)(c & 15), (int)(d & 15));
                for (int shift = 4; shift <= 12; shift += 4)
                    Swap(message, (int)((a >> shift) & 15), (int)((b >> shift) & 15));
                break;
        }
    }

    internal static uint[] WordsFromLittleEndian(ReadOnlySpan<byte> input)
    {
        uint[] outw = new uint[4];
        for (int i = 0; i < 4; i++) outw[i] = BinaryPrimitives.ReadUInt32LittleEndian(input[(i * 4)..]);
        return outw;
    }

    internal static byte[] WordsBigEndian(uint[] words)
    {
        byte[] outb = new byte[16];
        for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt32BigEndian(outb.AsSpan(i * 4), words[i]);
        return outb;
    }

    // ---- FairPlay message decryption: inverse-AES with custom per-mode middle round keys ----

    internal static void DecryptMessage(ReadOnlySpan<byte> message, Span<byte> plaintext)
    {
        byte mode = message[12];
        Span<byte> state = stackalloc byte[16];
        for (int step = 0; step < 8; step++)
        {
            int block = mode == 3 ? 7 - step : step; // mode 3 traverses the CBC chain backwards
            int start = 16 + block * 16;
            message.Slice(start, 16).CopyTo(state);
            DecryptBlock(state, mode);

            ReadOnlySpan<byte> chain = block > 0 ? message.Slice(start - 16, 16) : MessageIv[mode];
            for (int i = 0; i < 16; i++)
                plaintext[block * 16 + i] = (byte)(state[i] ^ chain[i]);
        }
    }

    private static void DecryptBlock(Span<byte> state, byte mode)
    {
        XorRoundKey(state, MessageRoundKey10);
        for (int round = 9; round > 0; round--)
        {
            InverseShiftRows(state);
            for (int i = 0; i < 16; i++) state[i] = InverseSBox[state[i]];
            XorRoundKey(state, MessageMiddleKeys[mode][round - 1]);
            InverseMixColumns(state);
        }
        InverseShiftRows(state);
        for (int i = 0; i < 16; i++) state[i] = InverseSBox[state[i]];
        XorRoundKey(state, MessageRoundKey0);
    }

    private static void XorRoundKey(Span<byte> state, byte[] key)
    {
        for (int i = 0; i < 16; i++) state[i] ^= key[i];
    }

    private static void InverseShiftRows(Span<byte> state)
    {
        Span<byte> previous = stackalloc byte[16];
        state.CopyTo(previous);
        for (int row = 0; row < 4; row++)
            for (int column = 0; column < 4; column++)
                state[4 * column + row] = previous[4 * (((column - row) + 4) & 3) + row];
    }

    private static void InverseMixColumns(Span<byte> state)
    {
        for (int column = 0; column < 4; column++)
        {
            int o = column * 4;
            byte a = state[o], b = state[o + 1], c = state[o + 2], d = state[o + 3];
            state[o] = (byte)(GfMul(a, 14) ^ GfMul(b, 11) ^ GfMul(c, 13) ^ GfMul(d, 9));
            state[o + 1] = (byte)(GfMul(a, 9) ^ GfMul(b, 14) ^ GfMul(c, 11) ^ GfMul(d, 13));
            state[o + 2] = (byte)(GfMul(a, 13) ^ GfMul(b, 9) ^ GfMul(c, 14) ^ GfMul(d, 11));
            state[o + 3] = (byte)(GfMul(a, 11) ^ GfMul(b, 13) ^ GfMul(c, 9) ^ GfMul(d, 14));
        }
    }

    internal static byte GfMul(byte a, byte b)
    {
        byte product = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) product ^= a;
            byte high = (byte)(a & 0x80);
            a <<= 1;
            if (high != 0) a ^= 0x1b;
            b >>= 1;
        }
        return product;
    }
}
