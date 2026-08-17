// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies the capture drift controller (now inside <see cref="PositionedCaptureRing"/>):
/// the corrected producer rate steers the write position toward wall time, stays within ±0.2 %
/// (an inaudible pitch change), and moves in the converging direction.
/// </summary>
public class DriftControlTests
{
    private const double Base = 44100.0;

    [Fact]
    public void Zero_Alignment_Error_Leaves_The_Rate_Unchanged()
        => Assert.Equal(Base, PositionedCaptureRing.DriftCorrectedOutputRate(Base, 0), 6);

    [Fact]
    public void A_Writer_Ahead_Of_Wall_Time_Is_Slowed()
    {
        double rate = PositionedCaptureRing.DriftCorrectedOutputRate(Base, alignmentErrorFrames: 2000);
        Assert.True(rate < Base, $"expected a slower rate when the writer runs ahead, got {rate}");
    }

    [Fact]
    public void A_Writer_Behind_Wall_Time_Is_Sped_Up()
    {
        double rate = PositionedCaptureRing.DriftCorrectedOutputRate(Base, alignmentErrorFrames: -2000);
        Assert.True(rate > Base, $"expected a faster rate when the writer lags, got {rate}");
    }

    [Fact]
    public void Correction_Is_Bounded_To_Plus_Minus_0_2_Percent()
    {
        double behind = PositionedCaptureRing.DriftCorrectedOutputRate(Base, -1_000_000);
        double ahead = PositionedCaptureRing.DriftCorrectedOutputRate(Base, 1_000_000);
        Assert.Equal(Base * 1.002, behind, 3); // ≤ +0.2 %
        Assert.Equal(Base * 0.998, ahead, 3);  // ≥ −0.2 %
    }

    [Fact]
    public void Rate_Decreases_Monotonically_As_The_Writer_Gets_Further_Ahead()
    {
        double prev = double.MaxValue;
        for (int error = -8000; error <= 8000; error += 250)
        {
            double rate = PositionedCaptureRing.DriftCorrectedOutputRate(Base, error);
            Assert.True(rate <= prev, $"rate must not increase as the error grows (error {error})");
            prev = rate;
        }
    }
}
