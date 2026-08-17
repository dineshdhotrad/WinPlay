// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

public class BroadcastAudioSourceTests
{
    /// <summary>Deterministic source: sample i has value i (mod short range).</summary>
    private sealed class CountingSource : IAudioSource
    {
        public long Position { get; private set; }
        public bool Disposed { get; private set; }

        public void Read(Span<short> interleavedStereo)
        {
            for (int i = 0; i < interleavedStereo.Length; i++)
                interleavedStereo[i] = unchecked((short)Position++);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A source whose FlushToLive is observable, to verify WHEN it gets called.</summary>
    private sealed class FlushableCountingSource : IAudioSource, IFlushableAudioSource
    {
        private long _position;
        public int FlushCount { get; private set; }

        public void Read(Span<short> interleavedStereo)
        {
            for (int i = 0; i < interleavedStereo.Length; i++)
                interleavedStereo[i] = unchecked((short)_position++);
        }

        public void FlushToLive() => FlushCount++;

        public void Dispose() { }
    }

    /// <summary>A source that reports a fixed capture latency, to verify a branch handed out by
    /// <see cref="BroadcastAudioSource.CreateBranch"/> forwards it rather than silently reading as
    /// unreported.</summary>
    private sealed class LatencyReportingSource(double latencySeconds) : IAudioSource, ICaptureLatency
    {
        public double CaptureLatencySeconds => latencySeconds;
        public void Read(Span<short> interleavedStereo) => interleavedStereo.Clear();
        public void Dispose() { }
    }

    [Fact]
    public void TwoBranches_ReceiveIdenticalSamples()
    {
        using var broadcast = new BroadcastAudioSource(new CountingSource());
        var a = broadcast.CreateBranch();
        var b = broadcast.CreateBranch();

        short[] bufA = new short[704];
        short[] bufB = new short[704];
        for (int round = 0; round < 3; round++)
        {
            a.Read(bufA);
            b.Read(bufB);
            Assert.Equal(bufA, bufB);
            Assert.Equal(unchecked((short)(round * 704)), bufA[0]); // sequential, from 0
        }
    }

    [Fact]
    public void ProductionIsShared_NotDuplicated()
    {
        var inner = new CountingSource();
        using var broadcast = new BroadcastAudioSource(inner);
        var a = broadcast.CreateBranch();
        var b = broadcast.CreateBranch();

        short[] buf = new short[704];
        a.Read(buf);
        b.Read(buf);
        // Both branches consumed the same 704 samples — the inner source advanced once.
        Assert.Equal(704, inner.Position);
    }

    [Fact]
    public void LateBranch_StartsAtLivePosition()
    {
        using var broadcast = new BroadcastAudioSource(new CountingSource());
        var early = broadcast.CreateBranch();
        short[] buf = new short[704];
        early.Read(buf);

        var late = broadcast.CreateBranch();
        late.Read(buf);
        Assert.Equal(unchecked((short)704), buf[0]); // no replay of history
    }

    [Fact]
    public void StalledBranch_SkipsForwardInsteadOfServingWrappedData()
    {
        using var broadcast = new BroadcastAudioSource(new CountingSource(), capacitySamples: 2048);
        var stalled = broadcast.CreateBranch();
        var active = broadcast.CreateBranch();

        short[] big = new short[2048];
        active.Read(big);
        active.Read(big); // produced = 4096, ring holds [2048, 4096)

        short[] buf = new short[704];
        stalled.Read(buf);
        // Oldest retained sample is 2048 — the stalled branch resumes there.
        Assert.Equal(unchecked((short)2048), buf[0]);
        Assert.Equal(unchecked((short)(2048 + 703)), buf[703]);
    }

    [Fact]
    public void Branches_Taken_At_Different_Times_Report_Different_Start_Offsets()
    {
        // The multi-destination echo fix hinges on this: a branch created LATER, once the shared
        // capture is already flowing, must know how far along the timeline it started — otherwise
        // every destination stamps its own first sample as "frame 0" and two destinations play
        // different content at the same rtp timestamp.
        using var broadcast = new BroadcastAudioSource(new CountingSource());
        var early = (IPositionedAudioSource)broadcast.CreateBranch();

        short[] buf = new short[704]; // 352 frames, interleaved stereo
        ((IAudioSource)early).Read(buf); // advances the shared capture's live position by 352 frames

        var late = (IPositionedAudioSource)broadcast.CreateBranch();

        Assert.Equal(0, early.StartPositionFrames);
        Assert.Equal(352, late.StartPositionFrames);
        Assert.NotEqual(early.StartPositionFrames, late.StartPositionFrames);
    }

    [Fact]
    public void FlushToLive_Reseats_The_Branch_And_Its_Reported_Start_Offset()
    {
        // A branch is typically created BEFORE its destination's connect handshake and flushed
        // AFTER — the gap between the two must not become backlog, and StartPositionFrames must
        // reflect where the branch will actually start reading from, not where it was created.
        using var broadcast = new BroadcastAudioSource(new CountingSource());
        var early = broadcast.CreateBranch();
        short[] buf = new short[704];
        early.Read(buf); // produced = 704 samples = 352 frames

        var branch = broadcast.CreateBranch(); // created at 352 frames
        early.Read(buf); // produced = 1408 samples = 704 frames, while `branch` sits idle

        ((IFlushableAudioSource)branch).FlushToLive();

        Assert.Equal(704, ((IPositionedAudioSource)branch).StartPositionFrames);
        branch.Read(buf);
        Assert.Equal(unchecked((short)1408), buf[0]); // reads live content, not its creation-time backlog
    }

    [Fact]
    public void FlushToLive_On_An_Idle_Capture_Also_Flushes_The_Inner_Source()
    {
        // Real time passes between the capture being constructed and the first flush (an RTSP
        // handshake, a volume round-trip). Nothing has been produced yet, so that gap sits entirely
        // inside the wrapped device-level source's own ring — re-aiming only the branch's cursor
        // (already 0) would discard nothing. The inner source must be flushed too.
        var inner = new FlushableCountingSource();
        using var broadcast = new BroadcastAudioSource(inner);
        var branch = broadcast.CreateBranch();

        ((IFlushableAudioSource)branch).FlushToLive();

        Assert.Equal(1, inner.FlushCount);
    }

    [Fact]
    public void FlushToLive_On_An_Already_Producing_Capture_Does_Not_Disturb_The_Inner_Source()
    {
        // A second destination joining a capture already feeding another must never re-flush the
        // device-level source — that would yank live content out from under the destination
        // already consuming it, an audible glitch for someone who was there first.
        var inner = new FlushableCountingSource();
        using var broadcast = new BroadcastAudioSource(inner);
        var first = broadcast.CreateBranch();
        short[] buf = new short[704];
        first.Read(buf); // production has started

        var second = broadcast.CreateBranch();
        ((IFlushableAudioSource)second).FlushToLive();

        Assert.Equal(0, inner.FlushCount);
    }

    [Fact]
    public void Dispose_DisposesInner_AndBranchesGoSilent()
    {
        var inner = new CountingSource();
        var broadcast = new BroadcastAudioSource(inner);
        var branch = broadcast.CreateBranch();
        broadcast.Dispose();

        Assert.True(inner.Disposed);
        short[] buf = new short[64];
        buf[0] = 1234;
        branch.Read(buf);
        Assert.All(buf, s => Assert.Equal(0, s)); // silence after dispose, no throw
    }

    [Fact]
    public void Branch_Forwards_The_Inner_Sources_Reported_Capture_Latency()
    {
        // Part of the mirroring lip-sync fix: MirrorSession reads ICaptureLatency off whatever
        // SystemAudioMover hands it, which is a branch of this class wrapped again by
        // SystemAudioMover's own ref-counted handle. If either wrapper dropped the capability
        // instead of forwarding it, the reported latency would silently read as zero and the fix
        // would do nothing despite the underlying source reporting a real number.
        using var broadcast = new BroadcastAudioSource(new LatencyReportingSource(0.075));
        var branch = broadcast.CreateBranch();

        Assert.Equal(0.075, ((ICaptureLatency)branch).CaptureLatencySeconds);
    }

    [Fact]
    public void Branch_Reports_Zero_Latency_When_The_Inner_Source_Cannot_Report_One()
    {
        // CountingSource does not implement ICaptureLatency — the degrade-to-today's-behaviour path.
        using var broadcast = new BroadcastAudioSource(new CountingSource());
        var branch = broadcast.CreateBranch();

        Assert.Equal(0, ((ICaptureLatency)branch).CaptureLatencySeconds);
    }
}
