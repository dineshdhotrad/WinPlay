// SPDX-License-Identifier: GPL-3.0-or-later
// Portions derived from doubletake (LGPL-3.0-or-later); see THIRD_PARTY_NOTICES.md.
using static WinPlay.Core.Fairplay.FairplayPrimitives;

namespace WinPlay.Core.Fairplay;

/// <summary>
/// FairPlay's proprietary "SAP hash" — a 64-byte-block → 16-byte compression that is
/// NOT a standard cryptographic hash. Ported byte-for-byte from doubletake's
/// fairplay_sap.go. Every operation wraps as a byte exactly as Go's uint8 arithmetic
/// does; expressions are parenthesized to reproduce Go's operator precedence (bitwise
/// and shift ops bind as tightly as multiply, tighter than +/-/|/^). Verified against
/// doubletake's SAP-hash golden vectors and corpus checksum.
/// </summary>
internal sealed class SapState
{
    internal readonly byte[] Hash = (byte[])SapInitialHash.Clone();
    internal readonly byte[] Matrix = (byte[])SapInitialMatrix.Clone();
    internal readonly byte[] Aux = new byte[10];
    internal readonly byte[] Work = new byte[210];

    internal static byte[] Compute(ReadOnlySpan<byte> block)
    {
        var st = new SapState();
        byte[] work = st.Work;

        // Load input in reversed four-byte groups.
        for (int i = 0; i < 210; i++)
            work[i] = block[(i & 63) ^ 3];

        // Four scramble passes; the uint32 underflow in the index changes the first pass.
        for (uint i = 0; i < 840; i++)
        {
            byte x = work[(int)((i - 155u) % 210u)];
            byte y = work[(int)((i - 57u) % 210u)];
            byte z = work[(int)((i - 13u) % 210u)];
            byte w = work[(int)(i % 210u)];
            work[(int)(i % 210u)] = (byte)(RotateLeft8(y, 5) + (RotateLeft8(z, 3) ^ w) - RotateLeft8(x, 7));
        }

        st.NonlinearCircuit();

        byte[] o = new byte[16];
        Array.Copy(st.Aux, 0, o, 0, 3);
        Array.Copy(st.Aux, 3, o, 4, 7);
        for (int i = 0; i < 16; i++) o[i] = (byte)(o[i] + 0xe1);
        o[3] = 0x3d;
        o[11] = 0x3c;
        o[10] ^= (byte)(st.Aux[3] ^ 133);

        for (int i = 0; i < 210; i++)
        {
            byte value = work[i];
            if (i < st.Matrix.Length) value ^= st.Matrix[i];
            if (i < st.Hash.Length) value ^= st.Hash[i];
            o[i & 15] ^= value;
        }

        // Reverse scramble.
        for (int i = 0; i < 256; i++)
            o[i & 15] ^= (byte)(RotateLeft8(o[(i - 7) & 15], 1)
                ^ RotateLeft8(o[(i - 5) & 15], 6)
                ^ RotateLeft8(o[(i - 1) & 15], 5));
        return o;
    }

