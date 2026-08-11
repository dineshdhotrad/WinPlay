// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Capture;
using Xunit;

namespace WinPlay.Capture.Tests;

/// <summary>
/// Unit coverage for the pure decision logic behind GPU-capture recovery (A5) and mirror
/// resolution negotiation (C6). The DXGI/D3D interaction itself is exercised live on real
/// hardware (mode change, secure desktop, GPU TDR) — see the capture soak test — but the
/// policy that drives recovery is deterministic and tested here.
/// </summary>
public class RecoveryPolicyTests
{
    [Theory]
    [InlineData(0, 50)]      // first retry starts at 50 ms
    [InlineData(50, 100)]    // then doubles
    [InlineData(100, 200)]
    [InlineData(400, 800)]
    [InlineData(800, 1000)]  // doubling 800→1600 is capped at the 1 s ceiling
    [InlineData(1000, 1000)] // stays capped
    public void NextBackoffMs_Doubles_With_A_One_Second_Ceiling(int current, int expected)
        => Assert.Equal(expected, ScreenMirrorSource.NextBackoffMs(current));

    [Theory]
    [InlineData(0, 250)]   // clamped to attempt >= 1
    [InlineData(1, 250)]
    [InlineData(2, 500)]
    [InlineData(5, 1250)]
    [InlineData(6, 1500)]  // 250*6 = 1500 ceiling
    [InlineData(20, 1500)] // stays capped, so a restart always lands within the 2 s budget
    public void RestartBackoffMs_Steps_By_250ms_To_A_1500ms_Ceiling(int attempt, int expected)
        => Assert.Equal(expected, SupervisedMirrorSource.RestartBackoffMs(attempt));

    [Fact]
    public void NegotiateEncodeSize_Uses_Desktop_Size_When_No_Receiver_Display_Advertised()
        => Assert.Equal((2560, 1440), ScreenMirrorSource.NegotiateEncodeSize(2560, 1440, 0, 0));

    [Fact]
    public void NegotiateEncodeSize_Rounds_Odd_Dimensions_Down_To_Even_For_NV12()
        => Assert.Equal((2558, 1438), ScreenMirrorSource.NegotiateEncodeSize(2559, 1439, 0, 0));

    [Fact]
    public void NegotiateEncodeSize_Leaves_A_Desktop_Smaller_Than_The_Display_Untouched()
        => Assert.Equal((1920, 1080), ScreenMirrorSource.NegotiateEncodeSize(1920, 1080, 3840, 2160));

    [Fact]
    public void NegotiateEncodeSize_Scales_Down_Preserving_Aspect_Ratio()
        => Assert.Equal((1920, 1080), ScreenMirrorSource.NegotiateEncodeSize(3840, 2160, 1920, 1080));

    [Fact]
    public void NegotiateEncodeSize_Fits_To_The_Tighter_Axis_On_A_Mismatched_Display()
        // scale = min(1920/2560, 1200/1440) = min(0.75, 0.833) = 0.75 → 1920x1080
        => Assert.Equal((1920, 1080), ScreenMirrorSource.NegotiateEncodeSize(2560, 1440, 1920, 1200));

    [Fact]
    public void AutoBitrate_Clamps_A_Tiny_Resolution_Up_To_The_Floor()
        => Assert.Equal(4_000_000, ScreenMirrorSource.AutoBitrate(640, 480, 30));

    [Fact]
    public void AutoBitrate_Scales_Linearly_In_The_Normal_Range()
        // 1920*1080*60*0.10 = 12,441,600
        => Assert.Equal(12_441_600, ScreenMirrorSource.AutoBitrate(1920, 1080, 60));

    [Fact]
    public void AutoBitrate_Clamps_An_Enormous_Resolution_Down_To_The_Ceiling()
        => Assert.Equal(60_000_000, ScreenMirrorSource.AutoBitrate(7680, 4320, 60));
}
