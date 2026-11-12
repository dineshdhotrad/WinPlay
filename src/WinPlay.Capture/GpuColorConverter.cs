// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>
/// GPU color-convert + full-screen aspect adaptation using Direct3D11 Video Processor:
/// BGRA / RGB10A2 / FP16-HDR desktop, video, or game frame → full-screen NV12 at the
/// negotiated TV encode resolution, entirely on the GPU.
///
/// Key capabilities:
/// <list type="bullet">
/// <item>High-quality polyphase scaling (<see cref="VideoUsage.PlaybackNormal"/>) + BT.709 color space.</item>
/// <item>Smart GPU matte auto-crop: detects top/bottom letterbox bars (e.g. 16:9 fullscreen video on
/// a 2560x1600 16:10 laptop) or left/right pillarbox bars (e.g. 16:9 video on a 3440x1440 21:9 monitor)
/// via a 64×64 area-downsampled probe and crops dead black borders to exact 1:1 full-screen 16:9,
/// while keeping 100% of desktop UI (tabs + taskbar) full-bleed when no matte bars are present.</item>
/// <item>Atomic non-destructive dynamic resolution &amp; swapchain format transitions, including
/// GPU tone-mapping for Windows 11 HDR / WCG <see cref="Format.R16G16B16A16_Float"/> surfaces.</item>
/// </list>
/// </summary>
internal sealed class GpuColorConverter : IDisposable
{
    public const int RingSize = 8;
    private const int ProbeSize = 64;

