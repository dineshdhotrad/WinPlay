// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Raop;

/// <summary>
/// Wraps raw PCM in ALAC's uncompressed ("verbatim") frame format — lossless without a
/// real compressor. Bit layout for a stereo 16-bit frame:
/// 3b element tag = 1 (CPE, stereo pair) · 4b element instance = 0 · 12b unused = 0 ·
/// 1b hasSize = 0 (frame length comes from spf) · 2b wastedBytes = 0 ·
/// 1b isNotCompressed = 1 · then nFrames × {L16, R16} MSB-first ·
/// 3b END element tag = 7 · zero-padded to a byte boundary.
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

    /// <summary>
    /// Wraps 352 interleaved stereo samples as ALAC 24-bit verbatim — the buffered stream's
    /// format (audioFormat bit 19, ALAC/44100/24/2).
    ///
    /// <para>The receivers publish exactly which formats each stream accepts. Their
    /// <c>audioStream</c> (realtime) mask is <c>0x1440800</c>, which contains bit 18
    /// (ALAC/44100/16/2); their <c>bufferStream</c> mask is <c>0xF7FE018E00E80000</c>, which does
    /// NOT contain bit 18 but does contain bit 19 — the same 44.1 kHz stereo audio at 24 bits.
    /// Sending 16-bit on the buffered stream offered a format that stream never accepted: the
    /// receiver completed the handshake, took the packets, answered every keep-alive and rendered
    /// silence, with no error at any layer.</para>
    ///
    /// <para>Source samples are 16-bit, so each is shifted left by 8 into the 24-bit field. That is
    /// lossless — a value scaling, not a resample — so no rate conversion or requantisation is
    /// involved anywhere in the path.</para>
    /// </summary>
    public static byte[] WrapPcmFrame24(ReadOnlySpan<short> interleavedStereo)
    {
        if (interleavedStereo.Length != SamplesPerFrame * 2)
            throw new ArgumentException($"expected {SamplesPerFrame * 2} samples", nameof(interleavedStereo));

        // 23 header bits + 352 frames × 2 channels × 24 bits + 3 end bits.
        var w = new BitWriter((23 + SamplesPerFrame * 48 + 3 + 7) / 8);
        w.Write(1, 3);   // CPE element
        w.Write(0, 4);   // element instance
        w.Write(0, 12);  // unused/reserved
        w.Write(0, 1);   // hasSize = 0
        w.Write(0, 2);   // wastedBytes = 0
        w.Write(1, 1);   // isNotCompressed = 1
        foreach (short sample in interleavedStereo)
            w.Write((uint)((sample << 8) & 0xFFFFFF), 24);
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
