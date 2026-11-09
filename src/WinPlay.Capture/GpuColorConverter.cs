// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>
/// GPU color-convert + scale using the Direct3D11 Video Processor: BGRA desktop/game
/// frame → NV12 at the negotiated encode resolution, entirely on the GPU.
///
/// Supports dynamic input resolution and format changes (<see cref="EnsureInputFormat"/>)
/// so switching into an exclusive-fullscreen game at a different resolution (e.g.
/// 1920x1080 desktop → 2560x1600 fullscreen game) seamlessly resizes the input surface
/// and hardware-scales into the active H.264 encode stream without dropping the session
/// or stalling on a mismatched <c>CopyResource</c>.
/// </summary>
internal sealed class GpuColorConverter : IDisposable
{
    public const int RingSize = 8;

    private readonly object _sync = new();
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private ID3D11VideoProcessor _processor = null!;
    private ID3D11VideoProcessorEnumerator _enumerator = null!;
    private ID3D11VideoProcessorInputView _inputView = null!;
    private readonly ID3D11Texture2D[] _outputNv12Ring = new ID3D11Texture2D[RingSize];
    private readonly ID3D11VideoProcessorOutputView[] _outputViews = new ID3D11VideoProcessorOutputView[RingSize];
    private ID3D11Texture2D? _stagingNv12;
    private int _srcW;
    private int _srcH;
    private Format _srcFormat;
    private readonly int _dstW;
    private readonly int _dstH;
    private readonly int _fps;

    /// <summary>The input texture the capture writes into (dynamically matched to the current desktop/game resolution).</summary>
    public ID3D11Texture2D InputTexture { get; private set; } = null!;

    public GpuColorConverter(ID3D11Device device, int srcW, int srcH, int dstW, int dstH, int fps)
    {
        _device = device;
        _context = device.ImmediateContext;
        _dstW = dstW;
        _dstH = dstH;
        _fps = fps;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

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

    /// <summary>
    /// Ensures <see cref="InputTexture"/> matches the captured DXGI frame's dimensions and
    /// format (e.g. when a fullscreen game switches the display between 1920x1080 and 2560x1600).
    /// </summary>
    public void EnsureInputFormat(int srcW, int srcH, Format format)
    {
        lock (_sync)
        {
            if (srcW == _srcW && srcH == _srcH && format == _srcFormat && InputTexture is not null)
                return;

            RebuildProcessorAndViews(srcW, srcH, format);
        }
    }

    private void RebuildProcessorAndViews(int srcW, int srcH, Format format)
    {
        for (int i = 0; i < RingSize; i++)
            _outputViews[i]?.Dispose();
        _inputView?.Dispose();
        InputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();

        _srcW = srcW;
        _srcH = srcH;
        _srcFormat = format;

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)_fps, 1u),
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputFrameRate = new Rational((uint)_fps, 1u),
            OutputWidth = (uint)_dstW,
            OutputHeight = (uint)_dstH,
            Usage = VideoUsage.OptimalSpeed,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        InputTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW,
            Height = (uint)srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _inputView = _videoDevice.CreateVideoProcessorInputView(InputTexture, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });

        for (int i = 0; i < RingSize; i++)
        {
            _outputViews[i] = _videoDevice.CreateVideoProcessorOutputView(_outputNv12Ring[i], _enumerator,
                new VideoProcessorOutputViewDescription
                {
                    ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                    Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
                });
        }

        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
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
        for (int i = 0; i < RingSize; i++)
        {
            _outputViews[i]?.Dispose();
            _outputNv12Ring[i]?.Dispose();
        }
        _inputView?.Dispose();
        InputTexture?.Dispose();
        _processor?.Dispose();
        _enumerator?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
