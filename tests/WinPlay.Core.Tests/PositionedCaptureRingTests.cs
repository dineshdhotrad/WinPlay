// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Proves the property that fixes the growing-latency bug (Task C4): with a positioned ring,
/// capture jitter and silence gaps can NEVER shift audio later on the timeline — the
/// writer-to-reader distance (the latency) returns to the margin after every disturbance,
/// instead of accumulating as a FIFO does. Each test simulates the real threads: a "wall clock"
/// advancing in 10 ms ticks, a writer appending 441 frames per tick (the WASAPI callback), and
/// a reader consuming 441 frames per tick (the QPC-paced RTP pump).
/// </summary>
public class PositionedCaptureRingTests
{
    private const int Tick = 441;          // frames per 10 ms at 44.1 kHz
    private const int Capacity = 4410;     // 100 ms ring (small, to exercise lapping)
    private const int Margin = 441;        // 10 ms reader margin
    private const int GapThreshold = 2205; // 50 ms — larger holes are true silence

    private static PositionedCaptureRing NewRing() => new(Capacity, Margin, GapThreshold);

    /// <summary>One writer tick: BeginWrite at simulated time <paramref name="t"/>, append 441 frames of <paramref name="value"/>.</summary>
    private static void WriteTick(PositionedCaptureRing ring, double t, short value)
    {
        ring.BeginWrite(t);
        short[] block = new short[Tick * 2];
        Array.Fill(block, value);
        ring.Append(block);
    }

    private static short[] ReadTick(PositionedCaptureRing ring)
    {
        short[] block = new short[Tick * 2];
        ring.Read(block);
        return block;
    }

    /// <summary>The latency measure: how far the reader trails the writer, in frames.</summary>
    private static long Age(PositionedCaptureRing ring) => ring.WriterFrames - ring.ReaderFrames;

    [Fact]
    public void Steady_Flow_Holds_A_Constant_Age()
    {
        var ring = NewRing();
        double t = 0;
        for (int i = 0; i < 20; i++) { WriteTick(ring, t, 7); t += 0.010; }
        ring.FlushToLive();
        long initialAge = Age(ring);
        Assert.Equal(Margin, initialAge);

        for (int i = 0; i < 500; i++)
        {
            WriteTick(ring, t, 7);
            t += 0.010;
            ReadTick(ring);
        }

        Assert.Equal(initialAge, Age(ring)); // five seconds later: identical — zero creep
    }

    [Fact]
    public void Jitter_Burst_Recovers_To_The_Same_Age_Not_A_Larger_One()
    {
        var ring = NewRing();
        double t = 0;
        for (int i = 0; i < 20; i++) { WriteTick(ring, t, 7); t += 0.010; }
        ring.FlushToLive();
        long steadyAge = Age(ring);

        // 30 ms of callback jitter (< gap threshold): the reader keeps consuming, the writer
        // delivers nothing…
        for (int i = 0; i < 3; i++) { t += 0.010; ReadTick(ring); }

        // …then the delayed audio arrives as one contiguous burst at its ORIGINAL positions.
        for (int i = 0; i < 3; i++) WriteTick(ring, t, 7);

        // Resume steady flow: the age must return exactly to the pre-jitter margin. A FIFO
        // would sit at steadyAge + 30 ms forever — the creeping-latency bug.
        for (int i = 0; i < 50; i++)
        {
            WriteTick(ring, t, 7);
            t += 0.010;
            ReadTick(ring);
        }
        Assert.Equal(steadyAge, Age(ring));
    }

    [Fact]
    public void True_Silence_Gap_Resumes_Live_With_No_Backlog()
    {
        var ring = NewRing();
        double t = 0;
        for (int i = 0; i < 20; i++) { WriteTick(ring, t, 7); t += 0.010; }
        ring.FlushToLive();
        long steadyAge = Age(ring);

        // 500 ms of true silence: WASAPI delivers nothing; the reader keeps its constant pace.
        for (int i = 0; i < 50; i++) { t += 0.010; ReadTick(ring); }

        // Capture resumes: the writer must jump to "now" so resumed audio lands live.
        for (int i = 0; i < 50; i++)
        {
            WriteTick(ring, t, 9);
            t += 0.010;
            ReadTick(ring);
        }

        // The half-second of silence added NOTHING to the latency.
        Assert.True(Math.Abs(Age(ring) - steadyAge) <= Tick,
            $"age after silence gap = {Age(ring)}, steady = {steadyAge} — backlog accumulated");
    }

