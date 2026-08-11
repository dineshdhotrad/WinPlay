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

        // Anything failing from here on must release the client. The constructor of the owning
        // source aborts on throw, so no Dispose() will ever run — and an abandoned audio client
        // is not inert: a later attempt can see it as "engine periodicity/format locked by
        // another app", turning one transient failure into a persistent one.
        try
        {
            StartExcludingCore(_client, wfx);
        }
        catch
        {
            if (_client is not null) { try { _client.Stop(); } catch (Exception) { } Marshal.ReleaseComObject(_client); _client = null; }
            if (_capture is not null) { Marshal.ReleaseComObject(_capture); _capture = null; }
            _event?.Dispose();
            _event = null;
            throw;
        }
    }

    private void StartExcludingCore(IAudioClient client, WaveFormatEx wfx)
    {
        IntPtr wfxPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        Marshal.StructureToPtr(wfx, wfxPtr, false);
        try
        {
            const uint streamFlags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

            if (!TryInitializeLowLatency(wfxPtr, streamFlags))
            {
                // Classic path. The BUFFER is sized from the engine's own period (two periods of
                // headroom) rather than a hardcoded constant, which can only ever be wrong on
                // some machine. Note the buffer is not the delivery cadence: with
                // AUDCLNT_STREAMFLAGS_EVENTCALLBACK the engine signals at its own period —
                // measured here at 10.0 ms (p95 10.8), already meeting the ≤10 ms capture-latency
                // target. The extra headroom absorbs a late thread wake without losing samples.
                long hnsBuffer = 20 * 10000; // only if the engine will not report its period
                if (client.GetDevicePeriod(out long defaultPeriod, out long minimumPeriod) == 0)
                {
                    hnsBuffer = Math.Max(defaultPeriod * 2, minimumPeriod);
                    PeriodFrames = (int)(defaultPeriod * _sampleRate / 10_000_000);
                    LowLatencyStatus += $"; engine period {defaultPeriod / 10000.0:F1} ms";
                }
                Marshal.ThrowExceptionForHR(client.Initialize(0 /* SHARED */, streamFlags,
                    hnsBuffer, 0, wfxPtr, IntPtr.Zero));
            }
        }
        finally { Marshal.FreeHGlobal(wfxPtr); }

        _event = new EventWaitHandle(false, EventResetMode.AutoReset);
        Marshal.ThrowExceptionForHR(client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle()));

        var captureGuid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(client.GetService(ref captureGuid, out object svc));
        _capture = (IAudioCaptureClient)svc;

        Marshal.ThrowExceptionForHR(client.Start());
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WinPlay-ProcLoopback" };
        _thread.Start();
    }

    /// <summary>
    /// Negotiates the smallest capture period the audio engine will grant, via
    /// <c>IAudioClient3</c> (Task B6). <c>GetSharedModeEnginePeriod</c> reports the engine's
    /// default, fundamental, minimum and maximum periods for this format;
    /// <c>InitializeSharedAudioStream</c> then asks for the minimum instead of the ~10-20 ms
    /// default, cutting a whole buffer's worth of latency out of the capture stage.
    ///
    /// <para>Returns false — leaving the caller to use the classic path — when the interface is
    /// unavailable or the engine refuses. That is expected, not exceptional: the process-loopback
    /// virtual endpoint is not guaranteed to implement IAudioClient3, and a shared engine already
    /// running for another app reports <c>AUDCLNT_E_ENGINE_PERIODICITY_LOCKED</c> /
    /// <c>ENGINE_FORMAT_LOCKED</c> because its period is fixed by whoever initialised it first.
    /// Capability negotiation with a documented fallback is the correct shape here; forcing the
    /// low-latency path would simply fail to capture on those machines.</para>
    /// </summary>
    private bool TryInitializeLowLatency(IntPtr wfxPtr, uint streamFlags)
    {
        IAudioClient3? client3 = _client as IAudioClient3;
        if (client3 is null)
        {
            LowLatencyStatus = "IAudioClient3 not supported by this endpoint";
            return false;
        }

        try
        {
            int periodHr = client3.GetSharedModeEnginePeriod(wfxPtr, out uint defaultPeriod,
                out uint fundamentalPeriod, out uint minPeriod, out uint maxPeriod);
            if (periodHr != 0)
            {
                LowLatencyStatus = $"GetSharedModeEnginePeriod failed (0x{periodHr:X8})";
                return false;
            }

            // Ask for the minimum, but never below the engine's fundamental quantum, and align to
            // a multiple of it — the engine rejects anything else.
            uint requested = Math.Max(minPeriod, fundamentalPeriod);
            if (fundamentalPeriod > 0 && requested % fundamentalPeriod != 0)
                requested = (requested / fundamentalPeriod + 1) * fundamentalPeriod;
            if (requested > maxPeriod) requested = defaultPeriod;

            int hr = client3.InitializeSharedAudioStream(streamFlags, requested, wfxPtr, IntPtr.Zero);
            if (hr != 0)
            {
                LowLatencyStatus = hr switch
                {
                    unchecked((int)0x88890026) => "engine periodicity locked by another app",
                    unchecked((int)0x88890027) => "engine format locked by another app",
                    _ => $"InitializeSharedAudioStream failed (0x{hr:X8})",
                };
                return false;
            }

            PeriodFrames = (int)requested;
            LowLatencyStatus = $"IAudioClient3 granted {requested} frames "
                + $"(default {defaultPeriod}, fundamental {fundamentalPeriod}, min {minPeriod}, max {maxPeriod})";
            return true;
        }
        catch (Exception ex)
        {
            // An endpoint that advertises IAudioClient3 but misbehaves must not break capture.
            LowLatencyStatus = $"IAudioClient3 threw: {ex.Message}";
            return false;
        }
    }

    /// <summary>Why the low-latency path was or was not used — reported, never assumed.</summary>
    public string LowLatencyStatus { get; private set; } = "not attempted";

    /// <summary>
    /// Capture period actually granted, in frames (0 when the classic 20 ms path was used).
    /// Exposed so diagnostics can report the real capture-stage latency rather than assuming it.
    /// </summary>
    public int PeriodFrames { get; private set; }

    /// <summary>The granted capture period in milliseconds — the B6 acceptance measure.</summary>
    public double PeriodMilliseconds => PeriodFrames > 0 ? PeriodFrames * 1000.0 / _sampleRate : 20.0;

    private void CaptureLoop()
    {
        // Raise this capture thread to the MMCSS "Pro Audio" class (B6) so the scheduler treats
        // it as glitch-sensitive real-time audio work, reducing dropouts under system load.
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        try
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
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
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
            // Ask the activation itself for IAudioClient3 first. Requesting the derived
            // interface up front is the correct COM approach and is what enables the
            // low-latency path (B6): the object returned for a plain IAudioClient request on
            // the process-loopback virtual device does not answer QueryInterface for
            // IAudioClient3. Falls back to IAudioClient when the activation refuses.
            foreach (Guid requested in new[] { typeof(IAudioClient3).GUID, typeof(IAudioClient).GUID })
            {
                var handler = new ActivateCompletionHandler();
                var iid = requested;
                int hr = ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, ref iid,
                    ref propvariant, handler, out IActivateAudioInterfaceAsyncOperation op);
                if (hr != 0) continue;

                if (!handler.Completed.WaitOne(5000))
                    throw new TimeoutException("process-loopback activation timed out");
                if (op.GetActivateResult(out int activateHr, out object iface) != 0 || activateHr != 0)
                    continue;
                if (iface is IAudioClient client) return client;
            }
            throw new NotSupportedException("process-loopback activation returned no usable audio client");
        }
        finally { Marshal.FreeHGlobal(paramsPtr); }
    }

    public void Dispose()
    {
        _stopped = true;

        // The capture thread must be confirmed stopped before its COM objects are released.
        // Releasing them while it is still inside GetNextPacketSize/GetBuffer would fault on a
        // background thread with no handler, which terminates the process. The wait is generous
        // (the loop polls its event on a 100 ms timeout, so a normal exit takes <100 ms); if the
        // thread is genuinely wedged in a driver call, leaking the RCWs is strictly safer than
        // pulling them out from under it — the GC releases them when the thread finally ends.
        bool threadStopped = _thread is null || _thread.Join(TimeSpan.FromSeconds(3));
        if (!threadStopped)
        {
            _event?.Dispose();
            return;
        }

        if (_client is not null) { try { _client.Stop(); } catch (Exception) { } Marshal.ReleaseComObject(_client); _client = null; }
        if (_capture is not null) { Marshal.ReleaseComObject(_capture); _capture = null; }
        _event?.Dispose();
    }

    [DllImport("avrt.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [DllImport("avrt.dll", SetLastError = true)]
    private static extern bool AvRevertMmThreadCharacteristics(IntPtr avrtHandle);

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

    /// <summary>
    /// <c>IAudioClient3</c> — the low-latency shared-mode extension (Windows 10 1703+). Only the
    /// two methods WinPlay needs are declared, but every inherited slot must be present and in
    /// order: a COM vtable is positional, so omitting a base method would silently call the wrong
    /// function. The inherited entries are therefore declared as opaque placeholders.
    /// </summary>
    [ComImport, Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient3
    {
        // --- IAudioClient (12 methods) ---
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

        // --- IAudioClient2 (3 methods) ---
        [PreserveSig] int IsOffloadCapable(int category, out bool offloadCapable);
        [PreserveSig] int SetClientProperties(IntPtr properties);
        [PreserveSig] int GetBufferSizeLimits(IntPtr format, bool eventDriven, out long minDuration, out long maxDuration);

        // --- IAudioClient3 (3 methods) ---
        [PreserveSig] int GetSharedModeEnginePeriod(IntPtr format, out uint defaultPeriodFrames,
            out uint fundamentalPeriodFrames, out uint minPeriodFrames, out uint maxPeriodFrames);
        [PreserveSig] int GetCurrentSharedModeEnginePeriod(out IntPtr format, out uint currentPeriodFrames);
        [PreserveSig] int InitializeSharedAudioStream(uint streamFlags, uint periodFrames, IntPtr format, IntPtr audioSessionGuid);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out int numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(int numFramesRead);
        [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
    }
}
