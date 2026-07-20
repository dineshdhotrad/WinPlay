// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace WinPlay.Capture;

/// <summary>
/// Hardware-accelerated H.264 encoder using an asynchronous Media Foundation Transform.
/// <see cref="MediaFactory.MFTEnumEx"/> selects the system's hardware encoder — Intel
/// Quick Sync, AMD VCE/VCN, NVIDIA NVENC or Qualcomm/Adreno — so encoding runs on the GPU
/// and keeps up with high-resolution, high-frame-rate mirroring (software H.264 cannot).
/// Configured for low-latency realtime streaming (CBR, short GOP). NV12 system-memory
/// frames in, Annex-B access units out via <see cref="Encoded"/>.
///
/// If no hardware encoder is available this throws, and the caller falls back to the
/// software <see cref="MediaFoundationH264Encoder"/>.
/// </summary>
internal sealed class HardwareH264Encoder : IH264Encoder
{
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_ALL_SAMPLES_INDEPENDENT = new("c9173739-5e56-461c-b713-46fb995cb95f");
    private static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");

    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x00000001;
    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const uint InterlaceProgressive = 2;
    private const uint ProfileMain = 77;

    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly int _width;
    private readonly int _height;
    private readonly Thread _eventThread;
    private readonly BlockingCollection<byte[]> _pending = new(new ConcurrentQueue<byte[]>(), boundedCapacity: 8);
    private volatile bool _stopped;
    private long _frameIndex;

    public string Name { get; }
    public event Action<byte[]>? Encoded;

    public HardwareH264Encoder(int width, int height, int fps, int bitrate)
    {
        _width = width;
        _height = height;
        MediaFactory.MFStartup(false).CheckError();

        (_transform, Name) = ActivateHardwareEncoder();

        // Unlock the async MFT (required before configuring a hardware encoder). MFT
        // attributes come from the transform's Attributes, not a QueryInterface.
        using (var attrs = _transform.Attributes)
        {
            attrs.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
            attrs.Set(MF_LOW_LATENCY, 1u);
        }

        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
            outType.Set(MF_MT_AVG_BITRATE, (uint)bitrate);
            outType.Set(MF_MT_INTERLACE_MODE, InterlaceProgressive);
            outType.Set(MF_MT_MPEG2_PROFILE, ProfileMain);
            outType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            outType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
            outType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            outType.Set(MF_MT_MAX_KEYFRAME_SPACING, (uint)(fps * 2)); // GOP ~2 s so SPS/PPS recur
            _transform.SetOutputType(0, outType, 0);
        }

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
            inType.Set(MF_MT_INTERLACE_MODE, InterlaceProgressive);
            inType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
            inType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
            inType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
            _transform.SetInputType(0, inType, 0);
        }

        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _eventThread = new Thread(EventLoop) { IsBackground = true, Name = "WinPlay-HwEncoder" };
        _eventThread.Start();
    }

    private static (IMFTransform Transform, string Name) ActivateHardwareEncoder()
    {
        RegisterTypeInfo? register = new() { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            null, register, out IntPtr activatesPtr, out uint count);

        if (count == 0 || activatesPtr == IntPtr.Zero)
            throw new InvalidOperationException("no hardware H.264 encoder available");

        try
        {
            // Take the first (best) hardware encoder. Each array element is an AddRef'd
            // IMFActivate we must release; wrapping in a ComObject transfers that one
            // reference (disposing it releases exactly once — do NOT also Marshal.Release).
            IntPtr first = Marshal.ReadIntPtr(activatesPtr, 0);
            using var activate = new IMFActivate(first);
            string name = TryGetName(activate);
            var transform = activate.ActivateObject<IMFTransform>();
            for (int i = 1; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                if (p != IntPtr.Zero) Marshal.Release(p);
            }
            return (transform, $"H.264 hardware ({name})");
        }
        finally
        {
            Marshal.FreeCoTaskMem(activatesPtr);
        }
    }

    private static string TryGetName(IMFActivate activate)
    {
        try { return activate.GetString(new Guid("314ffbae-5b41-4c95-9c19-4e7d586face3")); } // MFT_FRIENDLY_NAME_Attribute
        catch (SharpGenException) { return "GPU"; }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>Queues one NV12 system-memory frame for encoding (drops if the encoder is saturated).</summary>
    public void Encode(byte[] nv12)
    {
        if (_stopped) return;
        // Non-blocking: if the encoder is behind, drop this frame to stay live (better than a stall).
        _pending.TryAdd(nv12.Clone() as byte[] ?? nv12, 0);
    }

    private void EventLoop()
    {
        try
        {
            while (!_stopped)
            {
                IMFMediaEvent evt;
                try { evt = _events.GetEvent(0); }
                catch (SharpGenException) { return; }

                using (evt)
                {
                    switch (evt.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput:
                            if (_pending.TryTake(out byte[]? frame, Timeout.Infinite) && frame is not null)
                                FeedInput(frame);
                            break;
                        case MediaEventTypes.TransformHaveOutput:
                            DrainOutput();
                            break;
                    }
                }
            }
        }
        catch (Exception) { /* shutting down */ }
    }

    private void FeedInput(byte[] nv12)
    {
        using var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length);
        buffer.Lock(out IntPtr ptr, out int _, out int _);
        Marshal.Copy(nv12, 0, ptr, nv12.Length);
        buffer.Unlock();
        buffer.CurrentLength = nv12.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        const long duration = 10_000_000 / 30;
        sample.SampleTime = _frameIndex * duration;
        sample.SampleDuration = duration;
        _frameIndex++;
        try { _transform.ProcessInput(0, sample, 0); }
        catch (SharpGenException) { /* encoder not ready; drop */ }
    }

    private void DrainOutput()
    {
        var streamInfo = _transform.GetOutputStreamInfo(0);
        bool providesSamples = (streamInfo.Flags & 0x300) != 0; // PROVIDES_SAMPLES | CAN_PROVIDE_SAMPLES

        using var outBuffer = providesSamples ? null : MediaFactory.MFCreateMemoryBuffer(Math.Max(streamInfo.Size, _width * _height));
        using var outSample = providesSamples ? null : MediaFactory.MFCreateSample();
        if (outSample is not null && outBuffer is not null) outSample.AddBuffer(outBuffer);

        var dataBuffer = new OutputDataBuffer { StreamID = 0, Sample = outSample! };
        Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);
        if (result == ResultCode.TransformNeedMoreInput || result.Failure) return;

        var produced = dataBuffer.Sample;
        using var converted = produced.ConvertToContiguousBuffer();
        converted.Lock(out IntPtr ptr, out int _, out int cur);
        byte[] annexB = new byte[cur];
        Marshal.Copy(ptr, annexB, 0, cur);
        converted.Unlock();
        if (providesSamples) produced.Dispose();
        Encoded?.Invoke(annexB);
    }

    public void Dispose()
    {
        _stopped = true;
        _pending.CompleteAdding();
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
        try { _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch (SharpGenException) { }
        _eventThread.Join(1000);
        _events.Dispose();
        _transform.Dispose();
        try { MediaFactory.MFShutdown(); } catch (SharpGenException) { }
    }
}
