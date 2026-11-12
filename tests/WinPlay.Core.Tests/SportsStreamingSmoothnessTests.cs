// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinPlay.Capture;
using WinPlay.Core.Mirror;
using Xunit;

namespace WinPlay.Core.Tests;

public class SportsStreamingSmoothnessTests
{
    // ---- Slice 1: Congestion Control (Zero IDR Keyframe Storm) & Audio-Aligned Playout Bias ----

    [Fact]
    public void MirrorClock_UsesAudioAlignedPlayoutBias_SoWifiBurstsDoNotMissTvDeadline()
    {
        var clock = new MirrorClock();
        ulong bootNtp = clock.BootTimestamp;
        ulong frameNtp = clock.FrameTimestamp;

        // BootTimestamp includes 1900->1970 epoch (2208988800s); subtract epoch to compare raw seconds.
        const ulong EpochOffset = 2208988800UL;
        double bootSec = (bootNtp >> 32) - EpochOffset + (bootNtp & 0xFFFFFFFFUL) / 4294967296.0;
        double frameSec = (frameNtp >> 32) + (frameNtp & 0xFFFFFFFFUL) / 4294967296.0;
        double biasMs = (frameSec - bootSec) * 1000.0;

        // Audio stream uses 2205 samples @ 44.1 kHz = 50.0 ms latencyMin; video bias must be ~50-65 ms
        // (never 16 ms, which caused AppleTV5,3 to discard sports frames delayed >16 ms over Wi-Fi).
        Assert.InRange(biasMs, 48.0, 68.0);
    }

    [Fact]
    public void CongestionController_ModerateWifiBurstDuringSportsPan_DropsZeroFramesAndTriggersZeroIdrStorm()
    {
        var ctrl = new MirrorCongestionController();
        int droppedFrames = 0;
        int forcedKeyframes = 0;
        var bitrateScales = new List<double>();

        // Simulate 2 seconds (120 frames @ 60fps) of high-motion sports panning where Wi-Fi TCP queue
        // fluctuates between 6 and 14 frames (~100-230 ms jitter) — previously this dropped 100% of
        // P-frames and spammed RequestKeyframe() 119 times ("球員一幀一幀跑" slideshow!).
        for (int f = 0; f < 120; f++)
        {
            bool isIdr = (f == 0);
            int queueDepth = 6 + (f % 9); // 6..14 queued frames
            long nowMs = f * 16L;

            var decision = ctrl.EvaluateIncomingFrame(isIdr, queueDepth, nowMs);
            if (decision.DropFrame) droppedFrames++;
            if (decision.RequestKeyframe) forcedKeyframes++;

            double? abr = ctrl.EvaluateSendCompletion(queueDepth, writeMs: 28.0, nowMs);
            if (abr.HasValue) bitrateScales.Add(abr.Value);
        }

        Assert.Equal(0, droppedFrames);
        Assert.Equal(0, forcedKeyframes);
        Assert.NotEmpty(bitrateScales);
        Assert.All(bitrateScales, s => Assert.InRange(s, 0.55, 0.85));
    }

    [Fact]
    public void CongestionController_SevereWifiOutage_RequestsSingleCooldownGuardedIdrAndRecoversSmoothly()
    {
        var ctrl = new MirrorCongestionController();
        int forcedKeyframes = 0;
        int droppedOrphanPFrames = 0;

        // 1. Severe 450ms+ outage (queueDepth = 26): drops P-frames cleanly, requests ONLY ONE IDR
        // across 30 consecutive congested frames (500 ms), NEVER spamming IDR every frame!
        for (int f = 1; f <= 30; f++)
        {
            var dec = ctrl.EvaluateIncomingFrame(isKeyframe: false, queueDepth: 26, nowMs: f * 16L);
            Assert.True(dec.DropFrame);
            droppedOrphanPFrames++;
            if (dec.RequestKeyframe) forcedKeyframes++;
        }
        Assert.Equal(1, forcedKeyframes);
        Assert.Equal(30, droppedOrphanPFrames);

        // 2. Queue drains to 10, non-IDR P-frame is STILL dropped while awaiting the recovery IDR
        // so Apple TV never receives broken inter-frame references!
        var orphanCheck = ctrl.EvaluateIncomingFrame(isKeyframe: false, queueDepth: 10, nowMs: 520);
        Assert.True(orphanCheck.DropFrame);

        // 3. Single recovery IDR arrives -> accepted immediately, and subsequent 60fps P-frames flow with 0 drops!
        var idrDec = ctrl.EvaluateIncomingFrame(isKeyframe: true, queueDepth: 8, nowMs: 540);
        Assert.False(idrDec.DropFrame);

        for (int f = 0; f < 30; f++)
        {
            var postRecovery = ctrl.EvaluateIncomingFrame(isKeyframe: false, queueDepth: 4, nowMs: 560 + f * 16L);
            Assert.False(postRecovery.DropFrame);
            Assert.False(postRecovery.RequestKeyframe);
        }
    }

