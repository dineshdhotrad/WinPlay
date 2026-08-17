// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using WinPlay.Core.Mirror;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Locks in the two missing-capture-latency terms behind the mirroring lip-sync fix (screen
/// mirroring with audio lagged the picture by ~60-70 ms):
///
/// <list type="bullet">
/// <item><see cref="MirrorLatency.AnchorFrames"/> — the audio sync loop's playout anchor must be
/// pulled forward by however much the capture source's newest sample is already stale, or the
/// receiver is promised the full 250 ms budget behind a sample that was itself already late.</item>
/// <item><see cref="MirrorClock.FrameTimestampAt"/> — a video frame's AirPlay timestamp must be
/// derived from the tick it was actually CAPTURED at, not from whenever the FrameEncoded event
/// happens to be observed (which, for the supervised capture-host child, is after a pipe hop).</item>
/// </list>
/// </summary>
public class MirrorLatencyTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void AnchorFrames_Is_Reduced_By_The_Reported_Capture_Latency()
    {
        long rawBudget = MirrorLatency.Frames(SampleRate); // 11025 frames = 250 ms
        long captureLatencyFrames = 2646 + 441; // ~60 ms ring margin + ~10 ms WASAPI callback period

        long anchor = MirrorLatency.AnchorFrames(SampleRate, captureLatencyFrames);

        Assert.Equal(rawBudget - captureLatencyFrames, anchor);
        Assert.True(anchor < rawBudget,
            "the capture-latency-aware anchor must sit nearer 'now' than the raw 250 ms budget");
    }

    [Fact]
    public void AnchorFrames_With_Zero_Capture_Latency_Reproduces_Todays_Behaviour()
    {
        // A source that cannot report ICaptureLatency must not change behaviour at all — this is
        // the degrade path the capability interface exists for.
        Assert.Equal(MirrorLatency.Frames(SampleRate), MirrorLatency.AnchorFrames(SampleRate, captureLatencyFrames: 0));
    }

    [Fact]
    public void AnchorFrames_Is_Floored_At_One_Frame()
    {
        // A pathological capture latency (>= the whole budget) must never zero out or underflow the
        // sync packet's playout-point field (offset 4 = rtpNow - anchorFrames, written as uint).
        long rawBudget = MirrorLatency.Frames(SampleRate);
        Assert.Equal(1, MirrorLatency.AnchorFrames(SampleRate, rawBudget));
        Assert.Equal(1, MirrorLatency.AnchorFrames(SampleRate, rawBudget * 10));
    }

    [Fact]
    public void FrameTimestampAt_An_Older_Capture_Tick_Produces_An_Earlier_Timestamp()
    {
        var clock = new MirrorClock();

        long earlierTick = Stopwatch.GetTimestamp();
        Thread.Sleep(30); // a stand-in for capture→encode→pipe-relay delay, exaggerated to avoid flakiness
        long laterTick = Stopwatch.GetTimestamp();

        ulong earlierTs = clock.FrameTimestampAt(earlierTick);
        ulong laterTs = clock.FrameTimestampAt(laterTick);

        Assert.True(earlierTs < laterTs,
            $"a frame captured earlier must carry an earlier AirPlay timestamp (earlier={earlierTs}, later={laterTs})");

        // The gap between the two NTP timestamps should track the real elapsed time (both are
        // 32.32 fixed-point seconds sharing one epoch, so a plain difference / 2^32 is valid here).
        double deltaSeconds = (laterTs - earlierTs) / 4294967296.0;
        Assert.InRange(deltaSeconds, 0.015, 2.0); // generous bounds around the 30ms sleep for CI jitter
    }

    [Fact]
    public void FrameTimestampAt_Agrees_With_FrameTimestamp_For_The_Same_Instant()
    {
        var clock = new MirrorClock();
        long now = Stopwatch.GetTimestamp();

        ulong viaCaptureTick = clock.FrameTimestampAt(now);
        ulong viaNow = clock.FrameTimestamp; // re-samples Stopwatch.GetTimestamp() a few microseconds later

        Assert.True(viaNow >= viaCaptureTick);
        double deltaSeconds = (viaNow - viaCaptureTick) / 4294967296.0;
        Assert.True(deltaSeconds < 0.05, $"expected the two calls to land within 50ms, was {deltaSeconds * 1000:F3} ms");
    }
}
