// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies the multi-destination echo fix: <see cref="SystemAudioMover.CreateCaptureSource"/>
/// must hand every destination a BRANCH of one machine-wide capture, not a fresh capture each
/// call — two independent captures of the same system mix drift apart, so at the same rtp
/// timestamp two destinations would emit different content, which is the echo a listener hears
/// standing between two rooms.
///
/// <para>Uses the internal test-seam constructor to swap the real WASAPI capture for a
/// deterministic fake, so this is verifiable without an audio device.</para>
/// </summary>
public sealed class SystemAudioMoverCaptureTests
{
    /// <summary>Deterministic source: interleaved sample i has value i (mod short range).</summary>
    private sealed class CountingSource : IAudioSource
    {
        private long _position;
        public bool Disposed { get; private set; }

        public void Read(Span<short> interleavedStereo)
        {
            for (int i = 0; i < interleavedStereo.Length; i++)
                interleavedStereo[i] = unchecked((short)_position++);
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A source that reports a fixed capture latency, to verify
    /// <see cref="SystemAudioMover.CreateCaptureSource"/>'s returned handle forwards it.</summary>
    private sealed class LatencyReportingSource(double latencySeconds) : IAudioSource, ICaptureLatency
    {
        public double CaptureLatencySeconds => latencySeconds;
        public void Read(Span<short> interleavedStereo) => interleavedStereo.Clear();
        public void Dispose() { }
    }

    private static SystemAudioMover MakeMover(Func<IAudioSource> innerFactory)
    {
        string statePath = Path.Combine(Path.GetTempPath(), "winplay-mover-" + Guid.NewGuid().ToString("N") + ".json");
        var guardian = new AudioStateGuardian(new FakeEndpointVolumeController(), statePath);
        return new SystemAudioMover(guardian, innerFactory);
    }

    [Fact]
    public void Two_Destinations_Share_One_Capture_Not_Two()
    {
        int created = 0;
        using var mover = MakeMover(() => { created++; return new CountingSource(); });

        using var a = mover.CreateCaptureSource();
        using var b = mover.CreateCaptureSource();

        // The historical bug: CreateCaptureSource() built a brand new capture every call. One
        // machine capture must serve every destination.
        Assert.Equal(1, created);
    }

    [Fact]
    public void Two_Destinations_Reading_Together_See_Sample_Identical_Content()
    {
        using var mover = MakeMover(() => new CountingSource());
        using var a = mover.CreateCaptureSource();
        using var b = mover.CreateCaptureSource();

        short[] bufA = new short[704];
        short[] bufB = new short[704];
        a.Read(bufA);
        b.Read(bufB);

        // Same rtp position, same audio — two independent captures could never guarantee this,
        // which is exactly the echo the shared capture fixes.
        Assert.Equal(bufA, bufB);
    }

    [Fact]
    public void Capture_Survives_One_Destination_Stopping_While_Another_Streams()
    {
        int created = 0;
        using var mover = MakeMover(() => { created++; return new CountingSource(); });

        var a = mover.CreateCaptureSource();
        using var b = mover.CreateCaptureSource();
        a.Dispose(); // destination A stops

        // B must keep working — reading must not throw, and the shared capture must not have
        // been torn down or recreated just because ONE of its two consumers left.
        short[] buf = new short[704];
        var ex = Record.Exception(() => b.Read(buf));
        Assert.Null(ex);
        Assert.Equal(1, created);
    }

    /// <summary>
    /// The machine capture OUTLIVES the destinations that use it, and is torn down only when the
    /// mover itself is disposed — i.e. when the app exits.
    ///
    /// <para>It used to be torn down as soon as the last destination stopped, and rebuilt on the
    /// next connect. That rebuild is a path only a SOLO destination ever exercises: switch one
    /// speaker on and off by itself and every connect after the first got a freshly constructed
    /// WASAPI process-loopback capture, while a speaker kept on alongside others never triggered
    /// it at all. When a rebuilt capture came up delivering digital silence, nothing anywhere
    /// reported it — the session was live, the pump paced, packets were well-formed, PTP held, no
    /// audio was dropped, so every health counter read clean while the room stayed quiet. One
    /// capture for the process lifetime removes the re-initialisation instead of trying to make it
    /// reliable.</para>
    /// </summary>
    [Fact]
    public void Capture_Outlives_Its_Destinations_And_Dies_With_The_Mover()
    {
        CountingSource? inner = null;
        var mover = MakeMover(() => inner = new CountingSource());

        var a = mover.CreateCaptureSource();
        var b = mover.CreateCaptureSource();
        a.Dispose();
        Assert.False(inner!.Disposed);

        b.Dispose();
        Assert.False(inner.Disposed);   // last destination left — the capture STAYS

        mover.Dispose();
        Assert.True(inner.Disposed);    // the app is exiting — now it goes
    }

    /// <summary>
    /// The SAME capture is handed to a destination that connects after every previous one stopped.
    /// Rebuilding here is what made a solo speaker's second and later connects unreliable.
    /// </summary>
    [Fact]
    public void The_Same_Capture_Serves_A_Destination_That_Connects_Later()
    {
        int created = 0;
        using var mover = MakeMover(() => { created++; return new CountingSource(); });

        var a = mover.CreateCaptureSource();
        a.Dispose();
        Assert.Equal(1, created);

        // Nothing was streaming in between — the next destination reuses the one machine capture.
        var b = mover.CreateCaptureSource();
        Assert.Equal(1, created);
        b.Dispose();
    }

    [Fact]
    public void A_Later_Destination_Gets_A_Nonzero_Start_Offset_On_The_Shared_Timeline()
    {
        using var mover = MakeMover(() => new CountingSource());
        using var a = mover.CreateCaptureSource();

        short[] buf = new short[704]; // 352 frames, interleaved stereo
        a.Read(buf); // advances the shared capture's live position by 352 frames

        using var b = mover.CreateCaptureSource();

        long offsetA = ((IPositionedAudioSource)a).StartPositionFrames;
        long offsetB = ((IPositionedAudioSource)b).StartPositionFrames;

        Assert.Equal(0, offsetA);
        Assert.Equal(352, offsetB);
        Assert.NotEqual(offsetA, offsetB);
    }

    [Fact]
    public void CreateCaptureSource_Forwards_The_Shared_Captures_Reported_Latency()
    {
        // The mirroring lip-sync fix reads ICaptureLatency off exactly what CreateCaptureSource
        // hands out (a RefCountedCapture wrapping a BroadcastAudioSource branch). Either layer
        // dropping the capability instead of forwarding it would make MirrorSession read zero
        // latency regardless of what the real device-level capture reports.
        using var mover = MakeMover(() => new LatencyReportingSource(0.075));
        using var a = mover.CreateCaptureSource();

        Assert.Equal(0.075, ((ICaptureLatency)a).CaptureLatencySeconds);
    }

    [Fact]
    public void CreateCaptureSource_Reports_Zero_Latency_When_The_Underlying_Capture_Cannot_Report_One()
    {
        using var mover = MakeMover(() => new CountingSource());
        using var a = mover.CreateCaptureSource();

        Assert.Equal(0, ((ICaptureLatency)a).CaptureLatencySeconds);
    }
}
