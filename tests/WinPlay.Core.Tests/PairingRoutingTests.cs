// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Discovery;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Regression tests for two real user reports: connecting a HomePod mini failed instantly with
/// "Couldn't connect - try again", after briefly asking for a PIN.
///
/// <para>Cause: RTSP <c>401 Unauthorized</c> (the receiver's access control refused us) was
/// treated identically to <c>470 Connection Authorization Required</c> (an Apple TV asking for
/// an on-screen code). A HomePod has no screen and never shows a PIN, so the app started a
/// pairing flow the device cannot perform — which fails immediately and explains nothing.</para>
///
/// <para>These tests pin the routing decision: only receivers that can DISPLAY a code may be
/// sent to the PIN flow.</para>
/// </summary>
public class PairingRoutingTests
{
    private static AirPlayDevice Device(string model, AirPlayFeatures features = AirPlayFeatures.SupportsAirPlayAudio) =>
        new() { DeviceId = "AABBCCDDEEFF", Name = "Test", Model = model, RawFeatures = (ulong)features };

    [Theory]
    [InlineData("AudioAccessory5,1")]  // HomePod mini — the reported devices
    [InlineData("AudioAccessory1,1")]  // original HomePod
    [InlineData("AudioAccessory6,1")]  // HomePod 2nd gen
    public void A_HomePod_Is_Never_Sent_To_The_Pin_Flow(string model)
    {
        // A HomePod has no display. Asking it for an on-screen code can only fail.
        Assert.False(Device(model).CanDisplayPairingPin);
    }

    [Fact]
    public void A_HomePod_Advertising_Screen_Support_Is_Still_Not_Pin_Capable()
    {
        // Defensive: even if the feature bits claim screen support, an AudioAccessory has no
        // display. The model is the authority here.
        var homePod = Device("AudioAccessory5,1",
            AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsAirPlayScreen);
        Assert.False(homePod.CanDisplayPairingPin);
    }

    [Fact]
    public void An_Apple_Tv_Is_Pin_Capable()
        => Assert.True(Device("AppleTV11,1", AirPlayFeatures.SupportsAirPlayScreen).CanDisplayPairingPin);

    [Fact]
    public void A_Third_Party_Receiver_With_A_Screen_Is_Pin_Capable()
    {
        // A non-Apple receiver qualifies only by advertising video/screen support, which implies
        // it has somewhere to show the code.
        var tv = Device("SomeVendorTV1,1",
            AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.SupportsAirPlayScreen);
        Assert.True(tv.CanDisplayPairingPin);
    }

    [Fact]
    public void A_Screenless_Third_Party_Speaker_Is_Not_Pin_Capable()
    {
        // Shairport-style speakers: audio only, no display.
        var speaker = Device("Shairport Sync",
            AirPlayFeatures.SupportsAirPlayAudio | AirPlayFeatures.HasUnifiedAdvertiserInfo);
        Assert.False(speaker.CanDisplayPairingPin);
    }

    [Fact]
    public void Access_Denied_Names_The_Setting_The_User_Must_Change()
    {
        // The whole point of separating 401 from 470: the message has to tell the user what to
        // do. "Couldn't connect - try again" is what made these reports unactionable.
        var ex = new Hap.ReceiverAccessDeniedException("Bedroom");
        Assert.Contains("Bedroom", ex.Receiver);
        Assert.Contains("Home app", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Speaker Access", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Access_Denied_And_Pairing_Required_Are_Distinct_Types()
    {
        // They must never be catchable as one another: one starts a PIN flow, the other must not.
        Assert.IsNotType<Hap.PairingRequiredException>(new Hap.ReceiverAccessDeniedException("x"));
        Assert.IsNotType<Hap.ReceiverAccessDeniedException>(new Hap.PairingRequiredException("x"));
    }
}
