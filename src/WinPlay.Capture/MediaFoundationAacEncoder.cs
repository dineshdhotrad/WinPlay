// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;
using WinPlay.Core.Audio;

namespace WinPlay.Capture;

/// <summary>
/// AAC-LC encoder over Microsoft's Media Foundation AAC MFT (always present on Windows).
/// PCM 44.1 kHz / 16-bit / stereo in, one raw AAC-LC access unit (ISO/IEC 14496-3
/// <c>raw_data_block</c>, 1024 samples, no ADTS header) out per frame — exactly the payload an
/// AirPlay 2 buffered AAC stream carries per packet.
///
/// <para>AAC-LC is the codec every captured Apple sender uses on the buffered stream to an
/// Apple TV; ALAC there is a receiver-table fiction no real sender exercises. The MFT's
/// <c>MF_MT_AAC_PAYLOAD_TYPE = 0</c> default emits raw access units, which is what the
/// receiver's decoder consumes after ChaCha20-Poly1305 unsealing.</para>
/// </summary>
public sealed class MediaFoundationAacEncoder : IAacFrameEncoder
{
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    private static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");

    private static readonly Guid CLSID_AACMFTEncoder = new("93af0c51-2275-45d2-a35b-f2ba21caed00");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int FrameSamples = 1024;
    // 24000 B/s = 192 kbit/s stereo — the highest tier Microsoft's AAC MFT offers and the
    // conventional quality point for AirPlay AAC audio.
    private const uint OutputBytesPerSecond = 24000;

    private readonly IMFTransform _transform;
    private readonly int _outputBufferSize;
    private readonly Queue<byte[]> _pending = new();
    private long _sampleIndex;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    public MediaFoundationAacEncoder()
    {
        MediaFactory.MFStartup(false).CheckError();

        Guid clsid = CLSID_AACMFTEncoder, iid = IID_IMFTransform;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr ptr);
        if (hr < 0 || ptr == IntPtr.Zero)
            throw new InvalidOperationException($"could not create AAC encoder MFT (hr=0x{hr:X8})");
        _transform = new IMFTransform(ptr);

        // Per Microsoft's AAC encoder contract: input type first, then output.
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            inType.Set(MF_MT_SUBTYPE, MFAudioFormat_PCM);
            inType.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)SampleRate);
            inType.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)Channels);
            inType.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            inType.Set(MF_MT_AUDIO_BLOCK_ALIGNMENT, (uint)(Channels * 2));
            inType.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)(SampleRate * Channels * 2));
            _transform.SetInputType(0, inType, 0);
        }
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            outType.Set(MF_MT_SUBTYPE, MFAudioFormat_AAC);
            outType.Set(MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)SampleRate);
            outType.Set(MF_MT_AUDIO_NUM_CHANNELS, (uint)Channels);
            outType.Set(MF_MT_AUDIO_BITS_PER_SAMPLE, 16u);
            outType.Set(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, OutputBytesPerSecond);
            // Raw access units (no ADTS) and the AAC-LC profile, set EXPLICITLY — the default is
            // documented as 0/raw, but the AirPlay packet's validity depends on it, so it is
            // pinned rather than assumed (verified on this machine: AUs begin with a
            // raw_data_block, never an ADTS syncword, and the negotiated ASC is 0x12 0x10).
            outType.Set(MF_MT_AAC_PAYLOAD_TYPE, 0u);
            outType.Set(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29u);
            _transform.SetOutputType(0, outType, 0);
        }

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _outputBufferSize = Math.Max(streamInfo.Size, 8192);

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    /// <inheritdoc />
    public byte[]? EncodeFrame(ReadOnlySpan<short> interleavedStereo)
    {
        if (interleavedStereo.Length != FrameSamples * Channels)
            throw new ArgumentException($"expected {FrameSamples * Channels} samples", nameof(interleavedStereo));

        int byteCount = interleavedStereo.Length * 2;
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(byteCount))
        {
            buffer.Lock(out IntPtr dst, out _, out _);
            unsafe
            {
                fixed (short* src = interleavedStereo)
                    Buffer.MemoryCopy(src, (void*)dst, byteCount, byteCount);
            }
            buffer.Unlock();
            buffer.CurrentLength = byteCount;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
            // 10 MHz units; exact rational timing keeps the MFT's internal clock honest.
            sample.SampleTime = _sampleIndex * 10_000_000L / SampleRate;
            sample.SampleDuration = FrameSamples * 10_000_000L / SampleRate;
            _sampleIndex += FrameSamples;
            _transform.ProcessInput(0, sample, 0);
        }

        DrainOutput();
        // The MFT holds a short priming window at start, so the first call(s) may yield nothing;
        // one access unit per input thereafter. The pump treats null as "no packet this slot".
        return _pending.Count > 0 ? _pending.Dequeue() : null;
    }

    private void DrainOutput()
    {
        while (true)
        {
            using var outBuffer = MediaFactory.MFCreateMemoryBuffer(_outputBufferSize);
            using var outSample = MediaFactory.MFCreateSample();
            outSample.AddBuffer(outBuffer);

            var dataBuffer = new OutputDataBuffer { StreamID = 0, Sample = outSample };
            Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);
            if (result == ResultCode.TransformNeedMoreInput) return;
            result.CheckError();

            using var produced = outSample.ConvertToContiguousBuffer();
            produced.Lock(out IntPtr data, out _, out int length);
            byte[] accessUnit = new byte[length];
            Marshal.Copy(data, accessUnit, 0, length);
            produced.Unlock();
            _pending.Enqueue(accessUnit);
        }
    }

    public void Dispose() => _transform.Dispose();
}
