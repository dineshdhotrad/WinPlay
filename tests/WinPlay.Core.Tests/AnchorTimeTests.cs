// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Locks in the SETRATEANCHORTIME network-time encoding (Task C4). networkTimeFrac must be a
/// 2^-64 fixed-point fraction of a second — verified live: sending raw nanoseconds anchored the
/// stream in the past and the HomePods played silence; this encoding made buffered audio play.
/// The test recovers nanoseconds with the receiver's exact inverse (shairport-sync
/// handle_setrateanchori) and asserts a lossless round-trip.
/// </summary>
public class AnchorTimeTests
{
    /// <summary>The receiver's inverse: secs*1e9 + (frac * 1e9 >> 64).</summary>
    private static ulong RecoverNanos(ulong secs, ulong frac)
        => secs * 1_000_000_000UL + (ulong)(((System.UInt128)frac * 1_000_000_000UL) >> 64);

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(500_000_000UL)]          // exactly half a second
    [InlineData(999_999_999UL)]          // just under a second
    [InlineData(1_000_000_000UL)]        // one second exactly
    [InlineData(1_234_567_890_123UL)]    // arbitrary
    [InlineData(1_700_000_000_000_000_000UL)] // a large, realistic monotonic ns value
    public void NetworkTime_RoundTrips_Through_The_Receivers_Inverse(ulong nanos)
    {
        var (secs, frac) = RaopSession.AnchorNetworkTime(nanos);
        ulong recovered = RecoverNanos(secs, frac);
        // The 2^-64 → ns reduction loses at most sub-nanosecond precision.
        Assert.True(recovered <= nanos && nanos - recovered <= 1,
            $"expected ~{nanos}, receiver recovered {recovered}");
    }

    [Fact]
    public void Half_Second_Fraction_Is_The_Msb()
    {
        // 0.5 s must encode as a fraction whose most-significant bit is set (2^63).
        var (_, frac) = RaopSession.AnchorNetworkTime(1_500_000_000UL); // 1.5 s
        Assert.Equal(1UL << 63, frac);
    }

    [Fact]
    public void Whole_Seconds_Have_A_Zero_Fraction()
    {
        var (secs, frac) = RaopSession.AnchorNetworkTime(3_000_000_000UL);
        Assert.Equal(3UL, secs);
        Assert.Equal(0UL, frac);
    }
}
