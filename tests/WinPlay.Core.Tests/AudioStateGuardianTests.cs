// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;
using Xunit;

namespace WinPlay.Core.Tests;

/// <summary>
/// Verifies the crash-safe endpoint restore (Task B4): the original mute/volume is persisted
/// before silencing, restored on release and on exit, and — the flagship guarantee —
/// recovered on the NEXT launch if the previous session died while muted.
/// </summary>
public sealed class AudioStateGuardianTests : IDisposable
{
    private readonly string _statePath = Path.Combine(
        Path.GetTempPath(), "winplay-guardian-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { /* ignore */ }
    }

    [Fact]
    public void Silence_Then_Restore_Returns_The_Exact_Original_State()
    {
        var endpoint = new FakeEndpointVolumeController { Mute = false, Volume = 0.7f };
        var guardian = new AudioStateGuardian(endpoint, _statePath);

        guardian.SetLocalSilence(true);
        Assert.True(endpoint.Mute);
        Assert.True(File.Exists(_statePath)); // original snapshot persisted

        guardian.SetLocalSilence(false);
        Assert.False(endpoint.Mute);            // original mute restored
        Assert.Equal(0.7f, endpoint.Volume, 3); // original volume intact
        Assert.False(File.Exists(_statePath));  // snapshot cleared on clean restore
    }

    [Fact]
    public void Restore_Honours_A_Preexisting_Mute()
    {
        // If the user already had the endpoint muted, restoring must leave it muted.
        var endpoint = new FakeEndpointVolumeController { Mute = true, Volume = 0.5f };
        var guardian = new AudioStateGuardian(endpoint, _statePath);

        guardian.SetLocalSilence(true);
        guardian.SetLocalSilence(false);
        Assert.True(endpoint.Mute);
    }

    [Fact]
    public void SetLocalSilence_Is_Idempotent()
    {
        var endpoint = new FakeEndpointVolumeController { Mute = false, Volume = 0.9f };
        var guardian = new AudioStateGuardian(endpoint, _statePath);

        guardian.SetLocalSilence(true);
        guardian.SetLocalSilence(true); // no-op; must not overwrite the saved original
        guardian.SetLocalSilence(false);
        Assert.False(endpoint.Mute);
        Assert.Equal(0.9f, endpoint.Volume, 3);
    }

    [Fact]
    public void A_Crash_While_Muted_Is_Recovered_On_Next_Launch()
    {
        var endpoint = new FakeEndpointVolumeController { Mute = false, Volume = 0.6f };

        // Session 1 silences, then "crashes": the process dies without calling restore, so the
        // endpoint is left muted and the state file survives.
        var crashed = new AudioStateGuardian(endpoint, _statePath);
        crashed.SetLocalSilence(true);
        Assert.True(endpoint.Mute);
        Assert.True(File.Exists(_statePath));

        // Session 2 (next launch) over the same endpoint recovers the original on startup.
        var relaunched = new AudioStateGuardian(endpoint, _statePath);
        bool recovered = relaunched.RestorePersistedIfPresent();

        Assert.True(recovered);
        Assert.False(endpoint.Mute);            // system audio restored — not left muted
        Assert.Equal(0.6f, endpoint.Volume, 3);
        Assert.False(File.Exists(_statePath));  // marker cleared
    }

    [Fact]
    public void Clean_Previous_Session_Leaves_Nothing_To_Recover()
    {
        var endpoint = new FakeEndpointVolumeController();
        var guardian = new AudioStateGuardian(endpoint, _statePath);
        Assert.False(guardian.RestorePersistedIfPresent());
    }

    [Fact]
    public void OnProcessExit_Restores_When_Silenced()
    {
        var endpoint = new FakeEndpointVolumeController { Mute = false, Volume = 0.8f };
        var guardian = new AudioStateGuardian(endpoint, _statePath);

        guardian.SetLocalSilence(true);
        guardian.OnProcessExit();

        Assert.False(endpoint.Mute);
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void No_Endpoint_Available_Is_A_Safe_No_Op()
    {
        var endpoint = new FakeEndpointVolumeController { Available = false };
        var guardian = new AudioStateGuardian(endpoint, _statePath);

        guardian.SetLocalSilence(true);
        Assert.False(guardian.IsSilenced);      // could not read the endpoint → did not silence
        Assert.False(File.Exists(_statePath));
    }

    [Fact]
    public void Restore_Unmutes_The_Device_It_Muted_Even_If_The_Default_Changed()
    {
        // The real sequence: streaming starts and WinPlay silences the speakers; Bluetooth
        // headphones connect and Windows makes them the default; streaming stops. Restoring
        // "the current default" unmuted the HEADPHONES — writing the speakers' saved volume onto
        // them — and left the speakers muted with nothing playing. Nothing reported it, because
        // as far as the app knew it had restored what it changed.
        var endpoint = new FakeEndpointVolumeController();
        endpoint.AddDevice("speakers", mute: false, volume: 0.7f);
        endpoint.AddDevice("headphones", mute: false, volume: 0.3f);
        endpoint.DefaultRenderDeviceId = "speakers";

        string path = Path.Combine(Path.GetTempPath(), $"winplay-endpoint-{Guid.NewGuid():N}.json");
        try
        {
            var guardian = new AudioStateGuardian(endpoint, path);
            guardian.SetLocalSilence(true);
            Assert.True(endpoint.MuteOf("speakers"));

            endpoint.DefaultRenderDeviceId = "headphones";   // headphones connect mid-stream
            guardian.SetLocalSilence(false);

            Assert.False(endpoint.MuteOf("speakers"));       // the device we muted is released
            Assert.Equal(0.7f, endpoint.VolumeOf("speakers"), 3);
            Assert.False(endpoint.MuteOf("headphones"));     // and the newcomer is untouched
            Assert.Equal(0.3f, endpoint.VolumeOf("headphones"), 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Crash_Recovery_Restores_The_Device_That_Was_Muted()
    {
        // Same guarantee across a process death: the state file has to name the endpoint, or the
        // next launch "recovers" a device that was never touched and leaves the muted one muted.
        var endpoint = new FakeEndpointVolumeController();
        endpoint.AddDevice("speakers", mute: false, volume: 0.6f);
        endpoint.AddDevice("hdmi", mute: false, volume: 1f);
        endpoint.DefaultRenderDeviceId = "speakers";

        string path = Path.Combine(Path.GetTempPath(), $"winplay-endpoint-{Guid.NewGuid():N}.json");
        try
        {
            new AudioStateGuardian(endpoint, path).SetLocalSilence(true);   // dies while silenced
            Assert.True(endpoint.MuteOf("speakers"));

            endpoint.DefaultRenderDeviceId = "hdmi";                        // display took over
            Assert.True(new AudioStateGuardian(endpoint, path).RestorePersistedIfPresent());

            Assert.False(endpoint.MuteOf("speakers"));
            Assert.Equal(0.6f, endpoint.VolumeOf("speakers"), 3);
            Assert.Equal(1f, endpoint.VolumeOf("hdmi"), 3);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
