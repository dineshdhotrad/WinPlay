// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinPlay.Core.Audio;

/// <summary>Persisted snapshot of the endpoint state WinPlay changed, so it can be restored
/// even if the process dies while the local speakers are silenced.</summary>
/// <remarks>
/// <c>DeviceId</c> is nullable so a state file written by an older build still deserialises; it
/// then restores the current default, which is the old behaviour and the best that can be done
/// without knowing what was actually muted.
/// </remarks>
public sealed record PersistedAudioState(bool Mute, float Volume, int Pid, string SavedUtc, string? DeviceId = null);

// Source-generated JSON so the persistence path stays trim/AOT-safe (roadmap-friendly).
[JsonSerializable(typeof(PersistedAudioState))]
internal sealed partial class AudioStateJsonContext : JsonSerializerContext;

/// <summary>
/// Crash-safe custodian of the local endpoint's mute/volume.
///
/// <para>When WinPlay silences the local speakers while streaming, the guardian first records
/// the endpoint's original mute + volume to <c>%LOCALAPPDATA%\WinPlay\audio-state.json</c>, then
/// mutes. It restores the original on release, on process exit, and — crucially — on the NEXT
/// launch if the previous session died while silenced: the file's presence at startup means
/// "a session muted me and never restored, put me back". This is what guarantees the system is
/// never left muted after a crash or <c>taskkill</c> — the exact failure the old mover had.</para>
///
/// <para>All methods are exception-safe and lock-guarded; a diagnostics callback records every
/// transition.</para>
/// </summary>
public sealed class AudioStateGuardian
{
    private readonly IEndpointVolumeController _endpoint;
    private readonly string _statePath;
    private readonly Action<string>? _log;
    private readonly object _lock = new();
    private bool _silenced;
    private (bool Mute, float Volume, string? DeviceId)? _original;

    /// <summary>
    /// The user changed the system volume while WinPlay had the local speakers silenced (0..1).
    ///
    /// <para>Their volume keys have to keep meaning something. With local output muted, the only
    /// thing left worth changing is the AirPlay destination's volume — which is exactly what a Mac
    /// does when it is playing to an AirPlay speaker. Without this the keys did nothing useful and,
    /// worse, unmuted the PC so both it and the speaker played at once.</para>
    /// </summary>
    public event Action<float>? SystemVolumeChanged;

    public AudioStateGuardian(IEndpointVolumeController endpoint, string? statePath = null, Action<string>? log = null)
    {
        _endpoint = endpoint;
        _endpoint.ExternalChange += OnExternalChange;
        _statePath = statePath ?? DefaultStatePath;
        _log = log;
    }

    public static string DefaultStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPlay", "audio-state.json");

    public bool IsSilenced { get { lock (_lock) return _silenced; } }

    /// <summary>Applies or lifts local silence. Idempotent.</summary>
    public void SetLocalSilence(bool silence)
    {
        lock (_lock)
        {
            if (silence == _silenced) return;
            if (silence) Apply();
            else Restore();
        }
    }

    /// <summary>
    /// Something outside WinPlay touched the endpoint while we had it silenced.
    ///
    /// <para>Windows CLEARS MUTE whenever the user presses volume-up — that is deliberate OS
    /// behaviour, not a glitch — so a mute set once and forgotten does not survive the first
    /// keypress. The PC's speakers then played the same audio as the AirPlay speaker, roughly a
    /// second apart. Holding the mute is the only way it stays true, and the volume they were
    /// reaching for is forwarded to the receiver so the keys still do what they look like they do.</para>
    /// </summary>
    private void OnExternalChange(bool muted, float volume)
    {
        bool userAction = false;
        lock (_lock)
        {
            if (!_silenced || _original is not { } o) return;
            // While WE hold the mute, the one notification that can represent genuine user intent
            // is an UNMUTE — volume keys and the system tray unmute before they act, and nothing
            // else unmutes an endpoint we just silenced. A notification with muted=true is an echo
            // of our own management: the original silencing write looping back off the audio
            // engine's thread (its callback can outrun the same-thread self-write guard), or the
            // re-mute below reporting itself. Forwarding those echoes treated our own mute as a
            // user volume command and pushed it to EVERY receiver — the receivers were literally
            // told to fall silent seconds into a healthy session, while every pipeline instrument
            // correctly read clean. Only the user's hand fans out.
            if (!muted)
            {
                userAction = true;
                _endpoint.TrySetMute(o.DeviceId, true);
                _log?.Invoke($"system unmuted the local endpoint while streaming — re-silenced (vol {volume:P0})");
            }
        }
        if (userAction) SystemVolumeChanged?.Invoke(volume);
    }