    [Fact]
    public void Skipped_Span_After_A_Gap_Reads_As_Silence_Not_Stale_Audio()
    {
        var ring = NewRing();
        double t = 0;
        // Fill past one full ring lap with nonzero so every slot holds old audio.
        for (int i = 0; i < 30; i++) { WriteTick(ring, t, 7); t += 0.010; }
        long stallPosition = ring.ReaderFrames;

        // The reader stalls; capture goes silent for 60 ms (> threshold) and then resumes,
        // which jumps the writer forward across the skipped span.
        t += 0.060;
        WriteTick(ring, t, 9);

        // The reader now traverses the skipped span: it must see SILENCE (zeros), never the
        // lap-old samples that previously occupied those ring slots.
        ring.FlushToLive(); // aim at live edge
        long liveStart = ring.ReaderFrames;
        Assert.True(liveStart > stallPosition);
        short[] block = ReadTick(ring);
        Assert.All(block, s => Assert.True(s is 0 or 9, $"stale sample {s} leaked from a previous lap"));
    }

    [Fact]
    public void Underrun_ZeroFills_And_Never_Replays_Late_Data()
    {
        var ring = NewRing();
        double t = 0;
        WriteTick(ring, t, 7); t += 0.010;
        ring.FlushToLive();

        // Read far past the writer: zero-filled, position advances anyway.
        short[] first = ReadTick(ring);
        short[] second = ReadTick(ring); // entirely beyond the writer → all zeros
        Assert.Contains(first, s => s == 7);
        Assert.All(second, s => Assert.Equal(0, s));
        long positionAfterUnderrun = ring.ReaderFrames;

        // The "late" audio for those passed positions arrives now (still below gap threshold).
        WriteTick(ring, t, 8);

        // The reader must not go back for it: its position is unchanged and the next read
        // returns data from the current position forward only.
        Assert.Equal(positionAfterUnderrun, ring.ReaderFrames);
        ReadTick(ring);
        Assert.Equal(positionAfterUnderrun + Tick, ring.ReaderFrames);
    }

    [Fact]
    public void Late_Frames_Count_Only_Audio_That_Arrived_After_Its_Position_Played()
    {
        var ring = NewRing();
        double t = 0;
        WriteTick(ring, t, 7); t += 0.010;
        ring.FlushToLive();

        // Reader passes the writer by two ticks (zero-filled), then the delayed audio arrives.
        ReadTick(ring);
        ReadTick(ring);
        ReadTick(ring);
        long underBefore = ring.UnderrunFrames;
        Assert.True(underBefore > 0);
        Assert.Equal(0, ring.LateFrames);   // nothing dropped yet — nothing has arrived late

        WriteTick(ring, t, 8);              // arrives late: its positions were already read
        Assert.Equal(Tick, ring.LateFrames); // exactly one tick of real audio was lost

        // Benign silence (reader ahead, writer never appends) must NOT count as late.
        ReadTick(ring);
        Assert.Equal(Tick, ring.LateFrames);
    }

    [Fact]
    public void Read_Before_Any_Write_Is_Silence_And_Advances()
    {
        var ring = NewRing();
        short[] block = ReadTick(ring);
        Assert.All(block, s => Assert.Equal(0, s));
        Assert.Equal(Tick, ring.ReaderFrames);
    }

    [Fact]
    public void FlushToLive_Aims_The_Reader_A_Margin_Behind_The_Writer()
    {
        var ring = NewRing();
        double t = 0;
        for (int i = 0; i < 10; i++) { WriteTick(ring, t, 7); t += 0.010; }
        ring.FlushToLive();
        Assert.Equal(ring.WriterFrames - Margin, ring.ReaderFrames);
    }

    [Fact]
    public void A_Stalled_Reader_Skips_Forward_Rather_Than_Replaying_A_Lapped_Ring()
    {
        var ring = NewRing();
        double t = 0;
        // Write far more than the capacity while the reader never reads.
        for (int i = 0; i < 40; i++) { WriteTick(ring, t, 7); t += 0.010; }

        ReadTick(ring);
        // The reader may serve at most one capacity of history — stale audio is dropped.
        Assert.True(ring.WriterFrames - ring.ReaderFrames <= Capacity);
    }
}
