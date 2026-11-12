// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using WinPlay.Core.Mirror;

namespace WinPlay.Capture;

/// <summary>
/// <see cref="IH264VideoSource"/> that mirrors the primary desktop: DXGI Desktop
/// Duplication → GPU BGRA → NV12 texture ring (color-convert + scale) → zero-copy
/// Media Foundation hardware H.264 (<c>IMFDXGIDeviceManager</c> + <c>MFCreateDXGISurfaceBuffer</c>)
/// → Annex-B access units.
///
/// Automatically adapts resolution and CBR bitrate limits to the receiver's hardware
/// model (<see cref="ConfigureReceiverProfile"/>) and adjusts encoder bitrate in real time
/// (<see cref="AdjustBitrate"/>) in response to network/receiver backpressure.
/// </summary>
public sealed class ScreenMirrorSource : IH264VideoSource
{
    private const int MinBitrateBps = 2_500_000;

    private readonly int _fps;
    private readonly int _bitrateOverride;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _receiverDisplayW;
    private int _receiverDisplayH;
    private string? _receiverModel;
    private int _maxAllowedBitrateBps = 8_000_000;
    private int _currentBitrateBps = 6_500_000;
    private IH264Encoder? _activeEncoder;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public int CurrentBitrateBps => Volatile.Read(ref _currentBitrateBps);

