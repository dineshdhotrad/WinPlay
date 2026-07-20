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
}
