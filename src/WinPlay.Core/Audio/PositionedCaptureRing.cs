// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// A capture ring that locks audio content to the wall-clock timeline, making end-to-end
/// latency <em>constant by construction</em>.
///
/// <para><b>The flaw this replaces:</b> a plain FIFO plus zero-fill-on-underrun lets every
/// capture hiccup permanently lengthen the pipeline. The pump fabricates silence for frame k
/// while the real audio for k is still in flight; when that audio arrives it is appended and
/// sent as frame k+n — shifted later on the RTP timeline — and every later sample inherits the
/// shift. Each jitter event adds its duration to the perceived delay, which is exactly the
/// "latency slowly grows" symptom on anchored (buffered) AirPlay streams, where a single
/// SETRATEANCHORTIME fixes the timeline once and nothing re-anchors it.</para>
///
/// <para><b>The invariant here:</b> every frame owns an absolute position on one timeline.
/// <list type="bullet">
/// <item>The <b>writer</b> appends contiguously (a late burst of contiguous device audio keeps
/// its true positions — jitter never shifts the mapping), and a real capture gap longer than
/// <c>gapThresholdFrames</c> (the device rendered nothing, e.g. system silence) jumps the write
/// position to "now", zeroing the skipped span so resumed audio lands <em>live</em>.</item>
/// <item>The <b>reader</b> always advances: positions not yet written are emitted as silence,
/// and data arriving after its position was passed is dropped rather than re-timed. Stale audio
/// is never sent late, so backlog — and therefore latency growth — cannot exist.</item>
/// <item>A drift term nudges the producer's resample rate (≤ ±0.2 %, inaudible) so the slow
/// skew between the audio device clock and the wall clock never accumulates into a false gap.</item>
/// </list></para>
///
/// <para>Frames are interleaved stereo (2 shorts per frame) at 44.1 kHz. Thread-safe: one
/// writer thread (WASAPI callback), one reader thread (RTP pump).</para>
/// </summary>
public sealed class PositionedCaptureRing
{
    public const int SampleRate = 44100;

    /// <summary>~60 ms — how far the reader trails the writer, absorbing callback jitter.</summary>
    public const int DefaultMarginFrames = 2646;

    /// <summary>~150 ms — a hole in device time larger than this is true silence, not jitter.</summary>
    public const int DefaultGapThresholdFrames = 6615;

    private readonly short[] _ring;                // interleaved stereo, indexed by frame % capacity
    private readonly int _capacityFrames;
    private readonly int _marginFrames;
    private readonly int _gapThresholdFrames;
    private readonly object _lock = new();

    private long _writerFrames;                    // next absolute frame position to write
    private long _readerFrames;                    // next absolute frame position to read
    private double _t0Seconds = double.NaN;        // wall time of position 0 (first write)
    private double _alignmentEma;                  // smoothed (writer − expected) frames
    private long _underrunFrames;                  // frames served as silence (reader ahead of writer)
    private long _lateFrames;                      // frames that arrived after their position was read
    private long _gapJumps;                        // true-silence jumps taken by the writer

    public PositionedCaptureRing(int capacityFrames = SampleRate * 4,
        int marginFrames = DefaultMarginFrames, int gapThresholdFrames = DefaultGapThresholdFrames)
    {
        _capacityFrames = capacityFrames;
        _marginFrames = marginFrames;
        _gapThresholdFrames = gapThresholdFrames;
        _ring = new short[capacityFrames * 2];
    }

    /// <summary>Absolute frame position of the next write (test/diagnostic visibility).</summary>
    public long WriterFrames { get { lock (_lock) return _writerFrames; } }

    /// <summary>Absolute frame position of the next read (test/diagnostic visibility).</summary>
    public long ReaderFrames { get { lock (_lock) return _readerFrames; } }

    /// <summary>Total frames served as silence because the reader passed the writer. Includes
    /// BENIGN silence (nothing rendering → no capture callbacks → silence out is correct), so a
    /// large value is not necessarily damage — see <see cref="LateFrames"/> for actual harm.</summary>
    public long UnderrunFrames { get { lock (_lock) return _underrunFrames; } }

    /// <summary>Real audio DROPPED: frames appended after their position had already been read
    /// (their slot went out as silence). This is the precise measure of audible capture damage
    /// during active playback — benign silence can never inflate it.</summary>
    public long LateFrames { get { lock (_lock) return _lateFrames; } }

    /// <summary>Number of true-silence gap jumps the writer has taken.</summary>
    public long GapJumps { get { lock (_lock) return _gapJumps; } }

