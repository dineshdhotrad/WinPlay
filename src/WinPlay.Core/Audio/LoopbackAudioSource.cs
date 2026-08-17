// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using NAudio.Dsp;
using NAudio.Wave;

namespace WinPlay.Core.Audio;

/// <summary>
/// System-audio capture: WASAPI loopback → stereo downmix → WDL resample to 44.1 kHz →
/// <see cref="PositionedCaptureRing"/>. The positioned ring locks content to the wall-clock
/// timeline so capture jitter can never shift audio later on the RTP timeline — end-to-end
/// latency stays constant instead of creeping (critical for anchored buffered streams).
/// Note NAudio's documented gotcha: when nothing is playing, DataAvailable never fires — the
/// ring's reader zero-fills and keeps advancing, and its writer jumps across the silent span
/// when capture resumes, so resumed audio lands live.
/// </summary>
public sealed class LoopbackAudioSource : IAudioSource, IFlushableAudioSource, ICaptureDiagnostics, ICaptureAheadAudioSource
{
    private const int TargetRate = PositionedCaptureRing.SampleRate;

    private readonly WasapiLoopbackCapture _capture;
    private readonly WdlResampler _resampler;
    // NAudio's loopback capture cannot use event-driven delivery, so it polls in ~50 ms chunks.
    // The reader margin must exceed that cadence with real headroom or routine scheduling jitter
    // makes the reader nick past the writer — audible micro-cuts. 120 ms here; the event-driven
    // ProcessLoopbackAudioSource (10 ms cadence) keeps the tighter 60 ms default.
    private readonly PositionedCaptureRing _ring = new(marginFrames: 5292);
    private readonly int _sourceChannels;
    private readonly int _sourceRate;

    private float[] _floatScratch = new float[16384];
    private float[] _resampleOut = new float[16384];
    private short[] _shortScratch = new short[16384];

    public LoopbackAudioSource()
    {
        _capture = new WasapiLoopbackCapture();
        _sourceChannels = _capture.WaveFormat.Channels;
        _sourceRate = _capture.WaveFormat.SampleRate;

        _resampler = new WdlResampler();
        _resampler.SetMode(interp: true, filtercnt: 2, sinc: false);
        _resampler.SetFilterParms();
        _resampler.SetFeedMode(wantInputDriven: true);
        _resampler.SetRates(_sourceRate, TargetRate);

        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        int bytesPerSample = _capture.WaveFormat.BitsPerSample / 8;
        int frames = e.BytesRecorded / (bytesPerSample * _sourceChannels);
        if (frames == 0) return;

        EnsureCapacity(ref _floatScratch, frames * 2);

        // Convert to interleaved stereo float. The loopback mix format is float32
        // (WASAPI shared-mode Extensible resolves to IEEE float); tolerate PCM16 too.
        bool isFloat = _capture.WaveFormat.BitsPerSample == 32;
        for (int f = 0; f < frames; f++)
        {
            float l, r;
            if (isFloat)
            {
                int baseIdx = f * _sourceChannels * 4;
                l = BitConverter.ToSingle(e.Buffer, baseIdx);
                r = _sourceChannels > 1 ? BitConverter.ToSingle(e.Buffer, baseIdx + 4) : l;
            }
            else
            {
                int baseIdx = f * _sourceChannels * 2;
                l = BitConverter.ToInt16(e.Buffer, baseIdx) / 32768f;
                r = _sourceChannels > 1 ? BitConverter.ToInt16(e.Buffer, baseIdx + 2) / 32768f : l;
            }
            _floatScratch[f * 2] = l;
            _floatScratch[f * 2 + 1] = r;
        }

        // The ring reconciles the write position against wall time (true-gap jumps) and hands
        // back a drift-corrected output rate so device-clock skew never accumulates.
        double outRate = _ring.BeginWrite(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _resampler.SetRates(_sourceRate, outRate);

        int needed = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOff);
        Array.Copy(_floatScratch, 0, inBuf, inOff, Math.Min(needed, frames) * 2);
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

    /// <inheritdoc />
    public (long UnderrunFrames, long LateFrames, long GapJumps) CaptureStats =>
        (_ring.UnderrunFrames, _ring.LateFrames, _ring.GapJumps);

    /// <inheritdoc />
    public long SamplesAheadOfCursor => (_ring.WriterFrames - _ring.ReaderFrames) * 2;

    /// <summary>Aims the reader at the live edge so the first samples streamed are fresh.</summary>
    public void FlushToLive() => _ring.FlushToLive();

    private static void EnsureCapacity(ref float[] buf, int needed)
    {
        if (buf.Length < needed)
            buf = new float[Math.Max(needed, buf.Length * 2)];
    }

    private static void EnsureCapacity(ref short[] buf, int needed)
    {
        if (buf.Length < needed)
            buf = new short[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        // StopRecording can throw more than InvalidOperationException — unplugging or switching
        // the default render device mid-stream surfaces as a COM error
        // (AUDCLNT_E_DEVICE_INVALIDATED). Catching only one type meant the Dispose() below was
        // skipped (leaking the WASAPI client) AND the exception propagated up through the
        // session teardown chain, where it could strand a destination. Teardown must always
        // complete.
        try { _capture.StopRecording(); }
        catch (Exception) { /* device already gone; nothing left to stop */ }
        try { _capture.Dispose(); }
        catch (Exception) { /* best effort */ }
    }
}
