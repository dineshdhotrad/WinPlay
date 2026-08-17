// SPDX-License-Identifier: GPL-3.0-or-later
namespace WinPlay.Core.Audio;

/// <summary>
/// Pull source of interleaved stereo 16-bit PCM at 44.1 kHz. Implementations must fill
/// the whole buffer every call, zero-filling on underrun (silence), so the RTP pump can
/// run at a constant rate — AirPlay receivers drop the session after ~30 s without audio.
/// </summary>
public interface IAudioSource : IDisposable
{
    void Read(Span<short> interleavedStereo);
}

/// <summary>
/// A capture source that can drop any audio buffered so far and resume from the live edge. Called
/// just before streaming starts so the first samples sent are fresh — stale buffer accumulated
/// during connect is pure latency, so discarding it tightens the end-to-end delay without shrinking
/// the receiver's jitter buffer.
/// </summary>
public interface IFlushableAudioSource
{
    void FlushToLive();
}

/// <summary>
/// A source that is a BRANCH of a shared capture, taken partway through that capture's timeline —
/// see <see cref="BroadcastAudioSource"/>. <see cref="StartPositionFrames"/> is the absolute frame
/// position, on the shared capture's own frame counter, that this branch's very first sample
/// corresponds to. Zero for a branch taken at the moment the capture started (the ordinary
/// single/first-destination case); greater than zero for one taken later, while the capture is
/// already feeding another destination (a second room joining a multi-room stream, screen mirroring
/// alongside an audio-only room, …).
///
/// <para>This is what lets an RTP session stamp its frames correctly: every destination rendering
/// the same machine audio must play the SAME sample at the SAME instant, so a session whose branch
/// started at frame F must stamp its own frame n as absolute position F+n on the shared timeline —
/// not n. Getting this wrong (treating every branch as if it started at 0) is precisely the
/// multi-destination echo: a later destination would render the anchor's sample "0" as whatever it
/// happened to capture first, instead of the sample that was actually captured at the anchor
/// instant.</para>
/// </summary>
public interface IPositionedAudioSource
{
    long StartPositionFrames { get; }
}

/// <summary>
/// Capture-health counters for live attribution of audible problems: <c>UnderrunFrames</c> is
/// silence served in place of not-yet-arrived audio (benign while nothing renders),
/// <c>LateFrames</c> is real audio dropped because it arrived after its position played (actual
/// audible damage), and <c>GapJumps</c> counts true-silence writer jumps.
/// </summary>
public interface ICaptureDiagnostics
{
    (long UnderrunFrames, long LateFrames, long GapJumps) CaptureStats { get; }
}

/// <summary>
/// A capture that is not ready the instant it is constructed.
///
/// <para>WASAPI hands back a client immediately but delivers no samples until the audio engine
/// has started its first period, which takes tens of milliseconds. A pump that begins reading in
/// that window drains an empty ring and emits whatever it finds — the receiver renders the result
/// as harsh clicking until real audio catches up. It is only ever heard on the FIRST stream after
/// a capture is built, because every later one reuses a capture that is already running, which is
/// exactly the "first connection is horrible, second is clean" report this exists to fix.</para>
/// </summary>
public interface IPrimeableCapture
{
    /// <summary>
    /// Blocks until the capture has delivered its first samples, or the timeout expires.
    /// Returns false on timeout — the caller streams anyway rather than failing the connection,
    /// since a silent start is far better than no music.
    /// </summary>
    bool WaitUntilPrimed(TimeSpan timeout);
}