    /// <summary>
    /// Starts one producer callback: reconciles the write position against wall time and returns
    /// the drift-corrected output sample rate the producer should resample to before
    /// <see cref="Append"/>. Jumps across true capture gaps (device silence) so resumed audio
    /// lands live instead of queueing behind a fabricated backlog.
    /// </summary>
    public double BeginWrite(double nowSeconds)
    {
        lock (_lock)
        {
            if (double.IsNaN(_t0Seconds))
            {
                _t0Seconds = nowSeconds;
                return SampleRate;
            }

            double expected = (nowSeconds - _t0Seconds) * SampleRate;
            double error = _writerFrames - expected; // > 0: writer ahead of wall time

            if (-error > _gapThresholdFrames)
            {
                // The device produced nothing for a long span — true silence, not jitter. Land
                // the resumed audio at "now". The skipped span is zeroed so the reader can never
                // encounter stale samples from a previous lap of the ring.
                long target = (long)expected;
                ZeroFrames(Math.Max(_writerFrames, target - _capacityFrames), target);
                _writerFrames = target;
                _alignmentEma = 0;
                _gapJumps++;
                return SampleRate;
            }

            _alignmentEma = _alignmentEma * 0.97 + error * 0.03;
            return DriftCorrectedOutputRate(SampleRate, _alignmentEma);
        }
    }

    /// <summary>Appends resampled interleaved-stereo frames at the current write position.</summary>
    public void Append(ReadOnlySpan<short> interleavedStereo)
    {
        lock (_lock)
        {
            int frames = interleavedStereo.Length / 2;
            // Frames landing at positions the reader has already passed were sent as silence —
            // that audio is lost. Counted precisely so live diagnostics can attribute audible
            // damage to the capture stage (vs benign no-render silence, which appends nothing).
            _lateFrames += Math.Min(frames, Math.Max(0L, _readerFrames - _writerFrames));
            for (int i = 0; i < frames; i++)
            {
                int slot = (int)((_writerFrames + i) % _capacityFrames) * 2;
                _ring[slot] = interleavedStereo[i * 2];
                _ring[slot + 1] = interleavedStereo[i * 2 + 1];
            }
            _writerFrames += frames;
        }
    }

    /// <summary>
    /// Reads the next block. Positions not yet written are silence; the position ALWAYS advances,
    /// so audio arriving late is dropped rather than re-timed — the property that makes latency
    /// constant. Zero-fills entirely before the first write.
    /// </summary>
    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            // Never replay a lapped ring (reader stalled a whole capacity behind).
            if (_writerFrames - _readerFrames > _capacityFrames)
                _readerFrames = _writerFrames - _capacityFrames;

            int frames = interleavedStereo.Length / 2;
            for (int i = 0; i < frames; i++)
            {
                long pos = _readerFrames + i;
                if (pos < _writerFrames)
                {
                    int slot = (int)(pos % _capacityFrames) * 2;
                    interleavedStereo[i * 2] = _ring[slot];
                    interleavedStereo[i * 2 + 1] = _ring[slot + 1];
                }
                else
                {
                    interleavedStereo[i * 2] = 0;
                    interleavedStereo[i * 2 + 1] = 0;
                    _underrunFrames++;
                }
            }
            _readerFrames += frames;
        }
    }

    /// <summary>
    /// Re-aims the reader at the live edge minus the jitter margin — called at stream start so
    /// the first samples sent are fresh (audio buffered during connect is pure latency).
    /// </summary>
    public void FlushToLive()
    {
        lock (_lock)
        {
            _readerFrames = Math.Max(0, _writerFrames - _marginFrames);
        }
    }

    /// <summary>
    /// The producer resample rate that steers the write position toward wall time. Correction is
    /// bounded to ±0.2 % of <paramref name="baseRate"/> — far more than device-clock skew needs,
    /// and an inaudible pitch change (&lt;4 cents). Positive error (writer ahead) slows output.
    /// </summary>
    internal static double DriftCorrectedOutputRate(double baseRate, double alignmentErrorFrames)
    {
        double normalized = Math.Clamp(alignmentErrorFrames / 4410.0, -1.0, 1.0); // full at 100 ms
        return baseRate * (1.0 - normalized * 0.002);
    }

    private void ZeroFrames(long fromFrame, long toFrame)
    {
        for (long f = fromFrame; f < toFrame; f++)
        {
            int slot = (int)(f % _capacityFrames) * 2;
            _ring[slot] = 0;
            _ring[slot + 1] = 0;
        }
    }
}