    // Precompiled DXBC bytecode (vs_4_0 / ps_4_0) for zero-startup-cost fullscreen triangle passes:
    // 1. VSFullScreen: procedural fullscreen triangle from SV_VertexID (0..2)
    // 2. PSToneMapFp16: scRGB linear FP16 -> soft-shoulder tone-mapped sRGB B8G8R8A8_UNorm
    // 3. PSDownsampleProbe: bilinear UV downsample into 64x64 matte-detection thumbnail
    private static readonly byte[] VsFullScreenBytecode = Convert.FromBase64String(
        "RFhCQxaxx9nL2dwluLlHJpWitpABAAAAqAIAAAUAAAA0AAAAgAAAALQAAAAMAQAALAIAAFJERUZEAAAAAAAAAAAAAAAAAAAAHAAAAAAE/v8AgQAAHAAAAE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQBJU0dOLAAAAAEAAAAIAAAAIAAAAAAAAAAGAAAAAQAAAAAAAAABAQAAU1ZfVmVydGV4SUQAT1NHTlAAAAACAAAACAAAADgAAAAAAAAAAQAAAAMAAAAAAAAADwAAAEQAAAAAAAAAAAAAAAMAAAABAAAAAwwAAFNWX1Bvc2l0aW9uAFRFWENPT1JEAKurq1NIRFIYAQAAQAABAEYAAABgAAAEEhAQAAAAAAAGAAAAZwAABPIgEAAAAAAAAQAAAGUAAAMyIBAAAQAAAGgAAAIBAAAAKQAABxIAEAAAAAAAChAQAAAAAAABQAAAAQAAAAEAAAcSABAAAAAAAAoAEAAAAAAAAUAAAAIAAAABAAAHQgAQAAAAAAAKEBAAAAAAAAFAAAACAAAAVgAABTIAEAAAAAAAhgAQAAAAAAAyAAAPMiAQAAAAAABGABAAAAAAAAJAAAAAAABAAAAAwAAAAAAAAAAAAkAAAAAAgL8AAIA/AAAAAAAAAAA2AAAFMiAQAAEAAABGABAAAAAAADYAAAjCIBAAAAAAAAJAAAAAAAAAAAAAAAAAAAAAAIA/PgAAAVNUQVR0AAAACAAAAAEAAAAAAAAAAwAAAAEAAAABAAAAAgAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    private static readonly byte[] PsToneMapFp16Bytecode = Convert.FromBase64String(
        "RFhCQz5Fd41csNQztzsyxljyap0BAAAAxAQAAAUAAAA0AAAAqAAAAAABAAA0AQAASAQAAFJERUZsAAAAAAAAAAAAAAABAAAAHAAAAAAE//8AgQAAQwAAADwAAAACAAAABQAAAAQAAAD/////AAAAAAEAAAANAAAAU3JjVGV4AE1pY3Jvc29mdCAoUikgSExTTCBTaGFkZXIgQ29tcGlsZXIgMTAuMQCrSVNHTlAAAAACAAAACAAAADgAAAAAAAAAAQAAAAMAAAAAAAAADwMAAEQAAAAAAAAAAAAAAAMAAAABAAAAAwAAAFNWX1Bvc2l0aW9uAFRFWENPT1JEAKurq09TR04sAAAAAQAAAAgAAAAgAAAAAAAAAAAAAAADAAAAAAAAAA8AAABTVl9UYXJnZXQAq6tTSERSDAMAAEAAAADDAAAAWBgABABwEAAAAAAAVVUAAGQgAAQyEBAAAAAAAAEAAABlAAAD8iAQAAAAAABoAAACAwAAABsAAAUyABAAAAAAAEYQEAAAAAAANgAACMIAEAAAAAAAAkAAAAAAAAAAAAAAAAAAAAAAAAAtAAAH8gAQAAAAAABGDhAAAAAAAEZ+EAAAAAAANAAACnIAEAAAAAAARgIQAAAAAAACQAAAAAAAAAAAAAAAAAAAAAAAADQAAAeCABAAAAAAACoAEAAAAAAAGgAQAAAAAAA0AAAHggAQAAAAAAA6ABAAAAAAAAoAEAAAAAAAAAAACjIAEAABAAAA9g8QAAAAAAACQAAAmplZv9DMTL4AAAAAAAAAAA4AAAcSABAAAQAAAAoAEAABAAAAGgAQAAEAAAAyAAAJEgAQAAEAAAAKABAAAQAAAAFAAACamRk+AUAAAJqZWT8OAAAHEgAQAAEAAAAKABAAAQAAADoAEAAAAAAAMQAAB4IAEAAAAAAAAUAAAJqZWT86ABAAAAAAADgAAAdyABAAAQAAAEYCEAAAAAAABgAQAAEAAAA3IAAJcgAQAAAAAAD2DxAAAAAAAEYCEAABAAAARgIQAAAAAAA0AAAKcgAQAAEAAABGAhAAAAAAAAJAAACsxSc3rMUnN6zFJzcAAAAALwAABXIAEAABAAAARgIQAAEAAAA4AAAKcgAQAAEAAABGAhAAAQAAAAJAAABVVdU+VVXVPlVV1T4AAAAAGQAABXIAEAABAAAARgIQAAEAAAAyAAAPcgAQAAEAAABGAhAAAQAAAAJAAAA9Coc/PQqHPz0Khz8AAAAAAkAAAK5HYb2uR2G9rkdhvQAAAAA4AAAKcgAQAAIAAABGAhAAAAAAAAJAAABSuE5BUrhOQVK4TkEAAAAAHQAACnIAEAAAAAAAAkAAABwuTTscLk07HC5NOwAAAABGAhAAAAAAADcAAAlyIBAAAAAAAEYCEAAAAAAARgIQAAIAAABGAhAAAQAAADYAAAWCIBAAAAAAAAFAAAAAAIA/PgAAAVNUQVR0AAAAFwAAAAMAAAAAAAAAAgAAABAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAIAAAACAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=");

    private static readonly byte[] PsDownsampleProbeBytecode = Convert.FromBase64String(
        "RFhCQ3arB85sFoR9KEW0EPtL1YABAAAAUAIAAAUAAAA0AAAA1AAAACwBAABgAQAA1AEAAFJERUaYAAAAAAAAAAAAAAACAAAAHAAAAAAE//8AgQAAbgAAAFwAAAADAAAAAAAAAAAAAAAAAAAAAAAAAAEAAAABAAAAZwAAAAIAAAAFAAAABAAAAP////8AAAAAAQAAAA0AAABMaW5lYXJTYW1wAFNyY1RleABNaWNyb3NvZnQgKFIpIEhMU0wgU2hhZGVyIENvbXBpbGVyIDEwLjEAq6tJU0dOUAAAAAIAAAAIAAAAOAAAAAAAAAABAAAAAwAAAAAAAAAPAAAARAAAAAAAAAAAAAAAAwAAAAEAAAADAwAAU1ZfUG9zaXRpb24AVEVYQ09PUkQAq6urT1NHTiwAAAABAAAACAAAACAAAAAAAAAAAAAAAAMAAAAAAAAADwAAAFNWX1RhcmdldACrq1NIRFJsAAAAQAAAABsAAABaAAADAGAQAAAAAABYGAAEAHAQAAAAAABVVQAAYhAAAzIQEAABAAAAZQAAA/IgEAAAAAAASAAAC/IgEAAAAAAARhAQAAEAAABGfhAAAAAAAABgEAAAAAAAAUAAAAAAAAA+AAABU1RBVHQAAAACAAAAAAAAAAAAAAACAAAAAAAAAAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==");

    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VertexShader _vsFullScreen;
    private readonly ID3D11PixelShader _psToneMapFp16;
    private readonly ID3D11PixelShader _psDownsampleProbe;
    private readonly ID3D11SamplerState _linearSampler;
    private readonly ID3D11Texture2D _probeTex;
    private readonly ID3D11RenderTargetView _probeRtv;
    private readonly ID3D11Texture2D _probeStaging;
    private readonly byte[] _probeScratch = new byte[ProbeSize * ProbeSize * 4];