    private void Apply()
    {
        // Pin the endpoint at the moment of muting and hold on to its ID. Everything from here on
        // — the restore, the exit hook, the next launch's recovery — has to act on THIS device,
        // not on whatever happens to be default by then. Headphones connecting mid-stream is
        // enough to make those two different.
        string? deviceId = _endpoint.DefaultRenderDeviceId;
        if (!_endpoint.TryGetState(deviceId, out bool mute, out float volume)) return; // nothing to silence
        _original = (mute, volume, deviceId);
        Persist(new PersistedAudioState(mute, volume, Environment.ProcessId, DateTime.UtcNow.ToString("O"), deviceId));

        // Only claim to have silenced the endpoint if the mute actually took. Recording success
        // regardless meant a failed mute still set _silenced and still wrote the recovery file:
        // the local speakers kept playing alongside the AirPlay stream — the echo this exists to
        // prevent — and on release WinPlay would "restore" a device it never changed.
        if (!_endpoint.TrySetMute(deviceId, true))
        {
            _original = null;
            DeleteState();
            _log?.Invoke($"could not silence local speakers (device={deviceId ?? "default"}); leaving them alone");
            return;
        }
        _silenced = true;
        _endpoint.Watch(deviceId);
        _log?.Invoke($"local speakers silenced (device={deviceId ?? "default"}, saved original mute={mute}, vol={volume:F2})");
    }

    private void Restore()
    {
        if (_original is { } o)
        {
            bool restored = _endpoint.TrySetMute(o.DeviceId, o.Mute);
            _endpoint.TrySetVolume(o.DeviceId, o.Volume);
            if (!restored)
            {
                // The device is unreachable at this instant — unplugged dock, a Bluetooth speaker
                // that has not reconnected, a driver re-enumerating around resume. It is still
                // muted, and this is the ONLY record of that. Deleting it here is what turned a
                // recoverable state into a permanently muted device with no trace: keep the file
                // and stay "silenced", so the exit hook and the next launch both try again.
                _log?.Invoke($"could not restore local speakers (device={o.DeviceId ?? "default"}); "
                             + "keeping the recovery record so a later attempt can");
                return;
            }
            _log?.Invoke($"local speakers restored (device={o.DeviceId ?? "default"}, mute={o.Mute}, vol={o.Volume:F2})");
        }
        _endpoint.Watch(null);
        _original = null;
        _silenced = false;
        DeleteState();
    }

    /// <summary>Restore-on-exit hook (register with <c>ProcessExit</c>). Safe to call repeatedly.</summary>
    public void OnProcessExit()
    {
        lock (_lock) { if (_silenced) Restore(); }
    }

    /// <summary>
    /// Called once at startup. If a state file survived from a previous session — which therefore
    /// died while silenced — restore the endpoint to the recorded original and clear the file.
    /// Returns true if a recovery was performed.
    /// </summary>
    public bool RestorePersistedIfPresent()
    {
        lock (_lock)
        {
            if (ReadState() is not { } s) return false;
            if (!_endpoint.TrySetMute(s.DeviceId, s.Mute))
            {
                // Same reasoning as Restore: the record is the only evidence the device was left
                // muted, so it survives until a run actually succeeds in putting it back.
                _log?.Invoke("a previous session left the speakers muted, but that device is not "
                             + "reachable yet — keeping the recovery record for the next attempt");
                return false;
            }
            _endpoint.TrySetVolume(s.DeviceId, s.Volume);
            _log?.Invoke($"recovered audio from a previous session that exited while muted (mute={s.Mute}, vol={s.Volume:F2})");
            DeleteState();
            return true;
        }
    }

    private void Persist(PersistedAudioState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(state, AudioStateJsonContext.Default.PersistedAudioState));
        }
        catch (Exception) { /* best effort — never throw into the streaming path */ }
    }

    private PersistedAudioState? ReadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(_statePath), AudioStateJsonContext.Default.PersistedAudioState);
        }
        catch (Exception) { return null; }
    }

    private void DeleteState()
    {
        try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch (Exception) { }
    }
}
