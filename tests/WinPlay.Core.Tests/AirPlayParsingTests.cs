// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Discovery;
using Xunit;

namespace WinPlay.Core.Tests;

public class AirPlayParsingTests
{
    [Theory]
    [InlineData(null, 0UL)]
    [InlineData("", 0UL)]
    [InlineData("0x30", 0x30UL)]
    [InlineData("48", 48UL)]
    [InlineData("0x00000A00,0x00080200", 0x00080200_00000A00UL)]
    [InlineData("0x4A7FCA00,0x3C155FDE", 0x3C155FDE_4A7FCA00UL)]
    [InlineData("garbage", 0UL)]
    public void ParseFeatures_Handles_All_Advertised_Formats(string? input, ulong expected)
    {
        Assert.Equal(expected, AirPlayFeaturesExtensions.ParseFeatures(input));
    }

    [Fact]
    public void ParseFeatures_Low_Word_Comes_First()
    {
        // "0x00000A00,0x00080200": low word has bits 9+11, high word bits 9+19 → 41+51.
        var f = (AirPlayFeatures)AirPlayFeaturesExtensions.ParseFeatures("0x00000A00,0x00080200");
        Assert.True(f.HasFlag(AirPlayFeatures.SupportsAirPlayAudio));      // bit 9
        Assert.True(f.HasFlag(AirPlayFeatures.AudioRedundant));            // bit 11
        Assert.True(f.HasFlag(AirPlayFeatures.SupportsPtp));               // bit 41
        Assert.True(f.HasFlag(AirPlayFeatures.SupportsUnifiedPairSetupAndMfi)); // bit 51
        Assert.False(f.HasFlag(AirPlayFeatures.SupportsAirPlayScreen));
    }

    [Theory]
    [InlineData("AppleTV14,1", 0UL, AirPlayDeviceSubtype.AppleTv)]
    [InlineData("AudioAccessory5,1", 0UL, AirPlayDeviceSubtype.HomePod)]
    // Model wins over feature bits: a HomePod with unified-advertiser info is still a HomePod.
    [InlineData("AudioAccessory1,1", 1UL << 30, AirPlayDeviceSubtype.HomePod)]
    [InlineData(null, 1UL << 30, AirPlayDeviceSubtype.ThirdPartySpeaker)]
    [InlineData("Sonos One", 1UL << 51, AirPlayDeviceSubtype.ThirdPartySpeaker)]
    [InlineData(null, 0UL, AirPlayDeviceSubtype.Unknown)]
    public void Subtype_Follows_Spec_Rule(string? model, ulong features, AirPlayDeviceSubtype expected)
    {
        var d = new AirPlayDevice { DeviceId = "AABBCCDDEEFF", Name = "t", Model = model, RawFeatures = features };
        Assert.Equal(expected, d.Subtype);
    }

    [Fact]
    public void HomePod_Is_Never_A_Mirroring_Candidate()
    {
        // Even if a HomePod advertised the screen bit, AudioAccessory* must never mirror.
        var homePod = new AirPlayDevice
        {
            DeviceId = "A", Name = "HomePod", Model = "AudioAccessory5,1",
            RawFeatures = (ulong)AirPlayFeatures.SupportsAirPlayScreen,
        };
        Assert.False(homePod.IsMirroringCandidate);

        var appleTv = new AirPlayDevice
        {
            DeviceId = "B", Name = "TV", Model = "AppleTV14,1",
            RawFeatures = (ulong)AirPlayFeatures.SupportsAirPlayScreen,
        };
        Assert.True(appleTv.IsMirroringCandidate);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff", "AABBCCDDEEFF")]
    [InlineData("AA-BB-CC-DD-EE-FF", "AABBCCDDEEFF")]
    [InlineData(" aabbccddeeff ", "AABBCCDDEEFF")]
    public void NormalizeDeviceId_Strips_Separators(string raw, string expected)
    {
        Assert.Equal(expected, AirPlayDevice.NormalizeDeviceId(raw));
    }
}
