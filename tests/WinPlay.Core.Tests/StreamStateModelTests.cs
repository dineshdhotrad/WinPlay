// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies the single-source-of-truth mute invariant (Task B3):
/// <c>IsMuted == (AnyActive &amp;&amp; LocalSilenceRequested)</c>, and — the bug this fixes — that the
/// endpoint is NEVER left muted once no destination is active.
/// </summary>
public class StreamStateModelTests
{
    private static (StreamStateModel Model, FakeEndpointVolumeController Endpoint) NewModel()
    {
        var endpoint = new FakeEndpointVolumeController();
        var guardian = new AudioStateGuardian(endpoint, statePath: Path.Combine(
            Path.GetTempPath(), "winplay-ssm-" + Guid.NewGuid().ToString("N") + ".json"));
        return (new StreamStateModel(guardian), endpoint);
    }

    [Fact]
    public void No_Active_Destination_Is_Never_Muted()
    {
        var (model, endpoint) = NewModel();
        Assert.False(model.IsMuted);
        Assert.False(endpoint.Mute);

        model.LocalSilenceRequested = true; // wanting silence with nothing active must NOT mute
        Assert.False(model.IsMuted);
        Assert.False(endpoint.Mute);
    }

    [Fact]
    public void Active_Plus_Silence_Requested_Mutes_The_Endpoint()
    {
        var (model, endpoint) = NewModel();
        model.SetActive("living-room", true);
        Assert.True(model.IsMuted);
        Assert.True(endpoint.Mute);
    }

    [Fact]
    public void Active_But_Silence_Not_Requested_Does_Not_Mute()
    {
        var (model, endpoint) = NewModel();
        model.LocalSilenceRequested = false;
        model.SetActive("living-room", true);
        Assert.False(model.IsMuted);
        Assert.False(endpoint.Mute);
    }

    [Fact]
    public void Endpoint_Unmutes_When_The_Last_Destination_Stops()
    {
        var (model, endpoint) = NewModel();
        model.SetActive("a", true);
        model.SetActive("b", true);
        Assert.True(endpoint.Mute);

        model.SetActive("a", false);
        Assert.True(endpoint.Mute);   // b still active

        model.SetActive("b", false);
        Assert.False(endpoint.Mute);  // none active → restored
    }

    [Fact]
    public void Repeated_Activation_Of_The_Same_Key_Is_Balanced()
    {
        var (model, endpoint) = NewModel();
        model.SetActive("a", true);
        model.SetActive("a", true); // idempotent — not a second reference
        model.SetActive("a", false);
        Assert.False(model.AnyActive);
        Assert.False(endpoint.Mute);
    }

    [Fact]
    public void Reset_Always_Unmutes()
    {
        var (model, endpoint) = NewModel();
        model.SetActive("a", true);
        model.SetActive("b", true);
        model.Reset();
        Assert.False(model.AnyActive);
        Assert.False(endpoint.Mute);
    }

    [Fact]
    public void Invariant_Holds_Across_A_Random_Op_Sequence()
    {
        var (model, endpoint) = NewModel();
        var rng = new Random(20260807); // fixed seed → reproducible
        var active = new HashSet<string>();
        bool silence = true;
        string[] keys = ["hp-left", "hp-right", "atv", "kitchen", "study"];

        for (int i = 0; i < 5000; i++)
        {
            switch (rng.Next(3))
            {
                case 0:
                    string k = keys[rng.Next(keys.Length)];
                    bool on = rng.Next(2) == 0;
                    model.SetActive(k, on);
                    if (on) active.Add(k); else active.Remove(k);
                    break;
                case 1:
                    silence = rng.Next(2) == 0;
                    model.LocalSilenceRequested = silence;
                    break;
                default:
                    model.Reset();
                    active.Clear();
                    break;
            }

            bool expected = active.Count > 0 && silence;
            Assert.Equal(expected, model.IsMuted);
            Assert.Equal(expected, endpoint.Mute); // the endpoint tracks the derived state exactly
        }

        // And the crucial post-condition: with nothing active, the endpoint is not muted.
        model.Reset();
        Assert.False(endpoint.Mute);
    }
}
