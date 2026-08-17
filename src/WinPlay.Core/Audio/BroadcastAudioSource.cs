// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// Tees one <see cref="IAudioSource"/> to any number of consumers. Every group/pair
/// member session must carry the SAME samples — independent captures would drift and
/// break multi-room alignment. Production is demand-driven: the fastest branch pulls
/// from the inner source into a shared ring; slower branches read history from it.
/// A branch that falls behind the ring capacity skips forward (live streaming — stale
/// audio is worse than a gap). All access is serialized on one lock; at 44.1 kHz
/// stereo the contended work is a few memcpys per 8 ms packet.
/// </summary>
public sealed class BroadcastAudioSource(IAudioSource inner, int capacitySamples = BroadcastAudioSource.DefaultCapacity) : IDisposable
{
    /// <summary>8 seconds of interleaved stereo at 44.1 kHz.</summary>
    public const int DefaultCapacity = 44100 * 2 * 8;

    private readonly short[] _ring = new short[capacitySamples];
    private readonly object _lock = new();
    private long _produced; // total interleaved samples ever pulled from inner
    private long _skippedSamples; // real audio this layer discarded because a branch fell behind
    private bool _disposed;

    /// <summary>Creates a consumer that starts at the current live position.</summary>
    public IAudioSource CreateBranch()
    {
        lock (_lock)
        {
            return new Branch(this, _produced);
        }
    }

    /// <summary>
    /// Milliseconds after which, with no branch having pulled a sample, this source is considered
    /// idle and the wrapped capture's own ring may be flushed. Comfortably longer than any pump's
    /// read cadence (~8 ms) so a LIVE consumer can never be mistaken for idleness, and far shorter
    /// than any human-scale gap between sessions.
    /// </summary>
    private const long IdleAfterMilliseconds = 250;

    private long _lastProduceMs;   // Environment.TickCount64 of the last inner read; 0 = never

    private long FlushAndGetLivePosition()
    {
        lock (_lock)
        {
            // Flush the wrapped capture's ring whenever NOBODY is consuming — measured directly
            // from production recency, not inferred from lifecycle. The previous condition was
            // `_produced == 0` ("never produced"), which was equivalent back when this object was
            // built fresh for every session and died with it. Once the capture became
            // process-lifetime, that condition was true exactly once per launch: every session
            // after the first found `_produced > 0`, skipped the inner flush, and REPLAYED the
            // audio that had accumulated during its own multi-second connect handshake — a
            // constant 5–6 s of latency, invisible to synthetic-source tests because a generated
            // tone has no backlog to replay. Production recency has no such blind spot: a live
            // co-destination's reads keep the timestamp fresh (so its audio is never yanked), and
            // any genuinely idle gap — including the very first use — flushes to live.
            if (Environment.TickCount64 - _lastProduceMs > IdleAfterMilliseconds)
            {
                (inner as IFlushableAudioSource)?.FlushToLive();
            }
            else if ((inner as ICaptureAheadAudioSource)?.SamplesAheadOfCursor is > 0 and long ahead)
            {
                // A consumer is LIVE, so the inner source must not be flushed out from under it —
                // but "live position" must still mean CAPTURE-live, not "as far as the live
                // consumer happened to have read". The produced counter trails capture-live by the
                // live consumer's send lead, and a joiner flushed to the stale counter inherits
                // that lead as a permanent render offset (16–46 ms of deterministic cross-room
                // flam, packet-size dependent). Producing forward to capture-live closes the gap;
                // the live consumer is untouched — its cursor still reads the same history from
                // this ring.
                Produce(ahead);
            }
            return _produced;
        }
    }

    /// <summary>
    /// Forwards the wrapped source's reported capture latency (0 when <paramref name="inner"/>
    /// cannot report one), so every <see cref="Branch"/> handed out by <see cref="CreateBranch"/>
    /// still tells the truth about how stale its samples are — a branch is not the device-level
    /// source itself, so without this forward it would silently read as "no latency" regardless of
    /// what the wrapped capture actually reports.
    /// </summary>
    private double InnerCaptureLatencySeconds => (inner as ICaptureLatency)?.CaptureLatencySeconds ?? 0;

    private IAudioSource InnerSource => inner;

