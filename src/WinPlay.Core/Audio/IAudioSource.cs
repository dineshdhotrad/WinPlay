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
