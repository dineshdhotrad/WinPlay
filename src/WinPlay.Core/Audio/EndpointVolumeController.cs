// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;

namespace WinPlay.Core.Audio;

/// <summary>
/// Abstraction over a render endpoint's mute and master volume. Injecting this lets the crash-safe
/// mute logic (<see cref="StreamStateModel"/> / <see cref="AudioStateGuardian"/>) be unit-tested
/// deterministically, without touching real audio hardware.
///
/// <para>Every operation names the endpoint it acts on. Resolving "the current default" afresh on
/// each call looked equivalent and is not: the default render device changes underneath a running
/// stream all the time — Bluetooth headphones connect, a dock takes over, an HDMI display becomes
/// the audio target. When that happened while WinPlay had the speakers silenced, the restore
/// unmuted whatever had since become default (writing the OLD device's saved volume onto it) and
/// never touched the device it had actually muted. Those speakers stayed muted indefinitely, and
/// no crash-recovery path covered it, because from the app's point of view nothing had gone
/// wrong.</para>
/// </summary>
public interface IEndpointVolumeController
{
    /// <summary>Stable ID of the current default multimedia render endpoint, or null if there is none.</summary>
    string? DefaultRenderDeviceId { get; }

    /// <summary>
    /// Reads mute state and master volume (0..1) from a specific endpoint, or from the current
    /// default when <paramref name="deviceId"/> is null. Returns false if it cannot be read.
    /// </summary>
    bool TryGetState(string? deviceId, out bool mute, out float volume);

    /// <summary>Sets mute on a specific endpoint. Returns false if it could not be applied.</summary>
    bool TrySetMute(string? deviceId, bool mute);

    /// <summary>Sets master volume (0..1, clamped) on a specific endpoint.</summary>
    bool TrySetVolume(string? deviceId, float volume);

    /// <summary>
    /// Something OUTSIDE WinPlay changed the watched endpoint: (muted, volume 0..1).
    ///
    /// <para>This exists because muting an endpoint is not a state you can set once and assume.
    /// Windows clears mute whenever the user presses volume-up — by design — so the PC's speakers
    /// came back mid-stream and played the same audio as the AirPlay speaker, a second behind it.
    /// Owning the mute means noticing when the system takes it away.</para>
    /// </summary>
    event Action<bool, float>? ExternalChange;

    /// <summary>
    /// Starts reporting <see cref="ExternalChange"/> for one endpoint; null stops watching.
    /// Changes WinPlay itself makes are not reported — only what someone else did.
    /// </summary>
    void Watch(string? deviceId);
}

/// <summary>Controls real render endpoints via WASAPI (NAudio).</summary>
public sealed class WasapiEndpointController : IEndpointVolumeController, IDisposable
{
    public string? DefaultRenderDeviceId
    {
        get
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                return device.ID;
            }
            catch (Exception)
            {
                return null;   // no render endpoint at all — nothing to silence or restore
            }
        }
    }

    public bool TryGetState(string? deviceId, out bool mute, out float volume)
    {
        bool m = false;
        float v = 1f;
        bool ok = Apply(deviceId, d =>
        {
            m = d.AudioEndpointVolume.Mute;
            v = d.AudioEndpointVolume.MasterVolumeLevelScalar;
        });
        mute = m;
        volume = v;
        return ok;
    }

    public bool TrySetMute(string? deviceId, bool mute)
    {
        // Suppress the notification our own write causes, or defending the mute would re-trigger
        // itself forever.
        Interlocked.Increment(ref _selfWrites);
        try { return Apply(deviceId, d => d.AudioEndpointVolume.Mute = mute); }
        finally { Interlocked.Decrement(ref _selfWrites); }
    }

    public bool TrySetVolume(string? deviceId, float volume)
    {
        Interlocked.Increment(ref _selfWrites);
        try { return Apply(deviceId, d => d.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(volume, 0f, 1f)); }
        finally { Interlocked.Decrement(ref _selfWrites); }
    }

    public event Action<bool, float>? ExternalChange;

    private MMDeviceEnumerator? _watchEnumerator;
    private MMDevice? _watched;
    private int _selfWrites;

    /// <summary>
    /// Holds the device open and listens for endpoint changes. A persistent MMDevice is required:
    /// the notification is raised on the device object, so the per-call open/close used everywhere
    /// else here cannot deliver it.
    /// </summary>
    public void Watch(string? deviceId)
    {
        StopWatching();
        if (deviceId is null) return;
        try
        {
            _watchEnumerator = new MMDeviceEnumerator();
            _watched = _watchEnumerator.GetDevice(deviceId);
            _watched.AudioEndpointVolume.OnVolumeNotification += OnEndpointNotification;
        }
        catch (Exception)
        {
            StopWatching();   // the device went away; nothing to watch
        }
    }

    private void OnEndpointNotification(AudioVolumeNotificationData data)
    {
        if (Volatile.Read(ref _selfWrites) > 0) return;   // our own write, not the user's
        ExternalChange?.Invoke(data.Muted, data.MasterVolume);
    }

    private void StopWatching()
    {
        try
        {
            if (_watched is not null)
                _watched.AudioEndpointVolume.OnVolumeNotification -= OnEndpointNotification;
        }
        catch (Exception) { /* device already gone */ }
        _watched?.Dispose();
        _watched = null;
        _watchEnumerator?.Dispose();
        _watchEnumerator = null;
    }

    public void Dispose() => StopWatching();

    /// <summary>
    /// Runs an action against a named endpoint, or the current default when unnamed. A device that
    /// no longer exists — unplugged headphones, a disconnected dock — reports failure rather than
    /// silently retargeting whatever is default now, which would apply one device's settings to
    /// another.
    /// </summary>
    private static bool Apply(string? deviceId, Action<MMDevice> action)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            action(device);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