    /// <summary>
    /// Forwards the wrapped source's capture-health counters, for the same reason
    /// <see cref="InnerCaptureLatencySeconds"/> is forwarded: a branch is not the device-level
    /// source, so without this every consumer saw zeroes and the one measurement that separates
    /// "the capture lost audio" from "this layer lost audio" was unreachable from where the
    /// problem is actually heard.
    /// </summary>
    private (long UnderrunFrames, long LateFrames, long GapJumps) InnerCaptureStats =>
        (inner as ICaptureDiagnostics)?.CaptureStats ?? (0, 0, 0);

    private void ReadAt(ref long cursor, Span<short> destination)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                destination.Clear();
                return;
            }

            long needed = cursor + destination.Length - _produced;
            if (needed > 0)
                Produce(needed);

            // Fell out of the ring window (consumer stalled): jump to the oldest
            // sample still buffered rather than serving wrapped garbage. Counted, because this
            // is THIS layer discarding real audio a consumer never got to send — indistinguishable
            // by ear from a capture-stage drop, and previously invisible in every diagnostic.
            long oldest = Math.Max(0, _produced - _ring.Length);
            if (cursor < oldest)
            {
                _skippedSamples += oldest - cursor;
                cursor = oldest;
            }

            for (int i = 0; i < destination.Length; i++)
                destination[i] = _ring[(cursor + i) % _ring.Length];
            cursor += destination.Length;
        }
    }

    private void Produce(long count)
    {
        // Pull in bounded chunks so a huge lag never asks the source for one giant read.
        Span<short> chunk = stackalloc short[704]; // one 352-frame stereo packet
        while (count > 0)
        {
            int n = (int)Math.Min(count, chunk.Length);
            var slice = chunk[..n];
            inner.Read(slice);
            _lastProduceMs = Environment.TickCount64;
            for (int i = 0; i < n; i++)
                _ring[(_produced + i) % _ring.Length] = slice[i];
            _produced += n;
            count -= n;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            inner.Dispose();
        }
    }

    private sealed class Branch(BroadcastAudioSource owner, long start) : IAudioSource,
        IPositionedAudioSource, IFlushableAudioSource, ICaptureLatency, ICaptureDiagnostics,
        ICaptureAheadAudioSource
    {
        /// <inheritdoc />
        public long SamplesAheadOfCursor
        {
            get
            {
                lock (owner._lock)
                    return (owner._produced - _cursor)
                           + ((owner.InnerSource as ICaptureAheadAudioSource)?.SamplesAheadOfCursor ?? 0);
            }
        }

        /// <summary>
        /// The capture's own counters, plus what the fan-out itself discarded folded into
        /// <c>LateFrames</c> — the counter that means "real audio that should have been sent was
        /// not". Where it was lost is a matter for the log line's separate fields; that it was lost
        /// is the same fact either way.
        /// </summary>
        public (long UnderrunFrames, long LateFrames, long GapJumps) CaptureStats
        {
            get
            {
                var (underrun, late, gaps) = owner.InnerCaptureStats;
                lock (owner._lock) return (underrun, late + owner._skippedSamples / 2, gaps);
            }
        }

        private long _cursor = start;
        private long _startSample = start; // interleaved-sample position this branch's frame 0 maps to

        /// <inheritdoc />
        public long StartPositionFrames => _startSample / 2; // interleaved stereo → frames

        /// <inheritdoc />
        public double CaptureLatencySeconds => owner.InnerCaptureLatencySeconds;

        public void Read(Span<short> interleavedStereo) => owner.ReadAt(ref _cursor, interleavedStereo);

        /// <summary>
        /// Re-aims this branch at the shared timeline's CURRENT live edge, discarding whatever it
        /// would otherwise replay from its creation point — the same purpose
        /// <see cref="ProcessLoopbackAudioSource.FlushToLive"/> serves for the underlying device
        /// ring, one layer up: time passes between a branch being created (e.g. before an RTSP
        /// handshake) and its first real read, and that gap must not become backlog. Must be called
        /// before the first <see cref="Read"/> — it moves the exposed <see cref="StartPositionFrames"/>
        /// too, so an RTP session stamping its frames against this branch stays truthful about the
        /// absolute position it will actually emit.
        /// </summary>
        public void FlushToLive()
        {
            long live = owner.FlushAndGetLivePosition();
            _cursor = live;
            _startSample = live;
        }

        public void Dispose() { } // branches don't own the shared source
    }
}
