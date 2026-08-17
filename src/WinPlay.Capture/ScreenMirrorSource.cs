// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using WinPlay.Core.Mirror;

namespace WinPlay.Capture;

/// <summary>
/// <see cref="IH264VideoSource"/> that mirrors the primary desktop: DXGI Desktop
/// Duplication → GPU BGRA → NV12 (color-convert + scale) → Media Foundation H.264 →
/// Annex-B access units. The encode resolution is negotiated from the receiver's
/// advertised display size (never hardcoded), fit to the desktop aspect ratio. Runs the
/// capture/encode on a dedicated MTA thread paced to the target frame rate; when the
/// screen is unchanged it re-encodes the previous frame so the receiver's clock keeps
/// advancing (which the AirPlay pipeline requires).
/// </summary>
public sealed class ScreenMirrorSource : IH264VideoSource
{
    private readonly int _fps;
    private readonly int _bitrateOverride;
    private Thread? _thread;
    private volatile bool _stopped;
    private int _receiverDisplayW;
    private int _receiverDisplayH;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public event Action<ReadOnlyMemory<byte>, bool, long>? FrameEncoded;
    public event Action<string>? Diagnostic;

    /// <summary>
    /// Capture could not run at all, or has stopped for good. Distinct from
    /// <see cref="Diagnostic"/>, which is commentary nobody acts on.
    ///
    /// <para>Initialisation failure used to be reported as a diagnostic and the capture thread
    /// simply ended. The common trigger is real — Desktop Duplication reliably refuses to start
    /// over a Remote Desktop session — and the result was that the user asked to mirror their
    /// screen and absolutely nothing happened: no picture, no error, no retry, no fallback,
    /// indefinitely. A failure that stops the feature has to be a failure the session hears.</para>
    /// </summary>
    public event Action<Exception>? Failed;

    /// <param name="bitrateMbps">Encoder bitrate; 0 = auto (scaled to the negotiated resolution).</param>
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

    public void RequestKeyframe() { /* the MFT emits an IDR + SPS/PPS as its first output automatically */ }

    private void CaptureLoop(CancellationToken ct)
    {
        DesktopDuplicator? capture = null;
        GpuColorConverter? converter = null;
        IH264Encoder? encoder = null;
        try
        {
            capture = new DesktopDuplicator();
            int srcW = capture.Width, srcH = capture.Height;

            // Negotiate the encode size: fit the desktop into the receiver's advertised
            // display (or use the desktop size if none advertised). Even dimensions only.
            (Width, Height) = NegotiateEncodeSize(srcW, srcH, _receiverDisplayW, _receiverDisplayH);
            int bitrate = _bitrateOverride > 0 ? _bitrateOverride : AutoBitrate(Width, Height, _fps);

            // GPU color-convert + scale (BGRA → NV12) on the capture device — the heaviest
            // per-frame operation runs on the GPU (any vendor), keeping latency low.
            converter = new GpuColorConverter(capture.Device, srcW, srcH, Width, Height, _fps);

            // Prefer the GPU hardware encoder (NVENC/QuickSync/AMF/Adreno); fall back to
            // the software MFT only if no hardware encoder is present.
            encoder = CreateEncoder(Width, Height, _fps, bitrate);
            // Stamped here — the earliest point a capture-tick is available for this access unit —
            // so a consumer receiving this frame after some delay (a supervised capture-host child
            // relays it over a named pipe to its parent process) can still timestamp from the true
            // capture instant instead of from whenever it happens to observe the event. QPC is
            // machine-wide on Windows, so this is comparable across processes.
            encoder.Encoded += au => FrameEncoded?.Invoke(au, IsKeyframe(au), Stopwatch.GetTimestamp());
            Diagnostic?.Invoke($"capture {srcW}x{srcH} → encode {Width}x{Height} @ {_fps}fps, {bitrate / 1_000_000} Mbps ({encoder.Name})");

            byte[] nv12 = new byte[Width * Height * 3 / 2]; // NV12 = Y (w*h) + interleaved UV (w*h/2)
            bool haveFrame = false;

            var sw = Stopwatch.StartNew();
            long frameIndex = 0;
            double frameMs = 1000.0 / _fps;

            while (!_stopped && !ct.IsCancellationRequested)
            {
                double dueMs = frameIndex * frameMs;
                double nowMs = sw.Elapsed.TotalMilliseconds;
                if (nowMs < dueMs)
                {
                    Thread.Sleep(Math.Max(1, (int)(dueMs - nowMs)));
                    continue;
                }

                FrameStatus status = capture.TryCaptureInto(converter.InputTexture, timeoutMs: 12);
                if (status == FrameStatus.Captured)
                {
                    converter.Convert(nv12);
                    haveFrame = true;
                }
                else if (status is FrameStatus.AccessLost or FrameStatus.DeviceLost)
                {
                    // The desktop duplication was revoked (mode change, secure desktop, GPU
                    // reset). Recover in place instead of tearing down the mirror; recovery may
                    // resize the NV12 buffer if the desktop resolution changed.
                    if (!RecoverCapture(ref capture, ref converter, ref encoder, ref nv12, ct))
                        break; // cancelled during recovery
                    if (capture is null || converter is null || encoder is null)
                        break; // unreachable on success; keeps null-flow analysis exact
                    haveFrame = false;
                    frameIndex++;
                    continue;
                }

                if (!haveFrame) { frameIndex++; continue; } // nothing captured yet

                encoder.Encode(nv12);
                frameIndex++;
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                Diagnostic?.Invoke($"capture/encode error: {ex.Message}");
                // This thread IS the capture. Reaching here means no more frames will ever be
                // produced, so the session has to be told rather than left waiting for a picture
                // that is not coming.
                Failed?.Invoke(ex);
            }
        }
        finally
        {
            encoder?.Dispose();
            converter?.Dispose();
            capture?.Dispose();
        }
    }

