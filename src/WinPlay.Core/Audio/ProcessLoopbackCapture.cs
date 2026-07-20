// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace WinPlay.Core.Audio;

/// <summary>
/// WASAPI process-loopback capture (Windows 10 2004+). Captures the render audio of a
/// process tree — or, in exclude mode, of every process <em>except</em> a given tree —
/// directly at the process render stage. Because this tap sits <em>before</em> the output
/// endpoint's volume/mute, it keeps capturing even when the speakers are muted. That is
/// what lets WinPlay move system audio to AirPlay while silencing the PC (verified
/// empirically: muting the endpoint does not silence this capture).
///
/// Delivers interleaved 32-bit float at 44.1 kHz stereo.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";

    private readonly int _sampleRate;
    private readonly int _channels;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;
    private EventWaitHandle? _event;
    private Thread? _thread;
    private volatile bool _stopped;

    /// <summary>Capture format is chosen by the caller (usually the system mix rate) so no lossy resample happens here.</summary>
    public int SampleRate => _sampleRate;
    public int Channels => _channels;

    public ProcessLoopbackCapture(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
    }

    /// <summary>Raised on the capture thread with interleaved float samples and the frame count.</summary>
    public event Action<float[], int>? SamplesAvailable;

    /// <summary>Captures every process except <paramref name="excludeProcessId"/>'s tree (i.e. all system audio bar our own).</summary>
    public void StartExcluding(uint excludeProcessId)
    {
        var wfx = new WaveFormatEx
        {
            FormatTag = 3, // WAVE_FORMAT_IEEE_FLOAT
            Channels = (ushort)_channels,
            SamplesPerSec = (uint)_sampleRate,
            BitsPerSample = 32,
            BlockAlign = (ushort)(_channels * 4),
            AvgBytesPerSec = (uint)(_sampleRate * _channels * 4),
            Size = 0,
        };

        _client = Activate(excludeProcessId, mode: 1);

        IntPtr wfxPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(wfx, wfxPtr, false);
        try
        {
            const long hns20ms = 20 * 10000;
            Marshal.ThrowExceptionForHR(_client.Initialize(0 /* SHARED */,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                    | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY,
                hns20ms, 0, wfxPtr, IntPtr.Zero));
        }
        finally { Marshal.FreeHGlobal(wfxPtr); }

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        Marshal.ThrowExceptionForHR(_client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));

        var captureGuid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(_client.GetService(ref captureGuid, out object svc));
        _capture = (IAudioCaptureClient)svc;

        Marshal.ThrowExceptionForHR(_client.Start());
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WinPlay-ProcLoopback" };
        _thread.Start();
    }

    private void CaptureLoop()
    {
        while (!_stopped)
        {
            if (!_event!.WaitOne(100)) continue;
            while (_capture!.GetNextPacketSize(out int frames) == 0 && frames > 0)
            {
                if (_capture.GetBuffer(out IntPtr data, out int framesRead, out uint flags, out _, out _) != 0) break;
                int total = framesRead * Channels;
                float[] buffer = new float[total];
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                    Marshal.Copy(data, buffer, 0, total);
                _capture.ReleaseBuffer(framesRead);
                SamplesAvailable?.Invoke(buffer, framesRead);
            }
        }
    }

    private static IAudioClient Activate(uint targetPid, uint mode)
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = 1, // process loopback
            ProcessLoopbackParams = new AudioClientProcessLoopbackParams { TargetProcessId = targetPid, ProcessLoopbackMode = mode },
        };
        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        Marshal.StructureToPtr(activationParams, paramsPtr, false);
        try
        {
            var propvariant = new PropVariantBlob
            {
                Vt = 65, // VT_BLOB
                BlobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                BlobData = paramsPtr,
            };
            var handler = new ActivateCompletionHandler();
            var iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(
                VirtualDeviceProcessLoopback, ref iid, ref propvariant, handler, out IActivateAudioInterfaceAsyncOperation op));

            if (!handler.Completed.WaitOne(5000))
                throw new TimeoutException("process-loopback activation timed out");
            Marshal.ThrowExceptionForHR(op.GetActivateResult(out int activateHr, out object iface));
            Marshal.ThrowExceptionForHR(activateHr);
            return (IAudioClient)iface;
        }
        finally { Marshal.FreeHGlobal(paramsPtr); }
    }

    public void Dispose()
    {
        _stopped = true;
        _thread?.Join(1000);
        if (_client is not null) { try { _client.Stop(); } catch (Exception) { } Marshal.ReleaseComObject(_client); _client = null; }
        if (_capture is not null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        _event?.Dispose();
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        ref PropVariantBlob activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort FormatTag, Channels;
        public uint SamplesPerSec, AvgBytesPerSec;
        public ushort BlockAlign, BitsPerSample, Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientProcessLoopbackParams { public uint TargetProcessId; public uint ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams { public uint ActivationType; public AudioClientProcessLoopbackParams ProcessLoopbackParams; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariantBlob { public ushort Vt; public ushort R1, R2, R3; public uint BlobSize; public IntPtr BlobData; public IntPtr Padding; }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    private sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly EventWaitHandle Completed = new(false, EventResetMode.ManualReset);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => Completed.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesRead);
        [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
    }
}
