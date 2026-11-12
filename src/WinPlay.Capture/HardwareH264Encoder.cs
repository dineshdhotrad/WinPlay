// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace WinPlay.Capture;

/// <summary>
/// Zero-copy, zero-LOH-allocation hardware H.264 encoder using an asynchronous Media
/// Foundation Transform bound to the capture D3D11 device via <see cref="IMFDXGIDeviceManager"/>.
///
/// Configures strict hardware CBR rate control (<c>CODECAPI_AVEncCommonRateControlMode = 0</c>,
/// <c>CODECAPI_AVEncCommonMaxBitRate</c>, and a 0.5-second VBV <c>CODECAPI_AVEncCommonBufferSize</c>)
/// so high-motion fullscreen 3D games never spike instantaneous bitrate and overflow
/// Apple TV (<c>AppleTV5,3</c>+) receiver buffers.
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
    private static readonly Guid MF_MT_MAX_KEYFRAME_SPACING = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    private static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    private static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    private static readonly Guid CODECAPI_AVScenarioInfo = new("b28a6e64-3ff9-446a-8a4b-0d7a53413236");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CODECAPI_AVEncCommonQualityVsSpeed = new("98332df8-03cd-476b-89fa-3f9e442dec9f");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVEncCommonMaxBitRate = new("9651eae4-39b9-4ebf-85ef-d7f444ec7465");
    private static readonly Guid CODECAPI_AVEncCommonBufferSize = new("0db96574-b6a4-4c8b-8106-3773de0310cd");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    private static readonly Guid IID_ID3D11Texture2D = typeof(ID3D11Texture2D).GUID;

    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x00000002;
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x00000004;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x00000040;
    private const uint InterlaceProgressive = 2;
    private const uint ProfileHigh = 100;
    private const uint ProfileBaseline = 66;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint ulVal;
        [FieldOffset(8)] public short boolVal;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetValueDelegate(IntPtr thisPtr, ref Guid api, ref Variant value);

    private readonly IMFDXGIDeviceManager _dxgiManager;
    private readonly IMFTransform _transform;
    private readonly IMFMediaEventGenerator _events;
    private readonly int _width;
    private readonly int _height;
    private readonly long _frameDuration;
    private readonly Thread _eventThread;
    private readonly object _mftGate = new();
    private ID3D11Texture2D? _latestPendingTex;
    private int _needInputCredits;
    private byte[] _annexBScratch = new byte[512 * 1024];
    private volatile bool _stopped;
    private volatile int _forceKeyframePending;
    private volatile int _pendingBitrate;
    private readonly System.Diagnostics.Stopwatch _ptsClock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastPts100ns = -1;
    private long _frameIndex;

    public string Name { get; }
    public bool IsZeroCopy => true;
    public event Action<ReadOnlyMemory<byte>>? Encoded;

    public HardwareH264Encoder(ID3D11Device device, int width, int height, int fps, int bitrate)
    {
        _width = width;
        _height = height;
        _frameDuration = 10_000_000L / Math.Max(1, fps);
        MediaFactory.MFStartup(false).CheckError();

        _dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
        _dxgiManager.ResetDevice(device).CheckError();

        (_transform, Name) = ActivateAndConfigureHardwareEncoder(_dxgiManager, width, height, fps, bitrate);
        _events = _transform.QueryInterface<IMFMediaEventGenerator>();
        _transform.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        _transform.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "WinPlay-HwEncoder"
        };
        _eventThread.Start();
    }

    private static (IMFTransform Transform, string Name) ActivateAndConfigureHardwareEncoder(
        IMFDXGIDeviceManager dxgiManager, int width, int height, int fps, int bitrate)
    {
        RegisterTypeInfo? register = new() { GuidMajorType = MFMediaType_Video, GuidSubtype = MFVideoFormat_H264 };
        MediaFactory.MFTEnumEx(TransformCategoryGuids.VideoEncoder,
            MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_SORTANDFILTER,
            null, register, out IntPtr activatesPtr, out uint count);

        if (count == 0 || activatesPtr == IntPtr.Zero)
            throw new InvalidOperationException("no hardware H.264 encoder available");

        IMFTransform? selected = null;
        string selectedName = "GPU";
        Exception? lastError = null;

        try
        {
            for (int i = 0; i < count; i++)
            {
                IntPtr p = Marshal.ReadIntPtr(activatesPtr, i * IntPtr.Size);
                if (p == IntPtr.Zero) continue;
                using var activate = new IMFActivate(p);
                if (selected is not null) continue;

                string friendly = TryGetName(activate);
                IMFTransform? candidate = null;
                try
                {
                    candidate = activate.ActivateObject<IMFTransform>();
                    using (var attrs = candidate.Attributes)
                    {
                        attrs.Set(MF_TRANSFORM_ASYNC_UNLOCK, 1u);
                        attrs.Set(MF_LOW_LATENCY, 1u);
                    }
                    TryConfigureLowLatencyAndCbrCodecApi(candidate, bitrate);

                    candidate.ProcessMessage(TMessageType.MessageSetD3DManager, (UIntPtr)(ulong)dxgiManager.NativePointer);

                    using (var outType = MediaFactory.MFCreateMediaType())
                    {
                        outType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                        outType.Set(MF_MT_SUBTYPE, MFVideoFormat_H264);
                        outType.Set(MF_MT_AVG_BITRATE, (uint)bitrate);
                        outType.Set(MF_MT_INTERLACE_MODE, InterlaceProgressive);
                        outType.Set(MF_MT_MPEG2_PROFILE, ProfileHigh);
                        outType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
                        outType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
                        outType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                        outType.Set(MF_MT_MAX_KEYFRAME_SPACING, (uint)(fps * 2));
                        try
                        {
                            candidate.SetOutputType(0, outType, 0);
                        }
                        catch
                        {
                            outType.Set(MF_MT_MPEG2_PROFILE, ProfileBaseline);
                            candidate.SetOutputType(0, outType, 0);
                        }
                    }

                    // Configure CBR, VBV buffer, and TU7 preset AFTER SetOutputType and BEFORE SetInputType
                    // (Intel QSV locks ICodecAPI rate-control parameters once SetInputType is called).
                    TryConfigureLowLatencyAndCbrCodecApi(candidate, bitrate);

                    using (var inType = MediaFactory.MFCreateMediaType())
                    {
                        inType.Set(MF_MT_MAJOR_TYPE, MFMediaType_Video);
                        inType.Set(MF_MT_SUBTYPE, MFVideoFormat_NV12);
                        inType.Set(MF_MT_INTERLACE_MODE, InterlaceProgressive);
                        inType.Set(MF_MT_FRAME_SIZE, Pack(width, height));
                        inType.Set(MF_MT_FRAME_RATE, Pack(fps, 1));
                        inType.Set(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
                        candidate.SetInputType(0, inType, 0);
                    }

                    TryConfigureLowLatencyAndCbrCodecApi(candidate, bitrate);
                    selected = candidate;
                    selectedName = $"H.264 zero-copy hardware ({friendly})";
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    candidate?.Dispose();
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(activatesPtr);
        }

        if (selected is null)
            throw new InvalidOperationException($"no compatible hardware H.264 encoder for capture GPU ({lastError?.Message})");

        return (selected, selectedName);
    }

    private static void TryConfigureLowLatencyAndCbrCodecApi(IMFTransform transform, int bitrate)
    {
        IntPtr codecApiPtr = transform.QueryInterfaceOrNull(IID_ICodecAPI);
        if (codecApiPtr == IntPtr.Zero) return;
        try
        {
            IntPtr vtbl = Marshal.ReadIntPtr(codecApiPtr);
            IntPtr setValPtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
            var setVal = Marshal.GetDelegateForFunctionPointer<SetValueDelegate>(setValPtr);

            // 0. Wireless display remoting scenario (eAVScenarioInfo_DisplayRemoting = 1)
            // clamps Intel QSV frame-time jitter and slice bursts for real-time screen casting.
            Guid propScenario = CODECAPI_AVScenarioInfo;
            var vRemoting = new Variant { vt = 19 /* VT_UI4 */, ulVal = 1u };
            setVal(codecApiPtr, ref propScenario, ref vRemoting);

            // 1. Low-latency mode (no frame lookahead)
            Guid propLowLat = CODECAPI_AVLowLatencyMode;
            var vBool = new Variant { vt = 11 /* VT_BOOL */, boolVal = -1 };
            if (setVal(codecApiPtr, ref propLowLat, ref vBool) < 0)
            {
                var vUi4 = new Variant { vt = 19 /* VT_UI4 */, ulVal = 1u };
                setVal(codecApiPtr, ref propLowLat, ref vUi4);
            }

            // 2. Zero B-frames
            Guid propBFrames = CODECAPI_AVEncMPVDefaultBPictureCount;
            var vZero = new Variant { vt = 19 /* VT_UI4 */, ulVal = 0u };
            setVal(codecApiPtr, ref propBFrames, ref vZero);

            // 3. Ultra-fast hardware TU7 real-time preset (0/100) so high-motion sports panning
            // completes in ~1.5-2.5 ms without contending with browser video decoding on the iGPU.
            Guid propSpeed = CODECAPI_AVEncCommonQualityVsSpeed;
            var vQuality = new Variant { vt = 19 /* VT_UI4 */, ulVal = 0u };
            setVal(codecApiPtr, ref propSpeed, ref vQuality);

            // 4. Peak-Constrained VBR (eAVEncCommonRateControlMode_PeakConstrainedVBR = 1) with
            // compact 0.35s VBV buffer: strictly caps high-motion sports peaks at MaxBitRate while
            // allowing zero-reset mid-stream MeanBitRate/MaxBitRate ABR updates on Intel Quick Sync
            // (unlike CBR=0, where Intel QSV returns S_FALSE on mid-stream MeanBitRate changes).
            Guid propRc = CODECAPI_AVEncCommonRateControlMode;
            var vPcvbr = new Variant { vt = 19 /* VT_UI4 */, ulVal = 1u };
            if (setVal(codecApiPtr, ref propRc, ref vPcvbr) != 0)
            {
                setVal(codecApiPtr, ref propRc, ref vZero);
            }

            Guid propMean = CODECAPI_AVEncCommonMeanBitRate;
            var vBitrate = new Variant { vt = 19 /* VT_UI4 */, ulVal = (uint)bitrate };
            setVal(codecApiPtr, ref propMean, ref vBitrate);

            Guid propMax = CODECAPI_AVEncCommonMaxBitRate;
            setVal(codecApiPtr, ref propMax, ref vBitrate);

            Guid propBuf = CODECAPI_AVEncCommonBufferSize;
            var vBuf = new Variant { vt = 19 /* VT_UI4 */, ulVal = (uint)((long)bitrate * 35 / 100) };
            setVal(codecApiPtr, ref propBuf, ref vBuf);
        }
        catch
        {
            // Ignore optional CodecAPI differences across GPU vendors.
        }
        finally
        {
            Marshal.Release(codecApiPtr);
        }
    }

    public void ForceKeyframe() => Interlocked.Exchange(ref _forceKeyframePending, 1);

    public void UpdateBitrate(int bitrateBps) => Interlocked.Exchange(ref _pendingBitrate, bitrateBps);

    private void TryApplyDynamicEncoderUpdates()
    {
        int newBitrate = Interlocked.Exchange(ref _pendingBitrate, 0);
        int forceIdr = Interlocked.Exchange(ref _forceKeyframePending, 0);
        if (newBitrate <= 0 && forceIdr == 0) return;

        IntPtr codecApiPtr = _transform.QueryInterfaceOrNull(IID_ICodecAPI);
        if (codecApiPtr == IntPtr.Zero) return;
        try
        {
            IntPtr vtbl = Marshal.ReadIntPtr(codecApiPtr);
            IntPtr setValPtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
            var setVal = Marshal.GetDelegateForFunctionPointer<SetValueDelegate>(setValPtr);

            if (newBitrate > 0)
            {
                var vBitrate = new Variant { vt = 19 /* VT_UI4 */, ulVal = (uint)newBitrate };
                Guid propMean = CODECAPI_AVEncCommonMeanBitRate;
                setVal(codecApiPtr, ref propMean, ref vBitrate);
                Guid propMax = CODECAPI_AVEncCommonMaxBitRate;
                setVal(codecApiPtr, ref propMax, ref vBitrate);
                Guid propBuf = CODECAPI_AVEncCommonBufferSize;
                var vBuf = new Variant { vt = 19 /* VT_UI4 */, ulVal = (uint)((long)newBitrate * 35 / 100) };
                setVal(codecApiPtr, ref propBuf, ref vBuf);
            }

            if (forceIdr != 0)
            {
                Guid propForceIdr = CODECAPI_AVEncVideoForceKeyFrame;
                var vOne = new Variant { vt = 19 /* VT_UI4 */, ulVal = 1u };
                setVal(codecApiPtr, ref propForceIdr, ref vOne);
            }
        }
        catch { }
        finally
        {
            Marshal.Release(codecApiPtr);
        }
    }

    private void TryEmitForcedKeyframe()
    {
        if (Interlocked.Exchange(ref _forceKeyframePending, 0) == 0) return;
        IntPtr codecApiPtr = _transform.QueryInterfaceOrNull(IID_ICodecAPI);
        if (codecApiPtr == IntPtr.Zero) return;
        try
        {
            IntPtr vtbl = Marshal.ReadIntPtr(codecApiPtr);
            IntPtr setValPtr = Marshal.ReadIntPtr(vtbl, 9 * IntPtr.Size);
            var setVal = Marshal.GetDelegateForFunctionPointer<SetValueDelegate>(setValPtr);
            Guid propForceIdr = CODECAPI_AVEncVideoForceKeyFrame;
            var vOne = new Variant { vt = 19 /* VT_UI4 */, ulVal = 1u };
            setVal(codecApiPtr, ref propForceIdr, ref vOne);
        }
        catch { }
        finally
        {
            Marshal.Release(codecApiPtr);
        }
    }

    private static string TryGetName(IMFActivate activate)
    {
        try { return activate.GetString(MFT_FRIENDLY_NAME); }
        catch (SharpGenException) { return "GPU"; }
    }

    private static ulong Pack(int high, int low) => ((ulong)(uint)high << 32) | (uint)low;

    public void EncodeTexture(ID3D11Texture2D nv12Texture)
    {
        if (_stopped) return;
        lock (_mftGate)
        {
            if (_stopped) return;
            if (_needInputCredits > 0)
            {
                _needInputCredits--;
                if (!FeedGpuTexture(nv12Texture))
                {
                    _latestPendingTex = nv12Texture;
                }
            }
            else
            {
                _latestPendingTex = nv12Texture;
            }
        }
    }

    public void EncodeCpu(byte[] nv12) => throw new NotSupportedException();

    private void EventLoop()
    {
        while (!_stopped)
        {
            IMFMediaEvent evt;
            try { evt = _events.GetEvent(0); }
            catch (SharpGenException) { if (_stopped) return; continue; }

            try
            {
                using (evt)
                {
                    switch (evt.EventType)
                    {
                        case MediaEventTypes.TransformNeedInput:
                            // NEVER block _eventThread waiting for input! Blocking here prevents
                            // TransformHaveOutput from draining already-encoded frames until the next tick.
                            lock (_mftGate)
                            {
                                if (_stopped) return;
                                if (_latestPendingTex is not null)
                                {
                                    var tex = _latestPendingTex;
                                    _latestPendingTex = null;
                                    if (!FeedGpuTexture(tex))
                                        _needInputCredits++;
                                }
                                else
                                {
                                    _needInputCredits++;
                                }
                            }
                            break;
                        case MediaEventTypes.TransformHaveOutput:
                            DrainAllReadyOutputs();
                            break;
                    }
                }
            }
            catch (Exception) { if (_stopped) return; }
        }
    }

    private bool FeedGpuTexture(ID3D11Texture2D nv12Texture)
    {
        TryApplyDynamicEncoderUpdates();

        using var dxgiBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(IID_ID3D11Texture2D, nv12Texture, 0, false);
        using (var buf2d = dxgiBuffer.QueryInterfaceOrNull<IMF2DBuffer>())
        {
            dxgiBuffer.CurrentLength = buf2d is not null ? buf2d.ContiguousLength : dxgiBuffer.MaxLength;
        }

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(dxgiBuffer);
        long pts = (long)(_ptsClock.Elapsed.TotalSeconds * 10_000_000.0);
        if (pts <= _lastPts100ns) pts = _lastPts100ns + _frameDuration;
        _lastPts100ns = pts;
        sample.SampleTime = pts;
        sample.SampleDuration = _frameDuration;
        _frameIndex++;
        try
        {
            _transform.ProcessInput(0, sample, 0);
            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    private void DrainAllReadyOutputs()
    {
        while (!_stopped)
        {
            int producedLength = -1;
            lock (_mftGate)
            {
                if (_stopped) return;
                var streamInfo = _transform.GetOutputStreamInfo(0);
                bool providesSamples = (streamInfo.Flags & 0x300) != 0;

                using var outBuffer = providesSamples ? null : MediaFactory.MFCreateMemoryBuffer(Math.Max(streamInfo.Size, _width * _height));
                using var outSample = providesSamples ? null : MediaFactory.MFCreateSample();
                if (outSample is not null && outBuffer is not null) outSample.AddBuffer(outBuffer);

                var dataBuffer = new OutputDataBuffer { StreamID = 0, Sample = outSample! };
                Result result = _transform.ProcessOutput(ProcessOutputFlags.None, 1, ref dataBuffer, out ProcessOutputStatus _);

                if (result.Code == unchecked((int)0xC00D6D61)) // MF_E_TRANSFORM_STREAM_CHANGE
                {
                    try
                    {
                        using var newType = _transform.GetOutputAvailableType(0, 0);
                        _transform.SetOutputType(0, newType, 0);
                    }
                    catch (SharpGenException) { return; }
                    continue;
                }

                if (result == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput || result.Failure)
                    return;

                var produced = dataBuffer.Sample;
                if (produced is null) return;

                using var converted = produced.ConvertToContiguousBuffer();
                converted.Lock(out IntPtr ptr, out int _, out int cur);
                if (cur > _annexBScratch.Length)
                    _annexBScratch = new byte[Math.Max(cur, _annexBScratch.Length * 2)];
                Marshal.Copy(ptr, _annexBScratch, 0, cur);
                converted.Unlock();
                if (providesSamples) produced.Dispose();
                producedLength = cur;
            }

            if (producedLength > 0)
                Encoded?.Invoke(new ReadOnlyMemory<byte>(_annexBScratch, 0, producedLength));
            else
                return;
        }
    }

    public void Dispose()
    {
        _stopped = true;
        lock (_mftGate)
        {
            _latestPendingTex = null;
            _needInputCredits = 0;
            try { _transform.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero); } catch (SharpGenException) { }
            try { _transform.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero); } catch (SharpGenException) { }
        }
        _eventThread.Join(1000);
        _events.Dispose();
        _transform.Dispose();
        _dxgiManager.Dispose();
        try { MediaFactory.MFShutdown(); } catch (SharpGenException) { }
    }
}