    private void NonlinearCircuit()
    {
        byte[] hash = Hash, matrix = Matrix, aux = Aux, work = Work;

        byte hi(byte i) => hash[i % 20];
        byte si(byte i) => SapSeed[i % 21];
        byte h(int i) => hi(work[i]);
        byte m(int i) => matrix[work[i] % 35];
        byte s(int i) => si(work[i]);
        byte ma(int i) => matrix[aux[i] % 35];

        matrix[12] = (byte)(0x14 + (SelectBits(92, work[64], (byte)(work[99] / 3)) & WideSeed(s(206), 4)));
        work[4] = (byte)(2 * Square((byte)(work[99] / 5)));
        work[153] ^= (byte)(Square(m(203)) * work[190]);
        hash[3] = (byte)(0x13 ^ ((s(205) >> 1) & 0x10));
        work[33] -= (byte)(s(36) & ~9);
        aux[5] = (byte)(((m(67) & ~2) | 1 | ((h(181) >> 6) & 2) | (hash[3] & 0x10)) - 15);
        matrix[12] = 0x07;
        work[2] -= 64;
        hash[19] = s(58);
        aux[4] = (byte)(92 - m(32));
        aux[9] = (byte)(m(15) + 0x9e);
        work[34] += (byte)(si(aux[9]) / 5);
        hash[19] += (byte)(0xe6 ^ ((hi(aux[9]) >> 1) & 0x66));
        work[15] ^= (byte)(3 * RotateOrZero(work[72], (byte)((-s(190)) & 7)) - 9 * s(126));
        hash[15] ^= Cube(m(181));
        matrix[4] ^= (byte)(work[202] / 3);
        matrix[1] += Cube(Majority((byte)(92 - hi(aux[4])), (byte)~work[105], 0xc6));
        hash[19] ^= (byte)((224 | (s(92) & 27)) * (int)m(41) / 3);
        work[140] += RotateOrZero(92, (byte)((-work[5]) & 7));
        matrix[12] += Majority((byte)(~work[4] ^ m(12)), work[182], 192);
        work[36] += 125;
        work[124] = RotateLeft8(Majority(Majority(work[138], hash[15], 74), h(43), 95), 4);
        byte auxHash = hi(aux[9]);
        aux[1] = (byte)(0x4c & ~(auxHash & (s(68) << 1)));
        aux[2] = (byte)(222 - Majority((byte)(((int)work[177] + (int)s(79)) >> 1), (byte)(3 * (int)work[148] / 5), matrix[1]));
        matrix[16] += (byte)(((ma(4) & ~0x60) | auxHash | 8) - (RotateLeft8(work[33], 2) | 128));
        hash[14] ^= ma(2);
        work[19] += Majority(RotateOrZero(si(h(201)), (byte)((m(112) << 1) & 6)),
            (byte)(((h(208) & ~0x7c) | (h(164) & 0x7c)) / 5), 37);
        matrix[8] = (byte)(RotateOrZero(140, (byte)((-Square(s(45))) & 7)) ^ aux[4]);
        work[190] = 56;
        work[53] = (byte)(~((h(83) | 204) / 5));
        hash[13] += h(41);
        hash[10] = (byte)(Majority(ma(4), work[2], aux[2]) / 15);
        aux[3] = (byte)(92 - Square((byte)(0x28 | (ma(1) & (0x12 | (s(2) & 4))))));
        byte seedBits = si(aux[4]);
        matrix[13] ^= seedBits;
        aux[6] = (byte)(92 + Square(Majority((byte)(m(179) - 38), aux[2], 177)));
        byte expansionBits = Majority((byte)(aux[3] + (aux[4] & 74)), (byte)~seedBits, 121);
        work[47] ^= (byte)(m(89) + Majority((byte)(expansionBits ^ 0xa6), aux[4], 4));
        aux[7] = (byte)((seedBits / 3) - ma(9) - (0x14 | (work[151] & ((aux[4] & 0x88) | 0x62)) | (aux[4] & 0x22)));
        byte expandedSelector = (byte)(expansionBits ^ ((aux[4] & 0xca) >> 1) ^ 75);
        aux[9] += (byte)(0x80 | (Majority(aux[7], work[151], 0x20) & 0x64) | (seedBits & 0x44) | (ma(9) & 0x1b));
        matrix[33] ^= work[26];
        matrix[30] = (byte)(((aux[9] / 3) - ((aux[4] & ~8) | 0x13)) ^ h(122));
        work[22] = (byte)((m(90) & 0x1b) | 0x44);
        int wide = SelectBits(71, matrix[expandedSelector % 35], si(aux[5]));
        matrix[18] += (byte)(wide * wide * wide >> 1);
        matrix[5] -= s(92);
        matrix[18] ^= (byte)(SelectBits(aux[3], ma(3), SelectBits(16, m(183), work[41]))
            * SelectBits(expandedSelector, h(59), work[17]));
        matrix[22] = (byte)(Majority(
            SelectBits((byte)(hash[14] | 28), (byte)((work[7] & 28) | 0x82), h(93)),
            RotateOrZero(ma(4), (byte)(RotateOrZero(work[11], (byte)((-m(28)) & 7)) & 7)),
            matrix[33]) + 74);
        hash[15] -= Majority(Majority(aux[3], aux[4], 214), si((byte)(h(39) ^ 217)), aux[6]);

        byte hash9 = hi(aux[9]);
        byte indexedHash = hi((byte)((byte)((aux[4] / 3) - (aux[9] | work[22])) ^ aux[6]
            ^ (((m(57) | hash9) & (0x52 | (aux[9] & 0x0d))) | (((m(57) & hash9) | aux[9]) & 0x20))));
        aux[6] = (byte)(Square(Square(h(99))) | ma(9));
        aux[1] += (byte)(RotateOrZero((byte)(h(151) | s(202)), (byte)(h(50) & 7))
            + Majority(h(4), (byte)(((int)SelectBits(matrix[16], indexedHash, m(138))
                + (int)SelectBits(17, work[33], s(39))) / 5), 147));
        aux[0] = SelectBits((byte)(hash[10] & 7), (byte)(ma(6) & h(209)),
            SelectBits(0x47, RotateOrZero(s(127), (byte)(ma(6) & 7)), (byte)(si(ma(5)) << 1)));
        byte selectedSquare = SelectBits(198, Square(m(14)), (byte)(h(145) ^ aux[0]));
        byte seed9 = si(aux[9]);
        byte hash3 = hi(aux[3]);
        matrix[2] += (byte)(((hash3 << 1) & ((work[25] & 0x96) | (seed9 & 8))) | (seed9 & 0x40));
        matrix[14] -= SelectBits(34, work[97], (byte)(ma(3) & (aux[0] ^ m(100))));
        work[23] ^= (byte)(Majority(Majority(s(17), hash3, aux[0]), (byte)(work[50] / 3), 0x76) << 1);
        hash[17] = 115;
        hash[13] = (byte)(((Majority(hi(aux[7]), work[10], 82) >> 1) & 0x68) | (h(39) & 0x17));
        matrix[33] -= (byte)(work[113] & 9);
        matrix[28] -= (byte)((aux[3] & ~0x20) | ((work[110] >> 1) & 0x20));
        work[95] = si(aux[3]);
        hash[15] = (byte)(Majority((byte)(work[95] - 48), (byte)~work[184], 189)
            & Cube(Majority(aux[7], si(aux[1]), 0xaa)));
        matrix[22] += work[183];
        aux[4] ^= (byte)(3 * s(1));
        aux[5] += (byte)(198 * Majority(s(178), ma(1), 209) * h(13) * (s(26) >> 1));
        aux[8] = SelectBits(10, ma(3), ma(9));
        matrix[18] -= SelectBits(hash[15], (byte)(aux[5] / 15), Cube((byte)(hi(aux[6]) | 81)));
        aux[1] += (byte)((si(hi(aux[1])) / 3) - h(160));
        hash[16] = (byte)(147 - Majority(aux[0],
            Majority(s(69), work[172], (byte)(aux[2] - selectedSquare + 77)), (byte)(0xc2 | (aux[0] & 5))));
        hash[3] -= WideSeed(Majority(s(155), work[105], 141), (byte)(Majority(s(168), h(29), 6) & 7));
        work[5] = (byte)(RotateOrZero(0x38, (byte)((-(h(61) / 5)) & 7)) ^ ((byte)~ma(8) / 5));
        work[198] += work[3];
        wide = 162 | ma(9);
        work[164] += (byte)(wide * wide / 5);
        aux[2] = (byte)(Majority(RotateOrZero(139, (byte)((-aux[5]) & 6)), hi(aux[3]), 12)
            | SelectBits(95, Cube(seed9), hi(aux[7])));
        matrix[12] += (byte)((16 | ((work[103] | 60) & (aux[2] | (work[103] & 32)))) / 3);
        work[143] -= (byte)(0x12 | (SelectBits(aux[9], SelectBits(matrix[8], work[35], aux[7]), (byte)(aux[8] / 3))
            & (0x4d | ((work[172] >> 1) & 0x20))));
        matrix[29] = 162;
        hash[15] += Majority((byte)(m(149) ^ Square(work[43])),
            (byte)(SelectBits(95, h(125), si(aux[1])) >> 1), 115);
        aux[9] -= hi(aux[7]);
        hash[7] -= Square(RotateOrZero(ma(5), (byte)((-m(17)) * (m(17) & 1))));
        matrix[8] += (byte)(Cube(s(202)) - work[184]);
        hash[16] = (byte)((m(102) << 1) & 0x84);
        aux[6] ^= (byte)(si(aux[7]) >> 1);
        hash[7] -= (byte)(h(191) - SelectBits(177, si(si(aux[1])), (byte)(s(80) << 1)));
        hash[6] = h(119);
        hash[12] = (byte)((hi(aux[8]) ^ (byte)(m(71) + m(15)))
            & Majority((byte)((work[118] & ~0x2c) | 2), Square(hi(aux[9])), 27));
        byte digestIndex = (byte)(SelectBits(0xa9, (byte)(s(57) * 231), Majority(work[32], ma(1), 23)) / 5);
        byte seedSample = si(aux[6]);
        aux[5] = (byte)(Majority((byte)((seedSample & 0x1c) | (h(82) & 0xa2) | (si(digestIndex) & 0x41)),
            Majority(Cube(hi(aux[7])), work[82], 92), 192) ^ digestIndex);
        matrix[25] ^= (byte)(2 * hi(aux[9]) * work[5]
            - (RotateOrZero(aux[4], (byte)(seedSample & 7)) & (byte)(aux[3] + 110)));
    }
}
