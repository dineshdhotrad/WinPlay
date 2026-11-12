// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using WinPlay.Capture;
using WinPlay.Core.Mirror;
using Xunit;

namespace WinPlay.Core.Tests;

internal static class GpuTestEnvironment
{
    public static bool TryCreateHardwareVideoDevice(out ID3D11Device? device)
    {
        device = null;
        try
        {
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            var hr = D3D11.D3D11CreateDevice(null, DriverType.Hardware,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
                out ID3D11Device? dev);
            if (hr.Failure || dev is null) return false;

            using var dxgi = dev.QueryInterfaceOrNull<IDXGIDevice>();
            using var adapter = dxgi?.GetAdapter();
            // Headless CI VMs expose only Microsoft Basic Render Driver (VendorId 0x1414) without hardware video silicon.
            if (adapter is null || adapter.Description.VendorId == 0x1414)
            {
                dev.Dispose();
                return false;
            }
            using var vid = dev.QueryInterfaceOrNull<ID3D11VideoDevice>();
            if (vid is null)
            {
                dev.Dispose();
                return false;
            }
            device = dev;
            return true;
        }
        catch
        {
            device?.Dispose();
            device = null;
            return false;
        }
    }

    public static bool HasHardwareH264Encoder(ID3D11Device device)
    {
        try
        {
            using var probeEnc = new HardwareH264Encoder(device, 1920, 1080, 60, 6_000_000);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool CanDuplicateInteractiveDesktop()
    {
        try
        {
            using var dup = new DesktopDuplicator();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public class MirrorFullScreenAndQualityTests
{
    // ---- Seam 1: Receiver /info display size fallback & full-screen CodecHeader ----

    [Fact]
    public void ParseDisplaySize_FallsBackToReceiverHdrCapability_WhenDisplaysArrayOmittedByTvOs()
    {
        // Real payload shape observed on AppleTV5,3 running tvOS 23L781 ("displays" omitted).
        var info1080 = new Dictionary<string, object?>
        {
            ["name"] = "Living Room",
            ["model"] = "AppleTV5,3",
            ["osBuildVersion"] = "23L781",
            ["receiverHDRCapability"] = "1080p60",
        };
        Assert.Equal((1920, 1080), MirrorSession.ParseDisplaySize(info1080));

        var info4k = new Dictionary<string, object?>
        {
            ["name"] = "Bedroom",
            ["model"] = "AppleTV14,1",
            ["receiverHDRCapability"] = "4k60-dolbyvision",
        };
        Assert.Equal((3840, 2160), MirrorSession.ParseDisplaySize(info4k));

        var info720 = new Dictionary<string, object?>
        {
            ["model"] = "AppleTV3,2",
            ["receiverHDRCapability"] = "720p60",
        };
        Assert.Equal((1280, 720), MirrorSession.ParseDisplaySize(info720));
    }

    [Fact]
    public void ParseDisplaySize_DefaultsToFullScreenTvResolution_WhenNeitherDisplaysNorHdrCapabilityPresent()
    {
        var hdAppleTv = new Dictionary<string, object?> { ["model"] = "AppleTV5,3" };
        Assert.Equal((1920, 1080), MirrorSession.ParseDisplaySize(hdAppleTv));

        var uhdAppleTv = new Dictionary<string, object?> { ["model"] = "AppleTV14,1" };
        Assert.Equal((3840, 2160), MirrorSession.ParseDisplaySize(uhdAppleTv));

        var genericTv = new Dictionary<string, object?>();
        Assert.Equal((1920, 1080), MirrorSession.ParseDisplaySize(genericTv));
    }

    [Fact]
    public void ParseDisplaySize_PrefersExplicitDisplaysEntry_WhenPresent()
    {
        var infoWithDisplays = new Dictionary<string, object?>
        {
            ["model"] = "AppleTV14,1",
            ["receiverHDRCapability"] = "1080p60",
            ["displays"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["widthPixels"] = 3840L,
                    ["heightPixels"] = 2160L,
                },
            },
        };
        Assert.Equal((3840, 2160), MirrorSession.ParseDisplaySize(infoWithDisplays));
    }

    // ---- Seam 2: Full-screen 16:9 encode canvas negotiation across all PC / game / video sizes ----

    [Theory]
    [InlineData(2560, 1600, 1920, 1080, 1920, 1080)] // 16:10 laptop -> 1080p Apple TV
    [InlineData(2560, 1600, 0,    0,    1920, 1080)] // 16:10 laptop -> unadvertised TV
    [InlineData(2560, 1600, 3840, 2160, 1920, 1080)] // 16:10 laptop -> 4K Apple TV (2x integer upscale)
    [InlineData(1920, 1200, 1920, 1080, 1920, 1080)] // 16:10 FHD+ -> 1080p Apple TV
    [InlineData(1920, 1080, 1920, 1080, 1920, 1080)] // 16:9 FHD -> 1080p Apple TV
    [InlineData(2560, 1440, 1920, 1080, 1920, 1080)] // 16:9 QHD game -> 1080p Apple TV
    [InlineData(3840, 2160, 3840, 2160, 1920, 1080)] // 16:9 4K -> 4K Apple TV
    [InlineData(1280, 720,  1920, 1080, 1920, 1080)] // 720p game/video -> 1080p Apple TV (never stuck at 720p corner!)
    [InlineData(1366, 768,  1920, 1080, 1920, 1080)] // 1366x768 laptop -> 1080p Apple TV
    [InlineData(3440, 1440, 1920, 1080, 1920, 1080)] // 21:9 ultrawide -> 1080p Apple TV
    [InlineData(2880, 1920, 1920, 1080, 1920, 1080)] // 3:2 Surface laptop -> 1080p Apple TV
    [InlineData(1920, 1080, 1280, 720,  1280, 720)]  // 720p receiver -> 1280x720
    public void NegotiateEncodeSize_AlwaysMatchesFullScreenTvCanvas(
        int srcW, int srcH, int dispW, int dispH, int expectedW, int expectedH)
    {
        var (w, h) = ScreenMirrorSource.NegotiateEncodeSize(srcW, srcH, dispW, dispH);
        Assert.Equal((expectedW, expectedH), (w, h));
    }

    // ---- Seam 3: Real GPU GpuColorConverter Full-Screen Aspect, Video Auto-Crop & HDR Resilience ----

    private static unsafe ID3D11Texture2D CreateBgraTexture(
        ID3D11Device dev, int w, int h, Format fmt, Func<int, int, (byte B, byte G, byte R, byte A)> pixelFn)
    {
        byte[] raw = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            int rowOff = y * w * 4;
            for (int x = 0; x < w; x++)
            {
                var (b, g, r, a) = pixelFn(x, y);
                int i = rowOff + x * 4;
                raw[i] = b; raw[i + 1] = g; raw[i + 2] = r; raw[i + 3] = a;
            }
        }
        fixed (byte* ptr = raw)
        {
            var init = new SubresourceData((IntPtr)ptr, (uint)(w * 4), 0);
            return dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = fmt,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            }, [init]);
        }
    }

    [Fact]
    public void GpuConverter_16By10LaptopFullscreen16By9Video_AutoCropsBlackBarsToExact16By9FullScreen()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);