    /// <summary>
    /// Recovers desktop capture after it was lost, retrying with capped exponential backoff
    /// until it succeeds or the stream is cancelled. Re-acquires the duplication cheaply when
    /// the device is still alive; rebuilds the D3D device when it was removed; and, if the
    /// desktop resolution changed, rebuilds the color converter (and the encoder + NV12 buffer
    /// when the negotiated encode size changed). Returns false only if cancelled while recovering.
    /// </summary>
    /// <summary>
    /// How many times to try re-acquiring the desktop before declaring capture dead. Paired with
    /// the capped backoff this is roughly a minute — long enough to outlast a driver reset or a
    /// UAC prompt, short enough that a permanently broken capture path stops pretending.
    /// </summary>
    private const int MaxRecoveryAttempts = 40;

    private bool RecoverCapture(ref DesktopDuplicator? capture, ref GpuColorConverter? converter,
        ref IH264Encoder? encoder, ref byte[] nv12, CancellationToken ct)
    {
        Diagnostic?.Invoke("desktop capture lost (mode change / secure desktop / GPU reset); recovering…");
        int backoffMs = 0;
        int attempts = 0;
        while (!_stopped && !ct.IsCancellationRequested)
        {
            // Recovery is bounded. Losing the desktop is normally transient — a mode change, the
            // secure desktop, a driver reset — and worth waiting out. But a genuinely dead capture
            // path retried forever, silently, while the session went on reporting a live mirror
            // and the TV showed a frozen frame indefinitely. At the capped backoff this is a bit
            // over a minute of trying before admitting it is not coming back.
            if (++attempts > MaxRecoveryAttempts)
            {
                var dead = new InvalidOperationException(
                    $"desktop capture could not be recovered after {MaxRecoveryAttempts} attempts");
                Diagnostic?.Invoke(dead.Message);
                Failed?.Invoke(dead);
                return false;
            }

            try
            {
                int prevSrcW = capture?.Width ?? -1, prevSrcH = capture?.Height ?? -1;

                // Cheap path: re-acquire the duplication on the existing device. If that fails
                // the device is gone (or the secure desktop is still up) — rebuild it wholesale.
                bool sameDevice = capture is not null && capture.TryRecreateDuplication();
                if (!sameDevice)
                {
                    converter?.Dispose(); converter = null;
                    capture?.Dispose(); capture = null;
                    capture = new DesktopDuplicator();
                }

                // Rebuild the converter when the device changed or the desktop was resized.
                if (converter is null || capture!.Width != prevSrcW || capture.Height != prevSrcH)
                {
                    var (newW, newH) = NegotiateEncodeSize(capture!.Width, capture.Height, _receiverDisplayW, _receiverDisplayH);
                    converter?.Dispose();
                    converter = new GpuColorConverter(capture.Device, capture.Width, capture.Height, newW, newH, _fps);

                    // Rebuild the encoder and NV12 buffer only if the negotiated encode size changed.
                    if (newW != Width || newH != Height)
                    {
                        Width = newW;
                        Height = newH;
                        nv12 = new byte[Width * Height * 3 / 2];
                        int bitrate = _bitrateOverride > 0 ? _bitrateOverride : AutoBitrate(Width, Height, _fps);
                        IH264Encoder? old = encoder;
                        encoder = CreateEncoder(Width, Height, _fps, bitrate);
                        // Stamped here — the earliest point a capture-tick is available for this access unit —
            // so a consumer receiving this frame after some delay (a supervised capture-host child
            // relays it over a named pipe to its parent process) can still timestamp from the true
            // capture instant instead of from whenever it happens to observe the event. QPC is
            // machine-wide on Windows, so this is comparable across processes.
            encoder.Encoded += au => FrameEncoded?.Invoke(au, IsKeyframe(au), Stopwatch.GetTimestamp());
                        old?.Dispose();
                    }
                }

                Diagnostic?.Invoke($"desktop capture recovered ({capture!.Width}x{capture.Height} → {Width}x{Height})");
                return true;
            }
            catch (Exception ex)
            {
                backoffMs = NextBackoffMs(backoffMs);
                Diagnostic?.Invoke($"capture recovery retry in {backoffMs}ms: {ex.Message}");
                if (ct.WaitHandle.WaitOne(backoffMs)) return false; // cancelled during backoff
            }
        }
        return false;
    }

