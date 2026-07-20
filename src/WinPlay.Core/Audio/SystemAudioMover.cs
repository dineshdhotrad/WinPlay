// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace WinPlay.Core.Audio;

/// <summary>
/// Moves the PC's audio to AirPlay the way a Mac does: while streaming, it mutes the local
/// output endpoint (so the speakers are silent and there is no ~2 s echo against the
/// receiver) and captures the system mix via <see cref="ProcessLoopbackCapture"/>, which
/// keeps working even though the endpoint is muted. On the last destination stopping, the
/// speakers are restored to their previous state.
///
/// If process-loopback capture is unavailable (older Windows), it falls back to ordinary
/// endpoint loopback and does <em>not</em> mute — audio still streams, just without the
/// local‑mute behaviour.
/// </summary>
public sealed class SystemAudioMover : IDisposable
{
    private readonly object _lock = new();
    private readonly uint _ownPid = (uint)Environment.ProcessId;
    private bool _processLoopbackSupported = true;
    private bool _muting;
    private bool _priorMute;

    /// <summary>Mutes the local output endpoint (idempotent) so only the receiver is audible.</summary>
    public void EnterStreaming()
    {
        lock (_lock)
        {
            if (_muting || !_processLoopbackSupported) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _priorMute = device.AudioEndpointVolume.Mute;
                device.AudioEndpointVolume.Mute = true;
                _muting = true;
            }
            catch (Exception)
            {
                // No default endpoint or no permission — carry on without muting.
            }
        }
    }

    /// <summary>Restores the local endpoint's prior mute state.</summary>
    public void ExitStreaming()
    {
        lock (_lock)
        {
            if (!_muting) return;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                device.AudioEndpointVolume.Mute = _priorMute;
            }
            catch (Exception) { /* endpoint gone; nothing to restore */ }
            _muting = false;
        }
    }

    /// <summary>
    /// Creates an audio source for a streaming session. Prefers process-loopback capture
    /// (excludes WinPlay itself, survives the endpoint mute); falls back to endpoint
    /// loopback if process loopback can't start, disabling the local mute for this run.
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
                // Older Windows or blocked: stop muting and use endpoint loopback instead.
                lock (_lock) { _processLoopbackSupported = false; }
                ExitStreaming();
            }
        }
        return new LoopbackAudioSource();
    }

    public void Dispose() => ExitStreaming();
}
