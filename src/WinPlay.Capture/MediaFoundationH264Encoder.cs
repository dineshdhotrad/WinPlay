// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.MediaFoundation;

namespace WinPlay.Capture;

/// <summary>
/// Software Media Foundation H.264 encoder (Microsoft's encoder MFT, always present) —
/// the fallback when no hardware encoder is available. NV12 frames in, Annex-B access
/// units out. Synchronous ProcessInput/ProcessOutput drain, surfaced through the shared
/// <see cref="IH264Encoder"/> event interface.
/// </summary>
internal sealed class MediaFoundationH264Encoder : IH264Encoder
{
    private static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");

    private static readonly Guid CLSID_CMSH264EncoderMFT = new("6ca50344-051a-4ded-9779-a43305165e35");
    private static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");

    private const uint InterlaceProgressive = 2;
    private const uint ProfileMain = 77;

    private readonly IMFTransform _transform;
    private readonly int _outputBufferSize;
    private long _frameIndex;

    public string Name => "H.264 software (Microsoft MFT)";
    public event Action<byte[]>? Encoded;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    public MediaFoundationH264Encoder(int width, int height, int fps, int bitrate)
    {
        MediaFactory.MFStartup(false).CheckError();

        Guid clsid = CLSID_CMSH264EncoderMFT, iid = IID_IMFTransform;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr ptr);
        if (hr < 0 || ptr == IntPtr.Zero)
            throw new InvalidOperationException($"could not create H.264 encoder MFT (hr=0x{hr:X8})");
        _transform = new IMFTransform(ptr);

        // Output type (H.264) must be set before the input type on encoder MFTs.
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

        var streamInfo = _transform.GetOutputStreamInfo(0);
        _outputBufferSize = Math.Max(streamInfo.Size, width * height * 4);

        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    /// <summary>Encodes one NV12 frame, raising <see cref="Encoded"/> for each access unit produced.</summary>
    public void Encode(byte[] nv12)
    {
        using (var buffer = MediaFactory.MFCreateMemoryBuffer(nv12.Length))
        {
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
            _transform.ProcessInput(0, sample, 0);
        }
        DrainOutput();
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

            if (result == ResultCode.TransformNeedMoreInput)
                break;
            result.CheckError();

            using var converted = dataBuffer.Sample.ConvertToContiguousBuffer();
            converted.Lock(out IntPtr ptr, out int _, out int cur);
            byte[] annexB = new byte[cur];
            Marshal.Copy(ptr, annexB, 0, cur);
            converted.Unlock();
            Encoded?.Invoke(annexB);
        }
    }

    public void Dispose()
    {
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
        try { _transform.ProcessMessage(TMessageType.MessageNotifyEndStreaming, UIntPtr.Zero); } catch (SharpGenException) { }
        _transform.Dispose();
        try { MediaFactory.MFShutdown(); } catch (SharpGenException) { }
    }
}