    public event Action<ReadOnlyMemory<byte>, bool>? FrameEncoded;
    public event Action<string>? Diagnostic;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);

    /// <param name="bitrateMbps">Encoder bitrate; 0 = auto (adapted to receiver model and resolution).</param>
    public ScreenMirrorSource(int fps = 60, int bitrateMbps = 0)
    {
        _fps = fps;
        _bitrateOverride = bitrateMbps * 1_000_000;
    }

    public void Configure(int receiverDisplayWidth, int receiverDisplayHeight)
    {
        _receiverDisplayW = receiverDisplayWidth;
        _receiverDisplayH = receiverDisplayHeight;
    }

    public void ConfigureReceiverProfile(string? receiverModel, int receiverDisplayWidth, int receiverDisplayHeight)
    {
        _receiverModel = receiverModel;
        _receiverDisplayW = receiverDisplayWidth;
        _receiverDisplayH = receiverDisplayHeight;
        _maxAllowedBitrateBps = DetermineReceiverMaxBitrate(receiverModel, receiverDisplayWidth, receiverDisplayHeight);
    }

    /// <summary>
    /// Selects a safe hardware CBR ceiling based on the Apple TV generation so older A8
    /// receivers (<c>AppleTV5,3</c>) never experience decoder VBV overflow during fast 3D games,
    /// while 4K Apple TV models (<c>AppleTV6,2</c>, <c>AppleTV11,1</c>, <c>AppleTV14,1</c>)
    /// can scale to higher bitrates.
    /// </summary>
    internal static int DetermineReceiverMaxBitrate(string? model, int dispW, int dispH)
    {
        if (!string.IsNullOrEmpty(model))
        {
            if (model.StartsWith("AppleTV3", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV5", StringComparison.OrdinalIgnoreCase))
            {
                return 6_000_000; // Apple TV HD (A8): 6.0 Mbps High Profile (CABAC) sports-safe 1080p60 ceiling
            }
            if (model.StartsWith("AppleTV6", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV11", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV14", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV15", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV16", StringComparison.OrdinalIgnoreCase))
            {
                return 14_000_000; // Apple TV 4K (A10X / A12 / A15+): 14 Mbps High Profile ceiling
            }
        }
        return (dispW > 1920 || dispH > 1080) ? 12_000_000 : 7_500_000;
    }

    public void AdjustBitrate(double scaleFactor)
    {
        if (_bitrateOverride > 0) return;
        int cur = Volatile.Read(ref _currentBitrateBps);
        int next = (int)Math.Clamp(Math.Round(cur * scaleFactor), MinBitrateBps, _maxAllowedBitrateBps);
        if (Math.Abs(next - cur) < 250_000) return;
        Volatile.Write(ref _currentBitrateBps, next);
        _activeEncoder?.UpdateBitrate(next);
    }

    public Task StartAsync(CancellationToken ct)
    {
        _thread = new Thread(() => CaptureLoop(ct))
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "WinPlay-Mirror",
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        return Task.CompletedTask;
    }

    public void RequestKeyframe() => _activeEncoder?.ForceKeyframe();

    private void CaptureLoop(CancellationToken ct)
    {
        timeBeginPeriod(1);
        DesktopDuplicator? capture = null;
        GpuColorConverter? converter = null;
        IH264Encoder? encoder = null;
        try
        {
            capture = new DesktopDuplicator();
            int srcW = capture.Width, srcH = capture.Height;

            (Width, Height) = NegotiateEncodeSize(srcW, srcH, _receiverDisplayW, _receiverDisplayH);
            int bitrate = _bitrateOverride > 0
                ? _bitrateOverride
                : Math.Min(AutoBitrate(Width, Height, _fps), _maxAllowedBitrateBps);
            Volatile.Write(ref _currentBitrateBps, bitrate);

            converter = new GpuColorConverter(capture.Device, srcW, srcH, Width, Height, _fps);
            encoder = CreateEncoder(capture.Device, Width, Height, _fps, bitrate);
            _activeEncoder = encoder;
            encoder.Encoded += au => FrameEncoded?.Invoke(au, H264.ContainsKeyframeAnnexB(au.Span));
            Diagnostic?.Invoke($"capture {srcW}x{srcH} on {capture.AdapterName} → encode {Width}x{Height} @ {_fps}fps, {bitrate / 1_000_000.0:F1} Mbps max {_maxAllowedBitrateBps / 1_000_000.0:F1} Mbps ({encoder.Name}, receiver={_receiverModel ?? "generic"})");

            byte[]? cpuFallbackBuffer = encoder.IsZeroCopy ? null : new byte[Width * Height * 3 / 2];
            ID3D11Texture2D? lastGpuTex = null;
            bool haveFrame = false;
            int ringSlot = 0;

            var sw = Stopwatch.StartNew();
            double frameIntervalMs = 1000.0 / _fps;
            double lastCaptureMs = -frameIntervalMs;
            double lastKeepaliveMs = 0.0;

            while (!_stopped && !ct.IsCancellationRequested)
            {
                double nowMs = sw.Elapsed.TotalMilliseconds;
                double waitMs = (lastCaptureMs + frameIntervalMs) - nowMs;
                if (waitMs > 1.5)
                {
                    Thread.Sleep((int)(waitMs - 1.0));
                    continue;
                }
                if (waitMs > 0.05)
                {
                    Thread.SpinWait(60);
                    continue;
                }

                // Cap capture rate at <= 60 Hz on 240 Hz panels, while catching 30 / 50 / 60 FPS
                // sports video frames within ~1 ms of DWM presentation without injecting fake
                // duplicate frames between video frames.
                bool capturedNew = capture.TryCaptureInto(converter, timeoutMs: haveFrame ? 0 : 16);

                if (capturedNew)
                {
                    double stampMs = sw.Elapsed.TotalMilliseconds;
                    // Advance smoothly when in lock-step with 60 Hz, reset on pauses.
                    lastCaptureMs = (stampMs - lastCaptureMs < frameIntervalMs * 2.0)
                        ? lastCaptureMs + frameIntervalMs
                        : stampMs;
                    lastKeepaliveMs = stampMs;
                    haveFrame = true;

                    if (encoder.IsZeroCopy)
                    {
                        lastGpuTex = converter.ConvertToGpuTexture(ringSlot++);
                        encoder.EncodeTexture(lastGpuTex);
                    }
                    else
                    {
                        converter.ConvertToCpuFallback(cpuFallbackBuffer!);
                        encoder.EncodeCpu(cpuFallbackBuffer!);
                    }
                }
                else if (haveFrame)
                {
                    double idleMs = sw.Elapsed.TotalMilliseconds - lastCaptureMs;
                    // During 30 FPS (33.3 ms) or 50 FPS (20.0 ms) sports streams, wait 1 ms for the
                    // real next video frame instead of polluting the encoder with duplicate frames.
                    // Only emit a lightweight P-skip keepalive (~8 Hz) once the screen is truly static (>= 48 ms).
                    if (idleMs >= 48.0 && (sw.Elapsed.TotalMilliseconds - lastKeepaliveMs) >= 120.0)
                    {
                        lastKeepaliveMs = sw.Elapsed.TotalMilliseconds;
                        if (encoder.IsZeroCopy && lastGpuTex is not null)
                        {
                            encoder.EncodeTexture(lastGpuTex);
                        }
                        else if (!encoder.IsZeroCopy)
                        {
                            encoder.EncodeCpu(cpuFallbackBuffer!);
                        }
                    }
                    Thread.Sleep(1);
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Diagnostic?.Invoke($"capture/encode error: {ex.Message}");
        }
        finally
        {
            _activeEncoder = null;
            encoder?.Dispose();
            converter?.Dispose();
            capture?.Dispose();
            timeEndPeriod(1);
        }
    }

    private IH264Encoder CreateEncoder(ID3D11Device device, int width, int height, int fps, int bitrate)
    {
        try
        {
            return new HardwareH264Encoder(device, width, height, fps, bitrate);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"hardware encoder unavailable ({ex.Message}); using software");
            return new MediaFoundationH264Encoder(width, height, fps, bitrate);
        }
    }

    internal static (int Width, int Height) NegotiateEncodeSize(int srcW, int srcH, int dispW, int dispH)
    {
        int targetW = dispW > 0 ? dispW : 1920;
        int targetH = dispH > 0 ? dispH : 1080;
        if (targetW > 1920 || targetH > 1080)
        {
            double scale = Math.Min(1920.0 / targetW, 1080.0 / targetH);
            targetW = (int)Math.Round(targetW * scale);
            targetH = (int)Math.Round(targetH * scale);
        }
        return (Math.Max(2, targetW & ~1), Math.Max(2, targetH & ~1));
    }

    internal static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * height * fps * 0.077);
        return (int)Math.Clamp(bits, 4_500_000, 16_000_000);
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;
        _thread?.Join(2000);
        return ValueTask.CompletedTask;
    }
}
