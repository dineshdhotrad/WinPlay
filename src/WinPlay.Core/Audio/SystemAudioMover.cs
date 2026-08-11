// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// Moves the PC's audio to AirPlay the way a Mac does: while a destination is actively capturing
/// the system mix, the local speakers are silenced (so only the receiver plays — no ~2 s echo).
///
/// <para>The mute is now strictly <em>derived</em> from active reception (<see cref="StreamStateModel"/>)
/// and crash-safe (<see cref="AudioStateGuardian"/>): it is never left applied when nothing is
/// streaming, and it is restored on stop, on process exit, on crash (via the persisted state),
/// and on the next launch. This replaces the old unconditional mute that could leave the system
/// muted after a crash (§3 Problems 2 &amp; 4).</para>
///
/// <para>Capture uses process-loopback (which taps before the endpoint, so it survives the mute).
/// If that is unavailable, it falls back to endpoint loopback with local silence disabled —
/// endpoint loopback captures <em>after</em> the mute, so muting would record silence.</para>
/// </summary>
public sealed class SystemAudioMover : IDisposable
{
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private readonly StreamStateModel _state;
    private readonly AudioStateGuardian _guardian;
    private bool _processLoopbackSupported = true;

    public SystemAudioMover(Action<string>? log = null)
        : this(new AudioStateGuardian(new WasapiEndpointController(), log: log)) { }

    /// <summary>Test seam: inject a guardian built over a fake endpoint.</summary>
    public SystemAudioMover(AudioStateGuardian guardian)
    {
        _guardian = guardian;
        _state = new StreamStateModel(_guardian);
        // Recover audio left muted by a previous session that died while streaming (B4).
        _guardian.RestorePersistedIfPresent();
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <summary>Whether to silence local speakers while streaming (default true).</summary>
    public bool LocalSilenceWhileStreaming
    {
        get => _state.LocalSilenceRequested;
        set => _state.LocalSilenceRequested = value;
    }

    /// <summary>Marks a destination as actively capturing system audio → silences local speakers.</summary>
    public void EnterStreaming(string destinationKey)
    {
        if (_processLoopbackSupported) _state.SetActive(destinationKey, true);
    }

    /// <summary>Marks a destination as no longer capturing → unmutes once none remain.</summary>
    public void ExitStreaming(string destinationKey) => _state.SetActive(destinationKey, false);

    /// <summary>
    /// Creates an audio source for a streaming session. Prefers process-loopback capture
    /// (excludes WinPlay itself, survives the endpoint mute); falls back to endpoint loopback if
    /// process loopback can't start, disabling local silence for this run so the capture is not
    /// itself muted.
    /// </summary>
    public IAudioSource CreateCaptureSource()
    {
        if (_processLoopbackSupported)
        {
            try
            {
                return new ProcessLoopbackAudioSource(_ownPid);
            }
            catch (Exception)
            {
                _processLoopbackSupported = false;
                _state.LocalSilenceRequested = false; // endpoint loopback records post-mute; don't mute
                _state.Reset();
            }
        }
        return new LoopbackAudioSource();
    }

    private void OnProcessExit(object? sender, EventArgs e) => _guardian.OnProcessExit();

    public void Dispose()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _state.Reset();          // unmute — nothing is streaming anymore
        _guardian.OnProcessExit();
    }
}
