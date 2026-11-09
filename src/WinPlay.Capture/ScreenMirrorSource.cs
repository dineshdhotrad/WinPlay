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
                return 8_000_000; // Apple TV HD (A8) and earlier: 8 Mbps safe CBR ceiling
            }
            if (model.StartsWith("AppleTV6", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV11", StringComparison.OrdinalIgnoreCase) ||
                model.StartsWith("AppleTV14", StringComparison.OrdinalIgnoreCase))
            {
                return 15_000_000; // Apple TV 4K (A10X / A12 / A15+): 15 Mbps ceiling
            }
        }
        return (dispW > 1920 || dispH > 1080) ? 14_000_000 : 8_000_000;
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
            double nextFrameMs = 0.0;

            while (!_stopped && !ct.IsCancellationRequested)
            {
                double nowMs = sw.Elapsed.TotalMilliseconds;
                double waitMs = nextFrameMs - nowMs;
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

                nextFrameMs += frameIntervalMs;
                if (nowMs - nextFrameMs > frameIntervalMs)
                    nextFrameMs = nowMs + frameIntervalMs;

                // Pace DXGI polling strictly at 60 Hz (never 240 times/sec on a 240 Hz panel).
                // Once streaming has started (haveFrame == true), poll non-blocking (timeoutMs: 0)
                // so DXGI immediately returns the latest accumulated desktop surface in <0.02 ms
                // without stalling the 60.0 Hz cadence on unchanged ticks.
                bool capturedNew = capture.TryCaptureInto(converter, timeoutMs: haveFrame ? 0 : 16);

                if (capturedNew)
                {
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
                    // Reuse the already-converted GPU NV12 texture at 60 Hz so the hardware MFT
                    // pipeline never holds the last active frame waiting for a subsequent input,
                    // and emit a tiny P-skip slice without re-running 2560x1600 VideoProcessorBlt.
                    if (encoder.IsZeroCopy && lastGpuTex is not null)
                    {
                        encoder.EncodeTexture(lastGpuTex);
                    }
                    else if (!encoder.IsZeroCopy)
                    {
                        encoder.EncodeCpu(cpuFallbackBuffer!);
                    }
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
        int maxW = dispW > 0 ? Math.Min(dispW, 1920) : 1920;
        int maxH = dispH > 0 ? Math.Min(dispH, 1080) : 1080;
        int w = srcW, h = srcH;
        if (srcW > maxW || srcH > maxH)
        {
            double scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
            w = (int)Math.Round(srcW * scale);
            h = (int)Math.Round(srcH * scale);
        }
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }

    internal static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * height * fps * 0.06);
        return (int)Math.Clamp(bits, 4_000_000, 12_000_000);
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;
        _thread?.Join(2000);
        return ValueTask.CompletedTask;
    }
}
