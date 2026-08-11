// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using WinPlay.Core.Raop;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies the AirPlay volume mapping and the RTSP SET_PARAMETER wire format (Task B5):
/// 0 → muted (−144 dB), 1..100 → linear −30..0 dB, and a locale-independent payload.
/// </summary>
public class VolumeControlTests
{
    [Theory]
    [InlineData(0.0, -144.0)]    // mute sentinel
    [InlineData(0.5, -144.0)]    // at/below the threshold is still mute
    [InlineData(1.0, -29.7)]     // just above mute → near minimum
    [InlineData(50.0, -15.0)]    // midpoint
    [InlineData(100.0, 0.0)]     // full
    [InlineData(150.0, 0.0)]     // clamped — never louder than 0 dB
    public void PercentToDb_Maps_The_Slider_To_The_AirPlay_Scale(double percent, double expectedDb)
        => Assert.Equal(expectedDb, VolumeControl.PercentToDb(percent), 3);

    [Theory]
    [InlineData(0.0, "volume: 0.000000\r\n")]
    [InlineData(-18.0, "volume: -18.000000\r\n")]
    [InlineData(-144.0, "volume: -144.000000\r\n")]
    public void FormatVolumeBody_Produces_The_Expected_Payload(double db, string expected)
        => Assert.Equal(expected, VolumeControl.FormatVolumeBody(db));

    [Theory]
    [InlineData(0.0, 100.0)]      // full
    [InlineData(-15.0, 50.0)]     // midpoint
    [InlineData(-30.0, 0.0)]      // minimum
    [InlineData(-144.0, 0.0)]     // mute sentinel clamps to 0
    [InlineData(5.0, 100.0)]      // above full clamps to 100
    public void DbToPercent_Maps_The_AirPlay_Scale_Back_To_The_Slider(double db, double expected)
        => Assert.Equal(expected, VolumeControl.DbToPercent(db), 3);

    [Theory]
    [InlineData(1.0)]
    [InlineData(25.0)]
    [InlineData(50.0)]
    [InlineData(75.0)]
    [InlineData(100.0)]
    public void Percent_To_Db_And_Back_Is_Lossless(double percent)
    {
        // A receiver-initiated volume must land the slider exactly where the user would put it,
        // otherwise the two ends drift apart each time the volume is nudged.
        double db = VolumeControl.PercentToDb(percent);
        Assert.Equal(percent, VolumeControl.DbToPercent(db), 3);
    }

    [Fact]
    public void FormatVolumeBody_Is_Locale_Independent()
    {
        // On a culture with a comma decimal separator, a naive format would emit
        // "volume: -18,000000" and the receiver would reject it. The invariant format must not.
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("volume: -18.000000\r\n", VolumeControl.FormatVolumeBody(-18.0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
