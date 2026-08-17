// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// The single source of truth for whether the local speakers are silenced. The
/// endpoint is muted if — and only if — at least one destination is actively capturing system
/// audio AND the user wants local silence while streaming:
///
/// <code>IsMuted == (AnyActive &amp;&amp; LocalSilenceRequested)</code>
///
/// No other code path may mute the endpoint, so the system can never be left muted when nothing
/// is streaming. Active destinations are tracked as a set keyed by destination id, so repeated
/// start/stop of the same destination cannot unbalance the state. Every derived change is pushed
/// to the crash-safe <see cref="AudioStateGuardian"/>.
/// </summary>
public sealed class StreamStateModel
{
    private readonly AudioStateGuardian _guardian;
    private readonly object _lock = new();
    private readonly HashSet<string> _active = new(StringComparer.Ordinal);
    private bool _localSilenceRequested = true;

    public StreamStateModel(AudioStateGuardian guardian) => _guardian = guardian;

    /// <summary>Whether the user wants local speakers silenced while streaming (default true).</summary>
    public bool LocalSilenceRequested
    {
        get { lock (_lock) return _localSilenceRequested; }
        set { lock (_lock) { _localSilenceRequested = value; Apply(); } }
    }

    /// <summary>True while at least one destination is actively capturing system audio.</summary>
    public bool AnyActive { get { lock (_lock) return _active.Count > 0; } }

    /// <summary>The derived mute decision — the ONE place the endpoint mute is defined.</summary>
    public bool IsMuted { get { lock (_lock) return Derived(); } }

    private bool Derived() => _active.Count > 0 && _localSilenceRequested;

    /// <summary>Marks a destination as actively capturing system audio (or not). Idempotent per key.</summary>
    public void SetActive(string destinationKey, bool active)
    {
        lock (_lock)
        {
            if (active) _active.Add(destinationKey);
            else _active.Remove(destinationKey);
            Apply();
        }
    }

    /// <summary>Clears all active destinations (e.g. on shutdown) → guaranteed unmute.</summary>
    public void Reset()
    {
        lock (_lock) { _active.Clear(); Apply(); }
    }

    private void Apply() => _guardian.SetLocalSilence(Derived());
}