/// <summary>
/// A capture source that can report how far its newest sample actually lags "now" — the fixed
/// delay, in seconds, between a sound occurring at the device and that sample becoming available
/// to a reader of this source. Optional: a source implements this only when it genuinely knows the
/// number (e.g. it owns a device capture ring with a known jitter margin); one that cannot honestly
/// report it — a synthetic source, or a receiver with no visibility into the pipeline behind it —
/// simply does not implement the interface, and callers fall back to treating the newest sample as
/// captured "now" (today's behaviour).
///
/// <para>Wrappers that tee or ref-count a source (<see cref="BroadcastAudioSource"/>'s branches,
/// <see cref="SystemAudioMover"/>'s ref-counted handle) MUST forward this from whatever they wrap —
/// an implementation that silently drops it makes every caller downstream read zero latency, which
/// is indistinguishable from "not reported" but is in fact simply wrong.</para>
/// </summary>
public interface ICaptureLatency
{
    double CaptureLatencySeconds { get; }
}

/// <summary>Phase-continuous sine generator — protocol soak tests without touching WASAPI.</summary>
public sealed class SineAudioSource(double frequencyHz = 440.0, double amplitude = 0.25) : IAudioSource
{
    private double _phase;

    public void Read(Span<short> interleavedStereo)
    {
        double step = 2 * Math.PI * frequencyHz / 44100.0;
        for (int i = 0; i < interleavedStereo.Length; i += 2)
        {
            short s = (short)(Math.Sin(_phase) * amplitude * short.MaxValue);
            interleavedStereo[i] = s;
            interleavedStereo[i + 1] = s;
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// Stereo-pair verification source: alternates 4 s LEFT-only 440 Hz and 4 s RIGHT-only
/// 880 Hz (with a short silence between) so a listener can confirm channel placement.
/// </summary>
public sealed class ChannelTestAudioSource(double amplitude = 0.3) : IAudioSource
{
    private double _phase;
    private long _sample;

    public void Read(Span<short> interleavedStereo)
    {
        for (int i = 0; i < interleavedStereo.Length; i += 2)
        {
            // 9-second cycle: 0–4 s left (440 Hz), 4–4.5 s silence, 4.5–8.5 s right (880 Hz), 8.5–9 s silence.
            double t = (_sample % (9L * 44100)) / 44100.0;
            bool left = t < 4.0;
            bool right = t is >= 4.5 and < 8.5;
            double freq = left ? 440.0 : 880.0;

            short s = 0;
            if (left || right)
            {
                s = (short)(Math.Sin(_phase) * amplitude * short.MaxValue);
                _phase += 2 * Math.PI * freq / 44100.0;
                if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            }
            interleavedStereo[i] = left ? s : (short)0;
            interleavedStereo[i + 1] = right ? s : (short)0;
            _sample++;
        }
    }

    public void Dispose() { }
}

/// <summary>
/// A source that can report how many interleaved samples are ALREADY CAPTURED beyond its current
/// read position — the distance from this consumer's cursor to capture-live.
///
/// <para>Load-bearing for multi-destination sync: a demand-driven tee's produced count trails
/// capture-live by however far its live consumer reads ahead of real time (its send lead). A
/// destination that joins by flushing "to live" against that counter inherits the live consumer's
/// send lead as a PERMANENT render offset — 16–46 ms of cross-room flam, deterministic, for the
/// whole session. Knowing the true distance to capture-live is what lets the tee advance to it
/// instead.</para>
/// </summary>
public interface ICaptureAheadAudioSource
{
    long SamplesAheadOfCursor { get; }
}

/// <summary>
/// Encodes 1024-sample stereo PCM frames into raw AAC-LC access units (ISO/IEC 14496-3
/// raw_data_block, no ADTS) — the per-packet payload of an AirPlay 2 buffered AAC stream, which
/// is the only audio-only stream shape real Apple senders are captured using toward Apple TVs.
/// Implemented over Media Foundation in WinPlay.Capture; defined here so the protocol layer can
/// consume it without a codec dependency.
/// </summary>
public interface IAacFrameEncoder : IDisposable
{
    /// <summary>
    /// Encodes exactly 1024 interleaved stereo frames (2048 shorts). May return null while the
    /// encoder's priming window fills — one access unit per call thereafter.
    /// </summary>
    byte[]? EncodeFrame(ReadOnlySpan<short> interleavedStereo);
}