        // Simulate watching a 16:9 fullscreen video (2560x1440) on a 2560x1600 (16:10) laptop:
        // top 80px and bottom 80px are black letterbox bars; center has a 400x400 white square.
        using var videoFrame = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (y < 80 || y >= 1520) return (0, 0, 0, 255);
            if (x >= 1080 && x < 1480 && y >= 600 && y < 1000) return (245, 245, 245, 255);
            return (180, 95, 45, 255); // active blue-teal video background
        });

        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(videoFrame);
        conv.ConvertToCpuFallback(nv12);

        byte Luma(int x, int y) => nv12[y * 1920 + x];

        // 1. All 4 edges of the 1920x1080 TV output MUST be active video (zero black bars!)
        Assert.True(Luma(960, 6) > 60, $"Top edge was black (Y={Luma(960, 6)}), expected letterbox bar cropped");
        Assert.True(Luma(960, 1073) > 60, $"Bottom edge was black (Y={Luma(960, 1073)}), expected letterbox bar cropped");
        Assert.True(Luma(6, 540) > 60, $"Left edge was black (Y={Luma(6, 540)})");
        Assert.True(Luma(1913, 540) > 60, $"Right edge was black (Y={Luma(1913, 540)})");

        // 2. The 400x400 square inside 2560x1440 scaled 0.75x to 1920x1080 MUST be 300x300 (1:1 undistorted!)
        int whiteW = Enumerable.Range(0, 1920).Count(x => Luma(x, 540) > 200);
        int whiteH = Enumerable.Range(0, 1080).Count(y => Luma(960, y) > 200);
        Assert.InRange(whiteW, 296, 304);
        Assert.InRange(whiteH, 296, 304);
    }

    [Fact]
    public void GpuConverter_16By10DesktopWithTaskbarAndTabs_FillsFullScreen1080pWithoutCroppingTaskbar()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);

        using var desktopFrame = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (y < 60) return (235, 225, 220, 255);     // bright browser tab bar
            if (y >= 1528) return (215, 195, 180, 255);  // bright Windows taskbar
            return (95, 80, 70, 255);                    // desktop workspace
        });

        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(desktopFrame);
        conv.ConvertToCpuFallback(nv12);

        byte Luma(int x, int y) => nv12[y * 1920 + x];

        // Full-bleed horizontally AND preserves both top tab bar and bottom taskbar!
        Assert.True(Luma(6, 540) > 55, $"Left edge was black (Y={Luma(6, 540)})");
        Assert.True(Luma(1913, 540) > 55, $"Right edge was black (Y={Luma(1913, 540)})");
        Assert.True(Luma(960, 10) > 180, $"Top browser tab bar was cropped (Y={Luma(960, 10)})");
        Assert.True(Luma(960, 1068) > 160, $"Bottom taskbar was cropped (Y={Luma(960, 1068)})");
    }

    [Fact]
    public void GpuConverter_16By10StudioBlack161616WebVideo_AutoCropsToFullScreen()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);

        // Many web players (e.g. HTML5 <video> with studio-swing CSS/decoder) render letterbox bars at RGB(16,16,16) (#101010).
        using var studioVideo = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (y < 80 || y >= 1520) return (16, 16, 16, 255);
            return (150, 110, 75, 255);
        });

        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(studioVideo);
        conv.ConvertToCpuFallback(nv12);

        Assert.True(nv12[6 * 1920 + 960] > 75, $"Studio-black top bar was NOT cropped (Y={nv12[6 * 1920 + 960]})");
        Assert.True(nv12[1073 * 1920 + 960] > 75, $"Studio-black bottom bar was NOT cropped (Y={nv12[1073 * 1920 + 960]})");
    }

    [Fact]
    public void GpuConverter_16By10DarkModeDesktopWithTaskbarIcons_PreservesTabsAndTaskbar()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);

        // Windows 11 dark-mode desktop: RGB(32,32,36) browser strip + RGB(28,28,32) taskbar with centered app icons & clock.
        using var darkDesktop = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (y < 76)
            {
                // Dark tab strip with tab text & window controls
                if ((x >= 120 && x < 700 && y >= 20 && y < 44 && x % 16 < 8) || x > 2420)
                    return (210, 210, 215, 255);
                return (36, 33, 32, 255);
            }
            if (y >= 1528)
            {
                // Dark taskbar with centered icons (x=1050..1510) and right tray clock (x=2360..2530)
                bool isIcon = (x >= 1050 && x < 1510 && (x % 64) >= 12 && (x % 64) < 48 && y >= 1542 && y < 1584);
                bool isClock = (x >= 2360 && x < 2530 && y >= 1544 && y < 1582);
                if (isIcon || isClock) return (220, 180, 110, 255);
                return (32, 28, 28, 255);
            }
            return (55, 50, 48, 255);
        });

        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(darkDesktop);
        conv.ConvertToCpuFallback(nv12);

        // ActiveSourceRect must remain uncropped (0, 0, 2560, 1600) so taskbar & tabs stay visible!
        Assert.Equal(0, conv.ActiveSourceRect.Top);
        Assert.Equal(1600, conv.ActiveSourceRect.Bottom);
    }

    [Fact]
    public void GpuConverter_21By9UltrawideFullscreen16By9Video_AutoCropsLeftRightPillarboxBarsToFullScreen()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 3440, 1440, 1920, 1080, 60);

        using var ultrawideVideo = CreateBgraTexture(dev, 3440, 1440, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (x < 440 || x >= 3000) return (0, 0, 0, 255); // left/right pillarbox bars around 2560x1440
            if (x >= 1520 && x < 1920 && y >= 520 && y < 920) return (245, 245, 245, 255);
            return (90, 145, 55, 255);
        });

        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(ultrawideVideo);
        conv.ConvertToCpuFallback(nv12);

        byte Luma(int x, int y) => nv12[y * 1920 + x];
        Assert.True(Luma(6, 540) > 70, $"Left pillarbox bar remained (Y={Luma(6, 540)})");
        Assert.True(Luma(1913, 540) > 70, $"Right pillarbox bar remained (Y={Luma(1913, 540)})");
        int whiteW = Enumerable.Range(0, 1920).Count(x => Luma(x, 540) > 200);
        int whiteH = Enumerable.Range(0, 1080).Count(y => Luma(960, y) > 200);
        Assert.InRange(whiteW, 296, 304);
        Assert.InRange(whiteH, 296, 304);
    }

    [Fact]
    public void GpuConverter_DynamicModeSwitchSequence_StaysFullScreenAcrossGameVideoAndDesktop()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);
        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];

        (int W, int H, bool Letterbox)[] steps =
        [
            (2560, 1600, false), // 16:10 desktop
            (1920, 1080, false), // 1080p fullscreen game
            (1280, 720,  false), // 720p game
            (2560, 1600, true),  // 16:10 laptop playing fullscreen 16:9 video
            (2560, 1600, false), // exit fullscreen back to 16:10 desktop
        ];

        foreach (var (w, h, lb) in steps)
        {
            using var tex = CreateBgraTexture(dev, w, h, Format.B8G8R8A8_UNorm, (x, y) =>
            {
                if (lb && (y < 80 || y >= 1520)) return (0, 0, 0, 255);
                return (140, 130, 120, 255);
            });
            // Feed 3 frames so any hysteresis settles cleanly across mode transitions
            for (int f = 0; f < 3; f++) conv.CopyCapturedFrame(tex);
            conv.ConvertToCpuFallback(nv12);

            Assert.True(nv12[6 * 1920 + 960] > 80, $"Top edge black on {w}x{h} lb={lb}");
            Assert.True(nv12[1073 * 1920 + 960] > 80, $"Bottom edge black on {w}x{h} lb={lb}");
            Assert.True(nv12[540 * 1920 + 6] > 80, $"Left edge black on {w}x{h} lb={lb}");
            Assert.True(nv12[540 * 1920 + 1913] > 80, $"Right edge black on {w}x{h} lb={lb}");
        }
    }

    [Fact]
    public unsafe void GpuConverter_AllFullscreenSwapchainFormatsIncludingFp16Hdr_SucceedWithoutDisposingConverter()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);
        byte[] nv12 = new byte[1920 * 1080 * 3 / 2];

        // 1. sRGB swapchain
        using (var srgb = CreateBgraTexture(dev, 1920, 1080, Format.B8G8R8A8_UNorm_SRgb, (_, _) => (120, 140, 160, 255)))
        {
            conv.CopyCapturedFrame(srgb);
            conv.ConvertToCpuFallback(nv12);
            Assert.True(nv12[540 * 1920 + 960] > 90);
        }

        // 2. FP16 scRGB HDR fullscreen swapchain (Format.R16G16B16A16_Float)
        int w = 1920, h = 1080;
        ushort[] fp16 = new ushort[w * h * 4];
        ushort halfMid = BitConverter.HalfToUInt16Bits((Half)0.35f);
        ushort halfHdr = BitConverter.HalfToUInt16Bits((Half)3.5f); // 280-nit HDR highlight
        ushort halfOne = BitConverter.HalfToUInt16Bits((Half)1.0f);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                ushort v = x < w / 2 ? halfMid : halfHdr;
                fp16[i] = v; fp16[i + 1] = v; fp16[i + 2] = v; fp16[i + 3] = halfOne;
            }
        }
        fixed (ushort* pFp16 = fp16)
        {
            var init = new SubresourceData((IntPtr)pFp16, (uint)(w * 8), 0);
            using var hdrTex = dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget, // Deliberately omit ShaderResource like some DXGI desktop surfaces!
            }, [init]);

            conv.CopyCapturedFrame(hdrTex);
            conv.ConvertToCpuFallback(nv12);

            byte midLuma = nv12[540 * 1920 + 400];
            byte hdrLuma = nv12[540 * 1920 + 1500];
            Assert.InRange(midLuma, (byte)110, (byte)195);
            Assert.True(hdrLuma > midLuma && hdrLuma >= 210, $"Expected HDR highlight > midLuma ({midLuma}), got {hdrLuma}");
        }

        // 3. Subsequent SDR frame still works (converter was never corrupted/disposed!)
        using (var sdr = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (_, _) => (100, 120, 140, 255)))
        {
            conv.CopyCapturedFrame(sdr);
            conv.ConvertToCpuFallback(nv12);
            Assert.True(nv12[540 * 1920 + 960] > 80);
        }
    }

    // ---- Seam 4: Hardware H.264 High Profile Quality (PSNR) & Live 1920x1080 Pipeline ----

    private static byte[] DecodeH264ToNv12(IReadOnlyList<byte[]> annexBAccessUnits, int width, int height)
    {
        Guid clsidH264Decoder = new("62CE7E72-4C71-4D20-B15D-452831A87D9D");
        Guid mfMtMajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        Guid mfMtSubtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        Guid mfMtFrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        Guid mfMediaTypeVideo = new("73646976-0000-0010-8000-00aa00389b71");
        Guid mfVideoFormatH264 = new("34363248-0000-0010-8000-00aa00389b71");
        Guid mfVideoFormatNv12 = new("3231564e-0000-0010-8000-00aa00389b71");
        ulong Pack(int hi, int lo) => ((ulong)(uint)hi << 32) | (uint)lo;

        MediaFactory.MFStartup(false).CheckError();
        object comObj = Activator.CreateInstance(Type.GetTypeFromCLSID(clsidH264Decoder)!)!;
        IntPtr unk = Marshal.GetIUnknownForObject(comObj);
        using var decoder = new IMFTransform(unk);

        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(mfMtMajorType, mfMediaTypeVideo);
            inType.Set(mfMtSubtype, mfVideoFormatH264);
            inType.Set(mfMtFrameSize, Pack(width, height));
            decoder.SetInputType(0, inType, 0);
        }

        SelectNv12OutputType(decoder, mfMtSubtype, mfVideoFormatNv12);
        decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);
        decoder.ProcessMessage(TMessageType.MessageNotifyStartOfStream, UIntPtr.Zero);

        byte[]? lastDecodedNv12 = null;
        int outStride = width;
        int outPaneH = height;

        void DrainDecoder()
        {
            while (true)
            {
                var info = decoder.GetOutputStreamInfo(0);
                bool provides = (info.Flags & 0x300) != 0;
                using var outBuf = provides ? null : MediaFactory.MFCreateMemoryBuffer(Math.Max(info.Size, width * height * 2));
                using var outSample = provides ? null : MediaFactory.MFCreateSample();
                if (outSample is not null && outBuf is not null) outSample.AddBuffer(outBuf);

                var db = new OutputDataBuffer { StreamID = 0, Sample = outSample! };
                var hr = decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref db, out _);
                if (hr.Code == unchecked((int)0xC00D6D61)) // MF_E_TRANSFORM_STREAM_CHANGE
                {
                    SelectNv12OutputType(decoder, mfMtSubtype, mfVideoFormatNv12);
                    using var curOut = decoder.GetOutputCurrentType(0);
                    ulong fs = curOut.GetUInt64(mfMtFrameSize);
                    outStride = (int)(fs >> 32);
                    outPaneH = (int)(fs & 0xFFFFFFFF);
                    continue;
                }
                if (hr == Vortice.MediaFoundation.ResultCode.TransformNeedMoreInput || hr.Failure) break;

                var sample = db.Sample;
                if (sample is null) break;
                using var contig = sample.ConvertToContiguousBuffer();
                contig.Lock(out IntPtr ptr, out _, out int curLen);
                try
                {
                    byte[] raw = new byte[curLen];
                    Marshal.Copy(ptr, raw, 0, curLen);
                    byte[] tightLuma = new byte[width * height];
                    int stride = Math.Max(width, outStride);
                    for (int y = 0; y < height; y++)
                        Buffer.BlockCopy(raw, y * stride, tightLuma, y * width, width);
                    lastDecodedNv12 = tightLuma;
                }
                finally
                {
                    contig.Unlock();
                    if (provides) sample.Dispose();
                }
            }
        }

        long pts = 0;
        foreach (byte[] au in annexBAccessUnits)
        {
            using var mb = MediaFactory.MFCreateMemoryBuffer(au.Length);
            mb.Lock(out IntPtr p, out _, out _);
            Marshal.Copy(au, 0, p, au.Length);
            mb.Unlock();
            mb.CurrentLength = au.Length;
            using var s = MediaFactory.MFCreateSample();
            s.AddBuffer(mb);
            s.SampleTime = pts;
            s.SampleDuration = 166_666;
            pts += 166_666;
            decoder.ProcessInput(0, s, 0);
            DrainDecoder();
        }
        decoder.ProcessMessage(TMessageType.MessageCommandDrain, UIntPtr.Zero);
        DrainDecoder();

        Assert.NotNull(lastDecodedNv12);
        return lastDecodedNv12!;
    }

    private static void SelectNv12OutputType(IMFTransform decoder, Guid mfMtSubtype, Guid mfVideoFormatNv12)
    {
        for (int i = 0; i < 32; i++)
        {
            using var avail = decoder.GetOutputAvailableType(0, i);
            if (avail.GetGUID(mfMtSubtype) == mfVideoFormatNv12)
            {
                decoder.SetOutputType(0, avail, 0);
                return;
            }
        }
    }

    [Fact]
    public void HardwareH264Encoder_EmitsHighProfile1080p60_DecodesFullScreenWithHighPsnrAndLowLatency()
    {
        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using var dev = hwDev!;
        if (!GpuTestEnvironment.HasHardwareH264Encoder(dev)) return;
        using var conv = new GpuColorConverter(dev, 2560, 1600, 1920, 1080, 60);
        using var enc = new HardwareH264Encoder(dev, 1920, 1080, 60, 9_500_000);

        var encodedUnits = new List<byte[]>();
        using var gotEnough = new ManualResetEventSlim(false);
        enc.Encoded += au =>
        {
            lock (encodedUnits)
            {
                encodedUnits.Add(au.ToArray());
                if (encodedUnits.Count >= 12) gotEnough.Set();
            }
        };

        // Detailed 2560x1600 fullscreen 16:9 video frame (top/bottom 80px black bars + court lines + gradients)
        using var videoTex = CreateBgraTexture(dev, 2560, 1600, Format.B8G8R8A8_UNorm, (x, y) =>
        {
            if (y < 80 || y >= 1520) return (0, 0, 0, 255);
            bool courtLine = (x % 320 < 6) || ((y - 80) % 240 < 6);
            if (courtLine) return (235, 240, 245, 255);
            byte r = (byte)(60 + (x * 140 / 2560));
            byte g = (byte)(80 + ((y - 80) * 120 / 1440));
            byte b = 150;
            return (b, g, r, 255);
        });

        byte[] refNv12 = new byte[1920 * 1080 * 3 / 2];
        conv.CopyCapturedFrame(videoTex);
        conv.ConvertToCpuFallback(refNv12);

        var sw = Stopwatch.StartNew();
        double gpuWorkMs = 0;
        for (int i = 0; i < 16; i++)
        {
            long t0 = sw.ElapsedTicks;
            conv.CopyCapturedFrame(videoTex);
            var gpuNv12 = conv.ConvertToGpuTexture(i);
            enc.EncodeTexture(gpuNv12);
            gpuWorkMs += (sw.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            Thread.Sleep(5); // Pace slightly faster than 60fps (200fps) so 1-slot real-time queue encodes all frames
        }
        double avgSubmitMs = gpuWorkMs / 16.0;
        Assert.True(gotEnough.Wait(2500), $"Expected >=12 encoded frames, got {encodedUnits.Count}");
        Assert.True(avgSubmitMs < 6.0, $"Expected <6.0 ms/frame GPU convert+submit, got {avgSubmitMs:F2} ms");

        // 1. Verify H.264 High Profile (profile_idc == 100, CABAC + 8x8 DCT) in SPS NAL
        byte[]? sps = encodedUnits
            .SelectMany(au => H264.SplitAnnexB(au))
            .FirstOrDefault(nal => H264.NalType(nal) == H264.NalSps);
        Assert.NotNull(sps);
        Assert.Equal(100, (int)sps![1]); // 100 = High Profile (vs 66 = Baseline)

        // 2. Decode real H.264 bitstream back to 1920x1080 luma and verify full-screen + PSNR >= 38 dB!
        byte[] decodedLuma = DecodeH264ToNv12(encodedUnits.Take(12).ToList(), 1920, 1080);
        Assert.True(decodedLuma[6 * 1920 + 960] > 55, "Decoded H.264 top edge had black bar!");
        Assert.True(decodedLuma[1073 * 1920 + 960] > 55, "Decoded H.264 bottom edge had black bar!");
        Assert.True(decodedLuma[540 * 1920 + 6] > 55, "Decoded H.264 left edge had black bar!");
        Assert.True(decodedLuma[540 * 1920 + 1913] > 55, "Decoded H.264 right edge had black bar!");

        double mse = 0;
        int nPx = 1920 * 1080;
        for (int i = 0; i < nPx; i++)
        {
            double diff = refNv12[i] - decodedLuma[i];
            mse += diff * diff;
        }
        mse /= nPx;
        double psnr = mse <= 1e-6 ? 99.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);
        Assert.True(psnr >= 38.0, $"Expected H.264 High Profile PSNR >= 38.0 dB, got {psnr:F2} dB");
    }

    [Fact]
    public async Task ScreenMirrorSource_LiveCaptureWithMissingTvOsDisplays_StreamsFullScreen1920x1080HighProfile()
    {
        var infoFromTvOs = new Dictionary<string, object?>
        {
            ["name"] = "Living Room",
            ["model"] = "AppleTV5,3",
            ["receiverHDRCapability"] = "1080p60",
        };
        var (dispW, dispH) = MirrorSession.ParseDisplaySize(infoFromTvOs);
        Assert.Equal((1920, 1080), (dispW, dispH));

        if (!GpuTestEnvironment.TryCreateHardwareVideoDevice(out var hwDev)) return;
        using (hwDev)
        {
            if (!GpuTestEnvironment.HasHardwareH264Encoder(hwDev!) || !GpuTestEnvironment.CanDuplicateInteractiveDesktop())
                return;
        }

        await using var source = new ScreenMirrorSource(fps: 60, bitrateMbps: 0);
        source.ConfigureReceiverProfile("AppleTV5,3", dispW, dispH);

        byte[]? sps = null;
        using var gotKey = new ManualResetEventSlim(false);
        source.FrameEncoded += (au, isKey) =>
        {
            if (!isKey || sps is not null) return;
            var nals = H264.SplitAnnexB(au.Span);
            sps = nals.FirstOrDefault(n => H264.NalType(n) == H264.NalSps);
            if (sps is not null) gotKey.Set();
        };

        using var cts = new CancellationTokenSource();
        await source.StartAsync(cts.Token);
        Assert.True(gotKey.Wait(2500), "Timed out waiting for live keyframe");
        cts.Cancel();

        Assert.Equal(1920, source.Width);
        Assert.Equal(1080, source.Height);
        Assert.InRange(source.CurrentBitrateBps, 6_000_000, 7_000_000);
        Assert.Equal(100, (int)sps![1]); // High Profile
    }

    [Fact]
    public void MirrorVideoPacketizer_LocksCodecAndIdrTimestamp_FullScreenViewport_AndAeadNonceAcrossReconnect()
    {
        var packetizer = new MirrorVideoPacketizer();
        byte[] key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var cipher = new MirrorVideoCipher(key);
        using var receiverOpener = new System.Security.Cryptography.ChaCha20Poly1305(key);

        byte[] pFrame = [0, 0, 0, 1, 0x41, 0x9A, 0x22, 0x33];
        byte[] idrFrame =
        [
            0, 0, 0, 1, 0x67, 0x64, 0x00, 0x2A, 0xAC, 0xD9, 0x40, 0x78, 0x02, 0x27, 0xE5, 0xC0,
            0, 0, 0, 1, 0x68, 0xEE, 0x3C, 0xB0,
            0, 0, 0, 1, 0x65, 0x88, 0x84, 0x21, 0xA0, 0xFF,
        ];

        // 1. Pre-codec non-IDR frame MUST be rejected without advancing AEAD nonce!
        Assert.False(packetizer.TryPacketize(pFrame, false, 1920, 1080, 0, 0, 1111UL, cipher,
            out var c0, out var v0, out bool needIdr0));
        Assert.True(needIdr0);
        Assert.Null(c0);
        Assert.Null(v0);

        // 2. First IDR frame emits BOTH 0x01 CodecPacket (with 1920x1080 full-screen fallback!) AND 0x00 VideoPacket with IDENTICAL NTP timestamp!
        ulong expectedTs = 0x123456789ABCDEF0UL;
        Assert.True(packetizer.TryPacketize(idrFrame, true, 1920, 1080, 0, 0, expectedTs, cipher,
            out byte[]? codecPkt, out byte[]? videoPkt, out _));
        Assert.NotNull(codecPkt);
        Assert.NotNull(videoPkt);

        Assert.Equal(0x01, codecPkt![4]);
        Assert.Equal(expectedTs, BinaryPrimitives.ReadUInt64LittleEndian(codecPkt.AsSpan(8)));
        Assert.Equal(1920f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(16)));
        Assert.Equal(1080f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(20)));
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(48))); // x_offset = 0
        Assert.Equal(0f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(52))); // y_offset = 0
        Assert.Equal(1920f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(56))); // full-screen TV width
        Assert.Equal(1080f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt.AsSpan(60))); // full-screen TV height

        Assert.Equal(0x00, videoPkt![4]);
        Assert.Equal(0x10, videoPkt[5]);
        Assert.Equal(expectedTs, BinaryPrimitives.ReadUInt64LittleEndian(videoPkt.AsSpan(8)));

        // Receiver decrypts frame #0 with nonce counter 0 (proving step 1 did NOT corrupt nonce!)
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], 0UL);
        int cipherLen = videoPkt.Length - 128 - 16;
        byte[] plainOut = new byte[cipherLen];
        receiverOpener.Decrypt(
            nonce,
            videoPkt.AsSpan(128, cipherLen),
            videoPkt.AsSpan(128 + cipherLen, 16),
            plainOut,
            videoPkt.AsSpan(0, 128));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32BigEndian(plainOut)); // 6-byte IDR NAL in AVCC

        // 3. Reconnect reset re-emits 0x01 CodecPacket cleanly on next IDR!
        packetizer.Reset();
        byte[] key2 = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        using var cipher2 = new MirrorVideoCipher(key2);
        Assert.True(packetizer.TryPacketize(idrFrame, true, 1920, 1080, 3840, 2160, 9999UL, cipher2,
            out byte[]? codecPkt2, out byte[]? videoPkt2, out _));
        Assert.NotNull(codecPkt2);
        Assert.NotNull(videoPkt2);
        Assert.Equal(3840f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt2!.AsSpan(56)));
        Assert.Equal(2160f, BinaryPrimitives.ReadSingleLittleEndian(codecPkt2.AsSpan(60)));
    }
}