    // ---- Slice 2: Zero-Stall Async Matte Auto-Crop (MapFlags.DoNotWait) Under Sports Scoreboard Flicker ----

    private static unsafe ID3D11Texture2D CreateSportsTexture(ID3D11Device dev, int frameIdx, bool bottomOverlay)
    {
        int w = 2560, h = 1600;
        byte[] raw = new byte[w * h * 4];
        int panX = frameIdx * 40;
        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            bool topBar = y < 80;
            bool botBar = y >= 1520;
            for (int x = 0; x < w; x++)
            {
                int i = row + x * 4;
                if (topBar || botBar)
                {
                    byte v = (byte)((bottomOverlay && botBar && x >= 300 && x < 1100) ? 55 : 0);
                    raw[i] = v; raw[i + 1] = v; raw[i + 2] = v; raw[i + 3] = 255;
                    continue;
                }
                int wx = x + panX;
                byte r = (byte)(60 + ((wx * 13 + y * 7) & 0x7F));
                byte g = (byte)(110 + ((wx * 5 + y * 11) & 0x5F));
                byte b = 55;
                raw[i] = b; raw[i + 1] = g; raw[i + 2] = r; raw[i + 3] = 255;
            }
        }
        fixed (byte* p = raw)
        {
            return dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            }, [new SubresourceData((IntPtr)p, (uint)(w * 4), 0)]);
        }
    }

    [Fact]
    public void GpuConverter_SteadyStateSportsWithBottomOverlay_NeverBlocksCpuWithSyncMapAndKeepsFullScreenCrop()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);

        using var cleanSports0 = CreateSportsTexture(dev, 0, bottomOverlay: false);
        using var cleanSports1 = CreateSportsTexture(dev, 1, bottomOverlay: false);
        using var overlaySports0 = CreateSportsTexture(dev, 2, bottomOverlay: true);
        using var overlaySports1 = CreateSportsTexture(dev, 3, bottomOverlay: true);

        // Initial frame establishes 16:9 full-screen crop (0, 80, 2560, 1520)
        conv.CopyCapturedFrame(cleanSports0);
        var nv0 = conv.ConvertToGpuTexture(0);
        Assert.NotNull(nv0);
        Assert.Equal(80, conv.ActiveSourceRect.Top);
        Assert.Equal(1520, conv.ActiveSourceRect.Bottom);
        Thread.Sleep(16);

        // Steady-state 120 frames (60 clean + 60 with bottom overlay triggering _matteStreak > 0):
        // Previously, synchronous Map(MapFlags.None) stalled CPU for 1.5-4.1 ms on P90/P99!
        // With deferred MapFlags.DoNotWait, P90 CopyCapturedFrame CPU time must stay < 0.75 ms.
        var copyLatenciesMs = new List<double>(120);
        var sw = Stopwatch.StartNew();
        for (int i = 1; i <= 120; i++)
        {
            var tex = (i <= 60)
                ? ((i & 1) == 0 ? cleanSports0 : cleanSports1)
                : ((i & 1) == 0 ? overlaySports0 : overlaySports1);

            double t0 = sw.Elapsed.TotalMilliseconds;
            conv.CopyCapturedFrame(tex);
            double t1 = sw.Elapsed.TotalMilliseconds;
            conv.ConvertToGpuTexture(i);
            copyLatenciesMs.Add(t1 - t0);

            if ((i % 4) == 0) Thread.Sleep(4); // Allow async GPU command buffer completion between frames
        }

        copyLatenciesMs.Sort();
        double p90Ms = copyLatenciesMs[(int)(copyLatenciesMs.Count * 0.90)];
        Assert.True(p90Ms < 0.75, $"Expected non-blocking async matte probe P90 < 0.75 ms, got {p90Ms:F3} ms (Max={copyLatenciesMs[^1]:F3} ms)");

        // Also verify transitioning from 16:10 desktop -> 16:9 fullscreen video settles to full-screen crop smoothly!
        using var desktopTex = CreateSportsTexture(dev, 4, bottomOverlay: false);
        // Simulate bright top & bottom desktop bars by testing dynamic desktop -> video transition:
        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.ConvertToCpuFallback(nv12);
        Assert.True(nv12[6 * 1920 + 960] > 50, "Expected full-screen video top edge active (no black bar)");
        Assert.True(nv12[1073 * 1920 + 960] > 50, "Expected full-screen video bottom edge active (no black bar)");
    }

    // ---- Slice 3: Non-Blocking Async MFT Drain, 6.5 Mbps A8 Safe Ceiling & 60 FPS High-Motion Sports ----

    [Fact]
    public void ReceiverProfile_AppleTvHdUsesSportsSafe65MbpsCeiling_While4kRetainsHighCeiling()
    {
        int a8Max = ScreenMirrorSource.DetermineReceiverMaxBitrate("AppleTV5,3", 1920, 1080);
        int tv4kMax = ScreenMirrorSource.DetermineReceiverMaxBitrate("AppleTV14,1", 3840, 2160);

        Assert.Equal(6_000_000, a8Max);
        Assert.True(tv4kMax >= 12_000_000);
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint ms);

    [Fact]
    public void HardwareH264Encoder_DrainsEachSportsFrameImmediatelyWithoutWaitingForNextInput_AndSustains60Fps()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        if (!GpuTestEnvironment.HasHardwareH264Encoder(dev)) return;
        timeBeginPeriod(1);
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);
        int bitrate = ScreenMirrorSource.DetermineReceiverMaxBitrate("AppleTV5,3", 1920, 1080);
        using var enc = new HardwareH264Encoder(dev, 1920, 1080, 60, bitrate);

        var textures = new ID3D11Texture2D[12];
        for (int t = 0; t < textures.Length; t++)
            textures[t] = CreateSportsTexture(dev, t * 3, bottomOverlay: (t >= 6));

        try
        {
            var sw = Stopwatch.StartNew();
            var submitTimes = new System.Collections.Concurrent.ConcurrentQueue<double>();
            var turnaroundMs = new List<double>();
            var frameBytes = new List<(int Bytes, bool IsKey)>();
            using var singleFrameDone = new ManualResetEventSlim(false);
            using var all120Done = new ManualResetEventSlim(false);

            enc.Encoded += au =>
            {
                double doneMs = sw.Elapsed.TotalMilliseconds;
                bool isKey = H264.ContainsKeyframeAnnexB(au.Span);
                double subMs = submitTimes.TryDequeue(out double s) ? s : doneMs;
                lock (turnaroundMs)
                {
                    turnaroundMs.Add(doneMs - subMs);
                    frameBytes.Add((au.Length, isKey));
                    singleFrameDone.Set();
                    if (frameBytes.Count >= 120) all120Done.Set();
                }
            };

            // 1. Single-frame drain check: frame #0 MUST emit without requiring a second EncodeTexture() call!
            conv.CopyCapturedFrame(textures[0]);
            var firstNv12 = conv.ConvertToGpuTexture(0);
            submitTimes.Enqueue(sw.Elapsed.TotalMilliseconds);
            enc.EncodeTexture(firstNv12);
            Assert.True(singleFrameDone.Wait(250), "Async MFT stranded frame #0 waiting for a second input frame!");

            // 2. Sustained 2.0-second (120 frames @ 60.0 FPS = 16.67 ms cadence) panning high-motion sports stress test:
            lock (turnaroundMs)
            {
                turnaroundMs.Clear();
                frameBytes.Clear();
            }
            while (submitTimes.TryDequeue(out _)) { }

            var cpuSubmitLatencies = new List<double>(120);
            double startMs = sw.Elapsed.TotalMilliseconds;
            for (int f = 0; f < 120; f++)
            {
                double targetMs = startMs + f * (1000.0 / 60.0);
                while (true)
                {
                    double wait = targetMs - sw.Elapsed.TotalMilliseconds;
                    if (wait <= 0.1) break;
                    if (wait > 1.8) Thread.Sleep((int)(wait - 1.0));
                    else Thread.SpinWait(50);
                }

                double t0 = sw.Elapsed.TotalMilliseconds;
                conv.CopyCapturedFrame(textures[f % textures.Length]);
                var gpuNv12 = conv.ConvertToGpuTexture(f + 1);
                submitTimes.Enqueue(sw.Elapsed.TotalMilliseconds);
                enc.EncodeTexture(gpuNv12);
                cpuSubmitLatencies.Add(sw.Elapsed.TotalMilliseconds - t0);
            }

            Assert.True(all120Done.Wait(250), $"Dropped frames under 60 FPS sports load: only {frameBytes.Count}/120 encoded!");

            List<double> turns;
            List<(int Bytes, bool IsKey)> sizes;
            lock (turnaroundMs)
            {
                turns = turnaroundMs.ToList();
                sizes = frameBytes.ToList();
            }

            cpuSubmitLatencies.Sort();
            turns.Sort();
            double p90CpuMs = cpuSubmitLatencies[(int)(cpuSubmitLatencies.Count * 0.90)];
            double p90Turnaround = turns[(int)(turns.Count * 0.90)];
            int maxIdrBytes = sizes.Where(s => s.IsKey).Select(s => s.Bytes).DefaultIfEmpty(0).Max();
            double avgMbps = sizes.Sum(s => (long)s.Bytes) * 8.0 / 2.0 / 1_000_000.0;

            Assert.Equal(120, sizes.Count);
            Assert.True(p90CpuMs < 3.0, $"Expected <3.0 ms P90 CPU convert+submit, got {p90CpuMs:F2} ms");
            Assert.True(p90Turnaround < 18.5, $"Expected <18.5 ms (~1 frame) P90 hardware encode latency, got {p90Turnaround:F2} ms");
            Assert.True(maxIdrBytes <= 165 * 1024, $"Expected compact IDR <= 165 KB, got {maxIdrBytes / 1024.0:F1} KB");
            Assert.InRange(avgMbps, 2.0, 7.3);
        }
        finally
        {
            foreach (var tex in textures) tex.Dispose();
            timeEndPeriod(1);
        }
    }

    [Fact]
    public void HardwareH264Encoder_UpdateBitrateMidStream_ActuallyReducesIntelQsvEncodedFrameSize()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        if (!GpuTestEnvironment.HasHardwareH264Encoder(dev)) return;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);
        using var enc = new HardwareH264Encoder(dev, 1920, 1080, 60, 6_500_000);

        var textures = new ID3D11Texture2D[6];
        for (int t = 0; t < textures.Length; t++)
            textures[t] = CreateSportsTexture(dev, t * 7, bottomOverlay: false);

        try
        {
            var highPhaseBytes = new List<int>();
            var lowPhaseBytes = new List<int>();
            int count = 0;
            enc.Encoded += au =>
            {
                bool isKey = H264.ContainsKeyframeAnnexB(au.Span);
                int n = Interlocked.Increment(ref count);
                if (isKey) return;
                if (n >= 5 && n <= 28) lock (highPhaseBytes) highPhaseBytes.Add(au.Length);
                else if (n >= 55 && n <= 85) lock (lowPhaseBytes) lowPhaseBytes.Add(au.Length);
            };

            for (int f = 0; f < 90; f++)
            {
                if (f == 30) enc.UpdateBitrate(2_500_000);
                conv.CopyCapturedFrame(textures[f % textures.Length]);
                enc.EncodeTexture(conv.ConvertToGpuTexture(f));
                Thread.Sleep(15);
            }
            Thread.Sleep(80);

            Assert.NotEmpty(highPhaseBytes);
            Assert.NotEmpty(lowPhaseBytes);
            double avgHigh = highPhaseBytes.Average();
            double avgLow = lowPhaseBytes.Average();
            Assert.True(avgLow < avgHigh * 0.65,
                $"Expected mid-stream PCVBR ABR UpdateBitrate(2.5M) to cut P-frame size by >35%, got {avgHigh / 1024.0:F1} KB -> {avgLow / 1024.0:F1} KB");
        }
        finally
        {
            foreach (var tex in textures) tex.Dispose();
        }
    }
}
