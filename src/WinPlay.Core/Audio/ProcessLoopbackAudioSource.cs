// SPDX-License-Identifier: GPL-3.0-or-later
using NAudio.CoreAudioApi;
using NAudio.Dsp;

namespace WinPlay.Core.Audio;

/// <summary>
/// Adapts <see cref="ProcessLoopbackCapture"/> (push, float) to the <see cref="IAudioSource"/>
/// pull interface. Captures at the system mix rate (usually 48 kHz) and resamples to
/// 44.1 kHz stereo with a WDL resampler — feeding 48 kHz samples straight into a 44.1 kHz
/// ALAC stream is what produced audible static, so the resample is mandatory. Captured
/// audio lands in a ring buffer; <see cref="Read"/> drains it and zero-fills on underrun so
/// the RTP pump runs at a constant rate.
/// </summary>
public sealed class ProcessLoopbackAudioSource : IAudioSource
{
    private const int TargetRate = 44100;

    private readonly ProcessLoopbackCapture _capture;
    private readonly WdlResampler _resampler;
    private readonly int _sourceRate;
    private readonly int _sourceChannels;

    private readonly short[] _ring = new short[TargetRate * 2 * 4]; // 4 s stereo
    private readonly object _lock = new();
    private int _ringRead, _ringWrite, _ringCount;

    private float[] _stereoScratch = new float[16384];
    private float[] _resampleOut = new float[16384];

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

    private void OnSamples(float[] samples, int frames)
    {
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

        // Resample source-rate → 44.1 kHz stereo.
        int needed = _resampler.ResamplePrepare(frames, 2, out float[] inBuf, out int inOff);
        Array.Copy(_stereoScratch, 0, inBuf, inOff, Math.Min(needed, frames) * 2);
        int maxOut = (int)((long)frames * TargetRate / _sourceRate) + 16;
        EnsureCapacity(ref _resampleOut, maxOut * 2);
        int produced = _resampler.ResampleOut(_resampleOut, 0, frames, maxOut, 2);

        lock (_lock)
        {
            for (int i = 0; i < produced * 2; i++)
            {
                float v = Math.Clamp(_resampleOut[i], -1f, 1f);
                _ring[_ringWrite] = (short)(v * 32767f);
                _ringWrite = (_ringWrite + 1) % _ring.Length;
                if (_ringCount == _ring.Length)
                    _ringRead = (_ringRead + 1) % _ring.Length; // overwrite oldest
                else
                    _ringCount++;
            }
        }
    }

    public void Read(Span<short> interleavedStereo)
    {
        lock (_lock)
        {
            int available = Math.Min(_ringCount, interleavedStereo.Length);
            for (int i = 0; i < available; i++)
            {
                interleavedStereo[i] = _ring[_ringRead];
                _ringRead = (_ringRead + 1) % _ring.Length;
            }
            _ringCount -= available;
            interleavedStereo[available..].Clear();
        }
    }

    private static void EnsureCapacity(ref float[] buf, int needed)
    {
        if (buf.Length < needed) buf = new float[Math.Max(needed, buf.Length * 2)];
    }

    public void Dispose()
    {
        _capture.SamplesAvailable -= OnSamples;
        _capture.Dispose();
    }
}
