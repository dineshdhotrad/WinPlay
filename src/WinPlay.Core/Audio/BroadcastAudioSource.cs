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
    private bool _disposed;

    /// <summary>Creates a consumer that starts at the current live position.</summary>
    public IAudioSource CreateBranch()
    {
        lock (_lock)
        {
            return new Branch(this, _produced);
        }
    }

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
            // sample still buffered rather than serving wrapped garbage.
            long oldest = Math.Max(0, _produced - _ring.Length);
            if (cursor < oldest)
                cursor = oldest;

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

    private sealed class Branch(BroadcastAudioSource owner, long start) : IAudioSource
    {
        private long _cursor = start;

        public void Read(Span<short> interleavedStereo) => owner.ReadAt(ref _cursor, interleavedStereo);

        public void Dispose() { } // branches don't own the shared source
    }
}