    /// <summary>Capped exponential backoff: 50 ms, then doubling to a 1 s ceiling.</summary>
    internal static int NextBackoffMs(int current) => current <= 0 ? 50 : Math.Min(current * 2, 1000);

    private IH264Encoder CreateEncoder(int width, int height, int fps, int bitrate)
    {
        try
        {
            return new HardwareH264Encoder(width, height, fps, bitrate);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"hardware encoder unavailable ({ex.Message}); using software");
            return new MediaFoundationH264Encoder(width, height, fps, bitrate);
        }
    }

    private static bool IsKeyframe(byte[] annexB) => H264.ContainsKeyframe(H264.SplitAnnexB(annexB));

    /// <summary>
    /// Fits the desktop into the receiver's display (if advertised), preserving aspect
    /// ratio; both dimensions rounded down to even (NV12 requires even width and height).
    /// No fixed cap — the resolution comes from what the receiver reports it can present.
    /// </summary>
    internal static (int Width, int Height) NegotiateEncodeSize(int srcW, int srcH, int dispW, int dispH)
    {
        int w = srcW, h = srcH;
        if (dispW > 0 && dispH > 0 && (srcW > dispW || srcH > dispH))
        {
            double scale = Math.Min((double)dispW / srcW, (double)dispH / srcH);
            w = (int)Math.Round(srcW * scale);
            h = (int)Math.Round(srcH * scale);
        }
        return (Math.Max(2, w & ~1), Math.Max(2, h & ~1));
    }

    /// <summary>Bitrate scaled to resolution + frame rate (~0.10 bits/pixel/frame), clamped to a sane range.</summary>
    internal static int AutoBitrate(int width, int height, int fps)
    {
        long bits = (long)(width * height * fps * 0.10);
        return (int)Math.Clamp(bits, 4_000_000, 60_000_000);
    }

    public ValueTask DisposeAsync()
    {
        _stopped = true;
        _thread?.Join(2000);
        return ValueTask.CompletedTask;
    }
}
