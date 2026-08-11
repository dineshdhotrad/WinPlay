// SPDX-License-Identifier: GPL-3.0-or-later
using WinPlay.Core.Audio;

namespace WinPlay.Core.Tests;

/// <summary>
/// In-memory <see cref="IEndpointVolumeController"/> for deterministic audio-state tests.
///
/// <para>Models SEVERAL endpoints with a switchable default, because a machine has several and the
/// default moves between them while WinPlay is running — headphones connect, a dock takes over. A
/// fake with only one device cannot express the case where WinPlay mutes one device and the
/// default becomes another, which is precisely the case that used to leave a user's speakers muted
/// with nothing playing.</para>
/// </summary>
internal sealed class FakeEndpointVolumeController : IEndpointVolumeController
{
    private sealed class Endpoint
    {
        public bool Mute;
        public float Volume = 1f;
    }

    private readonly Dictionary<string, Endpoint> _devices = new()
    {
        ["speakers"] = new Endpoint(),
    };

    /// <summary>Which endpoint is currently the default; set it to simulate a device switch.</summary>
    public string? DefaultRenderDeviceId { get; set; } = "speakers";

    /// <summary>Set false to simulate a machine with no usable render endpoint.</summary>
    public bool Available = true;

    /// <summary>Convenience accessors for the single-device tests.</summary>
    public bool Mute
    {
        get => MuteOf("speakers");
        set => _devices["speakers"].Mute = value;
    }

    public float Volume
    {
        get => VolumeOf("speakers");
        set => _devices["speakers"].Volume = value;
    }

    public void AddDevice(string id, bool mute = false, float volume = 1f) =>
        _devices[id] = new Endpoint { Mute = mute, Volume = volume };

    public bool MuteOf(string id) => _devices[id].Mute;
    public float VolumeOf(string id) => _devices[id].Volume;

    public bool TryGetState(string? deviceId, out bool mute, out float volume)
    {
        mute = false;
        volume = 1f;
        if (!TryResolve(deviceId, out var endpoint)) return false;
        mute = endpoint.Mute;
        volume = endpoint.Volume;
        return true;
    }

    public bool TrySetMute(string? deviceId, bool mute)
    {
        if (!TryResolve(deviceId, out var endpoint)) return false;
        endpoint.Mute = mute;
        return true;
    }

    public bool TrySetVolume(string? deviceId, float volume)
    {
        if (!TryResolve(deviceId, out var endpoint)) return false;
        endpoint.Volume = Math.Clamp(volume, 0f, 1f);
        return true;
    }

    private bool TryResolve(string? deviceId, out Endpoint endpoint)
    {
        endpoint = null!;
        if (!Available) return false;
        string? id = deviceId ?? DefaultRenderDeviceId;
        return id is not null && _devices.TryGetValue(id, out endpoint!);
    }
}