    private ID3D11VideoProcessor _processor = null!;
    private ID3D11VideoProcessorEnumerator _enumerator = null!;
    private ID3D11VideoProcessorInputView _inputView = null!;
    private ID3D11ShaderResourceView? _inputSrv;
    private ID3D11RenderTargetView? _inputRtv;
    private ID3D11Texture2D? _hdrSourceCopyTex;
    private ID3D11ShaderResourceView? _hdrSourceCopySrv;
    private int _hdrSourceW;
    private int _hdrSourceH;
    private Format _hdrSourceFmt;

    private readonly ID3D11Texture2D[] _outputNv12Ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11Texture2D? _stagingNv12;

    private int _srcW;
    private int _srcH;
    private Format _srcFormat;
    private readonly int _dstW;
    private readonly int _dstH;
    private readonly int _fps;

    private RawRect _activeSrcRect;
    private bool _matteCropped;
    private int _matteStreak;
    private bool _probePendingReadback;
    private long _framesSinceFormatChange;

    /// <summary>The input texture the video processor reads from (matched to current capture dimensions).</summary>
    public ID3D11Texture2D InputTexture { get; private set; } = null!;

    public RawRect ActiveSourceRect
    {
        get { lock (_sync) return _activeSrcRect; }
    }

    public GpuColorConverter(ID3D11Device device, int srcW, int srcH, int dstW, int dstH, int fps)
    {
        _device = device;
        _context = device.ImmediateContext;
        _dstW = dstW;
        _dstH = dstH;
        _fps = fps;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        _vsFullScreen = device.CreateVertexShader(VsFullScreenBytecode);
        _psToneMapFp16 = device.CreatePixelShader(PsToneMapFp16Bytecode);
        _psDownsampleProbe = device.CreatePixelShader(PsDownsampleProbeBytecode);
        _linearSampler = device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            ComparisonFunc = ComparisonFunction.Never,
            MaxLOD = float.MaxValue,
        });

        _probeTex = device.CreateTexture2D(new Texture2DDescription
        {
            Width = ProbeSize,
            Height = ProbeSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        });
        _probeRtv = device.CreateRenderTargetView(_probeTex);
        _probeStaging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = ProbeSize,
            Height = ProbeSize,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        for (int i = 0; i < RingSize; i++)
        {
            _outputNv12Ring[i] = device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)dstW,
                Height = (uint)dstH,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.NV12,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
        }

        RebuildProcessorAndViews(srcW, srcH, Format.B8G8R8A8_UNorm);
    }

    private static Format NormalizeProcessorFormat(Format rawFormat) => rawFormat switch
    {
        Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_UNorm,
        Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb => Format.R8G8B8A8_UNorm,
        Format.R10G10B10A2_UNorm => Format.R10G10B10A2_UNorm,
        _ => Format.B8G8R8A8_UNorm, // FP16 HDR / other formats tone-map into B8G8R8A8_UNorm
    };

    private static bool CanDirectCopyToProcessorInput(Format rawFormat) => rawFormat is
        Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or
        Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or
        Format.R10G10B10A2_UNorm;

    /// <summary>
    /// Ensures <see cref="InputTexture"/> matches the captured frame's dimensions and a
    /// VideoProcessor-supported format without ever leaving existing resources disposed on failure.
    /// </summary>
    public void EnsureInputFormat(int srcW, int srcH, Format format)
    {
        Format vpFormat = NormalizeProcessorFormat(format);
        lock (_sync)
        {
            if (srcW == _srcW && srcH == _srcH && vpFormat == _srcFormat && InputTexture is not null)
                return;

            RebuildProcessorAndViews(srcW, srcH, vpFormat);
        }
    }

    /// <summary>
    /// Copies (or GPU tone-maps for HDR <see cref="Format.R16G16B16A16_Float"/>) a captured
    /// desktop/video/game texture into <see cref="InputTexture"/> and updates smart matte cropping.
    /// </summary>
    public void CopyCapturedFrame(ID3D11Texture2D sourceTexture)
    {
        var td = sourceTexture.Description;
        int srcW = (int)td.Width;
        int srcH = (int)td.Height;
        Format rawFormat = td.Format;

        lock (_sync)
        {
            EnsureInputFormat(srcW, srcH, rawFormat);

            if (CanDirectCopyToProcessorInput(rawFormat))
            {
                _context.CopyResource(InputTexture, sourceTexture);
            }
            else
            {
                BlitHdrOrCustomFormatToInput(sourceTexture, srcW, srcH, rawFormat);
            }

            UpdateActiveSourceRectIfNeeded();
            _framesSinceFormatChange++;
        }
    }

    private void BlitHdrOrCustomFormatToInput(ID3D11Texture2D sourceTexture, int srcW, int srcH, Format rawFormat)
    {
        if (_hdrSourceCopyTex is null || _hdrSourceCopySrv is null ||
            _hdrSourceW != srcW || _hdrSourceH != srcH || _hdrSourceFmt != rawFormat)
        {
            _hdrSourceCopySrv?.Dispose();
            _hdrSourceCopyTex?.Dispose();

            _hdrSourceCopyTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)srcW,
                Height = (uint)srcH,
                MipLevels = 1,
                ArraySize = 1,
                Format = rawFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            });
            _hdrSourceCopySrv = _device.CreateShaderResourceView(_hdrSourceCopyTex);
            _hdrSourceW = srcW;
            _hdrSourceH = srcH;
            _hdrSourceFmt = rawFormat;
        }

        _context.CopyResource(_hdrSourceCopyTex, sourceTexture);
        RunFullScreenPass(_hdrSourceCopySrv, _inputRtv!, srcW, srcH, _psToneMapFp16);
    }

    private void RunFullScreenPass(
        ID3D11ShaderResourceView srv, ID3D11RenderTargetView rtv, int outW, int outH, ID3D11PixelShader ps)
    {
        _context.IASetInputLayout(null);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vsFullScreen);
        _context.PSSetShader(ps);
        _context.PSSetSampler(0, _linearSampler);
        _context.PSSetShaderResource(0, srv);
        _context.RSSetViewport(0f, 0f, outW, outH, 0f, 1f);
        _context.OMSetRenderTargets(rtv, null);
        _context.Draw(3, 0);

        // Immediately unbind RTV and SRV so InputTexture has zero D3D11 hazard in subsequent passes.
        _context.OMSetRenderTargets((ID3D11RenderTargetView?)null, null);
        _context.PSSetShaderResource(0, null!);
    }

    private void RebuildProcessorAndViews(int srcW, int srcH, Format vpFormat)
    {
        // Build all new COM objects BEFORE disposing old ones so a driver error can never brick the session.
        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1u),
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputFrameRate = new Rational((uint)_fps, 1u),
            OutputWidth = (uint)_dstW,
            OutputHeight = (uint)_dstH,
            Usage = VideoUsage.OptimalSpeed, // Fixed-function hardware scaling so iGPU never contends with browser video decode
        };

        ID3D11VideoProcessorEnumerator? newEnum = null;
        ID3D11VideoProcessor? newProc = null;
        ID3D11Texture2D? newInputTex = null;
        ID3D11ShaderResourceView? newInputSrv = null;
        ID3D11RenderTargetView? newInputRtv = null;
        ID3D11VideoProcessorInputView? newInputView = null;
        var newOutputViews = new ID3D11VideoProcessorOutputView[RingSize];

        try
        {
            newEnum = _videoDevice.CreateVideoProcessorEnumerator(content);
            newProc = _videoDevice.CreateVideoProcessor(newEnum, 0);

            newInputTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)srcW,
                Height = (uint)srcH,
                MipLevels = 1,
                ArraySize = 1,
                Format = vpFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            newInputSrv = _device.CreateShaderResourceView(newInputTex);
            newInputRtv = _device.CreateRenderTargetView(newInputTex);

            newInputView = _videoDevice.CreateVideoProcessorInputView(newInputTex, newEnum,
                new VideoProcessorInputViewDescription
                {
                    FourCC = 0,
                    ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
                });

            for (int i = 0; i < RingSize; i++)
            {
                newOutputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputNv12Ring[i], newEnum,
                    new VideoProcessorOutputViewDescription
                    {
                        ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                        Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                    });
            }
        }
        catch
        {
            for (int i = 0; i < RingSize; i++) newOutputViews[i]?.Dispose();
            newInputView?.Dispose();
            newInputRtv?.Dispose();
            newInputSrv?.Dispose();
            newInputTex?.Dispose();
            newProc?.Dispose();
            newEnum?.Dispose();
            throw;
        }

        // Swap in atomically and release prior objects.
        for (int i = 0; i < RingSize; i++)
        {
            _outputViews[i]?.Dispose();
            _outputViews[i] = newOutputViews[i];
        }
        _inputView?.Dispose();
        _inputRtv?.Dispose();
        _inputSrv?.Dispose();
        InputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();

        _enumerator = newEnum;
        _processor = newProc;
        InputTexture = newInputTex;
        _inputSrv = newInputSrv;
        _inputRtv = newInputRtv;
        _inputView = newInputView;

        _srcW = srcW;
        _srcH = srcH;
        _srcFormat = vpFormat;
        _framesSinceFormatChange = 0;
        _matteCropped = false;
        _matteStreak = 0;
        _probePendingReadback = false;
        _activeSrcRect = new RawRect(0, 0, srcW, srcH);

        var fullDstRect = new RawRect(0, 0, _dstW, _dstH);
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetOutputTargetRect(_processor, true, fullDstRect);
        _videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, fullDstRect);
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, _activeSrcRect);

        // Explicit BT.709 color space: full-range RGB desktop/video input -> Rec.709 YCbCr video output.
        _videoContext.VideoProcessorSetStreamColorSpace(_processor, 0, new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,     // 0 = Full range (0..255)
            YCbCr_Matrix = 1,  // 1 = ITU-R BT.709
            Nominal_Range = 2, // 2 = 0..255
        });
        _videoContext.VideoProcessorSetOutputColorSpace(_processor, new VideoProcessorColorSpace
        {
            Usage = 0,
            RGB_Range = 0,
            YCbCr_Matrix = 1,  // 1 = ITU-R BT.709
            Nominal_Range = 1, // 1 = Studio range (16..235)
        });
    }

    private void UpdateActiveSourceRectIfNeeded()
    {
        double dstAspect = (double)_dstW / _dstH;
        double srcAspect = (double)_srcW / _srcH;

        // Already matches the TV's 16:9 aspect ratio: full source -> full destination.
        if (Math.Abs(srcAspect - dstAspect) <= 0.025)
        {
            ApplySourceRect(new RawRect(0, 0, _srcW, _srcH));
            _matteCropped = false;
            _matteStreak = 0;
            _probePendingReadback = false;
            return;
        }

        if (_inputSrv is null) return;

        // Initial frame after startup or display mode switch: synchronously establish initial crop once.
        if (_framesSinceFormatChange == 0)
        {
            RunFullScreenPass(_inputSrv, _probeRtv, ProbeSize, ProbeSize, _psDownsampleProbe);
            _context.CopyResource(_probeStaging, _probeTex);
            var map = _context.Map(_probeStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                CopyMappedProbe(map);
            }
            finally
            {
                _context.Unmap(_probeStaging, 0);
            }
            _probePendingReadback = false;
            EvaluateProbeBuffer(srcAspect, dstAspect, isInitialFrame: true);
            return;
        }

        // Steady-state streaming: NEVER block the CPU or D3D11 multithread lock with synchronous Map()!
        // Stage 2: Attempt non-blocking DoNotWait readback of the probe submitted on an earlier frame.
        if (_probePendingReadback)
        {
            var hr = _context.Map(_probeStaging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.DoNotWait, out MappedSubresource asyncMap);
            if (!hr.Success)
                return; // GPU still finishing previous frame; try again next tick in 0.000 ms!

            try
            {
                CopyMappedProbe(asyncMap);
            }
            finally
            {
                _context.Unmap(_probeStaging, 0);
            }
            _probePendingReadback = false;
            EvaluateProbeBuffer(srcAspect, dstAspect, isInitialFrame: false);
            return;
        }

        // Stage 1: Submit a lightweight 64x64 GPU downsample every 15 frames (~250 ms @ 60 Hz)
        // regardless of _matteStreak so sports scoreboards/overlays near bars never trigger 60 Hz probing.
        if ((_framesSinceFormatChange % 15) != 0)
            return;

        RunFullScreenPass(_inputSrv, _probeRtv, ProbeSize, ProbeSize, _psDownsampleProbe);
        _context.CopyResource(_probeStaging, _probeTex);
        _probePendingReadback = true;
    }

    private void CopyMappedProbe(MappedSubresource map)
    {
        int pitch = (int)map.RowPitch;
        for (int y = 0; y < ProbeSize; y++)
            Marshal.Copy(IntPtr.Add(map.DataPointer, y * pitch), _probeScratch, y * ProbeSize * 4, ProbeSize * 4);
    }

    private void EvaluateProbeBuffer(double srcAspect, double dstAspect, bool isInitialFrame)
    {
        bool isTaller = srcAspect < dstAspect; // e.g. 2560x1600 (16:10) on 1920x1080 (16:9)
        int cropW = _srcW, cropH = _srcH, barX = 0, barY = 0;
        double sideADarkRatio, sideBDarkRatio, sideAMean, sideBMean, centerActiveRatio;
        int centerPeak;

        if (isTaller)
        {
            cropH = Math.Clamp(((int)Math.Round(_srcW / dstAspect)) & ~1, 2, _srcH);
            barY = ((_srcH - cropH) / 2) & ~1;
            int barRows = Math.Max(1, (int)Math.Floor((double)ProbeSize * barY / _srcH));
            MeasureStrips(
                isHorizontalBars: true,
                barCells: barRows,
                out sideADarkRatio, out sideAMean,
                out sideBDarkRatio, out sideBMean,
                out centerActiveRatio, out centerPeak);
        }
        else
        {
            cropW = Math.Clamp(((int)Math.Round(_srcH * dstAspect)) & ~1, 2, _srcW);
            barX = ((_srcW - cropW) / 2) & ~1;
            int barCols = Math.Max(1, (int)Math.Floor((double)ProbeSize * barX / _srcW));
            MeasureStrips(
                isHorizontalBars: false,
                barCells: barCols,
                out sideADarkRatio, out sideAMean,
                out sideBDarkRatio, out sideBMean,
                out centerActiveRatio, out centerPeak);
        }

        // Hold current crop state steady during full-screen black fades between video scenes.
        if (centerPeak <= 22 && centerActiveRatio < 0.02)
            return;

        bool hasBlackMatteBars =
            sideADarkRatio >= 0.94 &&
            sideBDarkRatio >= 0.94 &&
            sideAMean <= 19.0 &&
            sideBMean <= 19.0 &&
            centerActiveRatio >= 0.05;

        if (isInitialFrame)
        {
            _matteCropped = hasBlackMatteBars;
            _matteStreak = 0;
        }
        else if (hasBlackMatteBars != _matteCropped)
        {
            _matteStreak++;
            // Exit immediately (1 async probe = ~250 ms) when BOTH bars have UI (tabs + taskbar),
            // enter fullscreen cleanly in 2 probes (~500 ms), and hold 6 probes (~1.5 s) if only one bar lit up.
            bool bothBarsLit = !hasBlackMatteBars && sideADarkRatio < 0.85 && sideBDarkRatio < 0.85;
            int requiredStreak = hasBlackMatteBars ? 2 : (bothBarsLit ? 1 : 6);
            if (_matteStreak >= requiredStreak)
            {
                _matteCropped = hasBlackMatteBars;
                _matteStreak = 0;
            }
        }
        else
        {
            _matteStreak = 0;
        }

        var nextRect = _matteCropped
            ? new RawRect(barX, barY, barX + cropW, barY + cropH)
            : new RawRect(0, 0, _srcW, _srcH);
        ApplySourceRect(nextRect);
    }

    private void MeasureStrips(
        bool isHorizontalBars,
        int barCells,
        out double sideADarkRatio, out double sideAMean,
        out double sideBDarkRatio, out double sideBMean,
        out double centerActiveRatio, out int centerPeak)
    {
        int sideADark = 0, sideATotal = 0; long sideASum = 0;
        int sideBDark = 0, sideBTotal = 0; long sideBSum = 0;
        int centerActive = 0, centerTotal = 0;
        centerPeak = 0;

        int innerStart = Math.Min(barCells + 1, ProbeSize / 2 - 1);
        int innerEnd = Math.Max(ProbeSize - barCells - 1, ProbeSize / 2 + 1);

        for (int y = 0; y < ProbeSize; y++)
        {
            for (int x = 0; x < ProbeSize; x++)
            {
                int idx = (y * ProbeSize + x) * 4;
                int b = _probeScratch[idx];
                int g = _probeScratch[idx + 1];
                int r = _probeScratch[idx + 2];
                int v = Math.Max(r, Math.Max(g, b));

                int coord = isHorizontalBars ? y : x;
                if (coord < barCells)
                {
                    sideATotal++;
                    sideASum += v;
                    if (v <= 22) sideADark++;
                }
                else if (coord >= ProbeSize - barCells)
                {
                    sideBTotal++;
                    sideBSum += v;
                    if (v <= 22) sideBDark++;
                }
                else if (coord >= innerStart && coord < innerEnd)
                {
                    centerTotal++;
                    if (v > 24) centerActive++;
                    if (v > centerPeak) centerPeak = v;
                }
            }
        }

        sideADarkRatio = sideATotal > 0 ? (double)sideADark / sideATotal : 0.0;
        sideAMean = sideATotal > 0 ? (double)sideASum / sideATotal : 255.0;
        sideBDarkRatio = sideBTotal > 0 ? (double)sideBDark / sideBTotal : 0.0;
        sideBMean = sideBTotal > 0 ? (double)sideBSum / sideBTotal : 255.0;
        centerActiveRatio = centerTotal > 0 ? (double)centerActive / centerTotal : 0.0;
    }

    private void ApplySourceRect(RawRect rect)
    {
        if (rect.Left == _activeSrcRect.Left &&
            rect.Top == _activeSrcRect.Top &&
            rect.Right == _activeSrcRect.Right &&
            rect.Bottom == _activeSrcRect.Bottom)
            return;

        _activeSrcRect = rect;
        _videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, rect);
    }

    public ID3D11Texture2D ConvertToGpuTexture(int slot)
    {
        lock (_sync)
        {
            int idx = (slot & 0x7FFFFFFF) % RingSize;
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = _inputView,
            };
            _videoContext.VideoProcessorBlt(_processor, _outputViews[idx], 0, 1, [stream]);
            _context.Flush();
            return _outputNv12Ring[idx];
        }
    }

    public void ConvertToCpuFallback(byte[] nv12)
    {
        var tex = ConvertToGpuTexture(0);
        _stagingNv12 ??= _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_dstW,
            Height = (uint)_dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _context.CopyResource(_stagingNv12, tex);
        var map = _context.Map(_stagingNv12, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)map.RowPitch;
            for (int y = 0; y < _dstH; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, y * rowPitch), nv12, y * _dstW, _dstW);
            int uvSrcBase = rowPitch * _dstH;
            int uvDstBase = _dstW * _dstH;
            for (int y = 0; y < _dstH / 2; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, uvSrcBase + y * rowPitch), nv12, uvDstBase + y * _dstW, _dstW);
        }
        finally
        {
            _context.Unmap(_stagingNv12, 0);
        }
    }

    public void Dispose()
    {
        _stagingNv12?.Dispose();
        _hdrSourceCopySrv?.Dispose();
        _hdrSourceCopyTex?.Dispose();
        _probeStaging.Dispose();
        _probeRtv.Dispose();
        _probeTex.Dispose();
        _linearSampler.Dispose();
        _psDownsampleProbe.Dispose();
        _psToneMapFp16.Dispose();
        _vsFullScreen.Dispose();

        for (int i = 0; i < RingSize; i++)
        {
            _outputViews[i]?.Dispose();
            _outputNv12Ring[i]?.Dispose();
        }
        _inputView?.Dispose();
        _inputRtv?.Dispose();
        _inputSrv?.Dispose();
        InputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
