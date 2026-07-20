// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Raop;

/// <summary>
/// Wraps raw PCM in ALAC's uncompressed ("verbatim") frame format — lossless without a
/// real compressor. Bit layout for a stereo 16-bit frame:
///   3b element tag = 1 (CPE, stereo pair) · 4b element instance = 0 · 12b unused = 0 ·
///   1b hasSize = 0 (frame length comes from spf) · 2b wastedBytes = 0 ·
///   1b isNotCompressed = 1 · then nFrames × {L16, R16} MSB-first ·
///   3b END element tag = 7 · zero-padded to a byte boundary.
/// </summary>
public static class AlacFramer
{
    public const int SamplesPerFrame = 352;

    /// <summary>Wraps 352 interleaved stereo 16-bit samples (704 shorts).</summary>
    public static byte[] WrapPcmFrame(ReadOnlySpan<short> interleavedStereo)
    {
        if (interleavedStereo.Length != SamplesPerFrame * 2)
            throw new ArgumentException($"expected {SamplesPerFrame * 2} samples", nameof(interleavedStereo));

        // 23 header bits + 352*32 sample bits + 3 end bits = 11290 bits = 1412 bytes.
        var w = new BitWriter((23 + SamplesPerFrame * 32 + 3 + 7) / 8);
        w.Write(1, 3);   // CPE element
        w.Write(0, 4);   // element instance
        w.Write(0, 12);  // unused/reserved
        w.Write(0, 1);   // hasSize = 0
        w.Write(0, 2);   // wastedBytes = 0
        w.Write(1, 1);   // isNotCompressed = 1
        foreach (short sample in interleavedStereo)
            w.Write((ushort)sample, 16);
        w.Write(7, 3);   // END element
        return w.ToArray();
    }

    private struct BitWriter(int capacity)
    {
        private readonly byte[] _buf = new byte[capacity];
        private int _bitPos = 0;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    _buf[_bitPos >> 3] |= (byte)(0x80 >> (_bitPos & 7));
                _bitPos++;
            }
        }

        public readonly byte[] ToArray() => _buf[..((_bitPos + 7) >> 3)];
    }
}
