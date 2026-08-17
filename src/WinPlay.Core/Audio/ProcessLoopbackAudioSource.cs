// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Dsp;

namespace WinPlay.Core.Audio;

/// <summary>
/// Adapts <see cref="ProcessLoopbackCapture"/> (push, float) to the <see cref="IAudioSource"/>
/// pull interface. Captures at the system mix rate (usually 48 kHz) and resamples to 44.1 kHz
/// stereo — feeding 48 kHz samples straight into a 44.1 kHz ALAC stream produces audible
/// static, so the resample is mandatory.
///
/// <para>Captured audio lands in a <see cref="PositionedCaptureRing"/>, which locks content to
/// the wall-clock timeline: capture jitter can never shift audio later on the RTP timeline, so
/// end-to-end latency stays constant instead of creeping (critical for anchored buffered
/// streams, which set their timeline once). <see cref="Read"/> zero-fills positions that have
/// no data yet, keeping the RTP pump at a constant rate.</para>
/// </summary>
public sealed class ProcessLoopbackAudioSource : IAudioSource, IFlushableAudioSource, ICaptureDiagnostics, ICaptureLatency, IPrimeableCapture, ICaptureAheadAudioSource
{
    private const int TargetRate = PositionedCaptureRing.SampleRate;

    private readonly ProcessLoopbackCapture _capture;
    private readonly WdlResampler _resampler;
    private readonly PositionedCaptureRing _ring = new();
    private readonly int _sourceRate;
    private readonly int _sourceChannels;

    private float[] _stereoScratch = new float[16384];
    private float[] _resampleOut = new float[16384];
    private short[] _shortScratch = new short[16384];

    public ProcessLoopbackAudioSource(uint excludeProcessId)
    {
        // The process-loopback virtual device mirrors the shared mix, so capture at the
        // default render endpoint's mix rate/channels, then resample to 44.1 kHz.
        (_sourceRate, _sourceChannels) = MixFormat();

        _resampler = new WdlResampler();
        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(wantInputDriven: true);
        _resampler.SetRates(_sourceRate, TargetRate);

        _capture = new ProcessLoopbackCapture(_sourceRate, _sourceChannels);
        _capture.SamplesAvailable += OnSamples;
        // Recovery and death reports were raised and discarded — no subscriber existed anywhere,
        // so a capture client rebuilding itself in a storm (audible as cuts) or dying outright
        // (audible as permanent silence) left no trace in any log. Forwarded so the layer that
        // owns logging can hear them.
        _capture.Diagnostic += d => Diagnostic?.Invoke(d);
        _capture.CaptureFailed += ex => Diagnostic?.Invoke($"capture FAILED terminally: {ex.Message}");
        _capture.StartExcluding(excludeProcessId);
    }

    private static (int Rate, int Channels) MixFormat()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            return (mix.SampleRate, Math.Max(1, mix.Channels));
        }
        catch (Exception)
        {
            return (48000, 2); // safe default for modern Windows
        }
    }

    /// <inheritdoc />
    public bool WaitUntilPrimed(TimeSpan timeout) => _primed.Wait(timeout);

    private readonly ManualResetEventSlim _primed = new(false);

    private void OnSamples(float[] samples, int frames)
    {
        SampleBatchObserved?.Invoke();
        // Set on the first callback CARRYING AUDIO. A zero-frame callback means the engine has
        // started but has nothing yet, so treating it as primed would reopen the very window this
        // closes.
        if (frames > 0) _primed.Set();
        if (frames == 0) return;

        // Downmix to interleaved stereo float.
        EnsureCapacity(ref _stereoScratch, frames * 2);
        for (int f = 0; f < frames; f++)
        {
            int baseIdx = f * _sourceChannels;
            float l = samples[baseIdx];
            float r = _sourceChannels > 1 ? samples[baseIdx + 1] : l;
            _stereoScratch[f * 2] = l;
            _stereoScratch[f * 2 + 1] = r;
        }

        // The ring reconciles the write position against wall time (true-gap jumps) and hands
        // back a drift-corrected output rate so device-clock skew never accumulates.
        double outRate = _ring.BeginWrite(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _resampler.SetRates(_sourceRate, outRate);

        int needed = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOff);
        Array.Copy(_stereoScratch, 0, inBuf, inOff, Math.Min(needed, frames) * 2);
        int maxOut = (int)((long)frames * (long)Math.Ceiling(outRate) / _sourceRate) + 32;
        EnsureCapacity(ref _resampleOut, maxOut * 2);
        int produced = _resampler.ResampleOut(_resampleOut, 0, frames, maxOut, 2);

        EnsureCapacity(ref _shortScratch, produced * 2);
        for (int i = 0; i < produced * 2; i++)
        {
            float v = Math.Clamp(_resampleOut[i], -1f, 1f);
            _shortScratch[i] = (short)(v * 32767f);
        }
        _ring.Append(_shortScratch.AsSpan(0, produced * 2));
    }

    public void Read(Span<short> interleavedStereo) => _ring.Read(interleavedStereo);

    /// <summary>
    /// The capture period the audio engine granted, in milliseconds. Reported rather than
    /// assumed: whether the low-latency path applies depends on the endpoint and on whether the
    /// shared engine's period is already locked by another app.
    /// </summary>
    public double CapturePeriodMs => _capture.PeriodMilliseconds;

    /// <summary>Why the low-latency capture path was or was not used.</summary>
    public string CaptureLatencyStatus => _capture.LowLatencyStatus;

    /// <summary>
    /// How far the newest sample <see cref="Read"/> can hand out actually lags real time, in
    /// seconds: the reader is deliberately held <see cref="PositionedCaptureRing.DefaultMarginFrames"/>
    /// behind the writer (a persistent jitter margin, not a one-time startup cost — see
    /// <see cref="PositionedCaptureRing"/>), plus the WASAPI capture callback period, since a sample
    /// is not visible to the ring until its callback delivers it. A caller that assumes "the newest
    /// sample sent was captured now" (as the mirror audio sync loop used to) is wrong by exactly this
    /// much, every single packet — the dominant term in mirroring's audio-lags-video lip-sync offset.
    /// </summary>
    public double CaptureLatencySeconds =>
        PositionedCaptureRing.DefaultMarginFrames / (double)TargetRate + CapturePeriodMs / 1000.0;

    /// <summary>
    /// Raised once per capture callback, so the real delivery cadence can be measured rather
    /// than inferred from the requested buffer size.
    /// </summary>
    public event Action? SampleBatchObserved;

    /// <summary>Capture recovery / failure reports, forwarded from the WASAPI layer.</summary>
    public event Action<string>? Diagnostic;

    /// <inheritdoc />
    public (long UnderrunFrames, long LateFrames, long GapJumps) CaptureStats =>
        (_ring.UnderrunFrames, _ring.LateFrames, _ring.GapJumps);

    /// <inheritdoc />
    public long SamplesAheadOfCursor => (_ring.WriterFrames - _ring.ReaderFrames) * 2;

    /// <summary>Aims the reader at the live edge so the first samples streamed are fresh.</summary>
    public void FlushToLive() => _ring.FlushToLive();

    private static void EnsureCapacity(ref float[] buf, int needed)
    {
        if (buf.Length < needed) buf = new float[Math.Max(needed, buf.Length * 2)];
    }

    private static void EnsureCapacity(ref short[] buf, int needed)
    {
        if (buf.Length < needed) buf = new short[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose()
    {
        _capture.SamplesAvailable -= OnSamples;
        _capture.Dispose();
    }
}
