// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>
/// GPU color-convert + scale using the Direct3D11 Video Processor: BGRA desktop frame →
/// NV12 at the negotiated encode resolution, entirely on the GPU (the heaviest per-frame
/// operation), then a single staging read-back to the CPU NV12 buffer the encoder wants.
/// The video processor is vendor-independent — Intel, AMD, NVIDIA, Qualcomm and MediaTek
/// GPUs all implement it — so this path works on any Windows device.
/// </summary>
internal sealed class GpuColorConverter : IDisposable
{
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoProcessor _processor;
    private readonly ID3D11VideoProcessorEnumerator _enumerator;
    private readonly ID3D11VideoProcessorOutputView _outputView;
    private readonly ID3D11Texture2D _outputNv12;
    private readonly ID3D11Texture2D _stagingNv12;
    private readonly int _dstW;
    private readonly int _dstH;

    /// <summary>The BGRA input texture the capture writes into (desktop-sized).</summary>
    public ID3D11Texture2D InputTexture { get; }

    public GpuColorConverter(ID3D11Device device, int srcW, int srcH, int dstW, int dstH, int fps)
    {
        _context = device.ImmediateContext;
        _dstW = dstW;
        _dstH = dstH;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = _context.QueryInterface<ID3D11VideoContext>();

        var content = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = new Rational((uint)fps, 1u),
            InputWidth = (uint)srcW,
            InputHeight = (uint)srcH,
            OutputFrameRate = new Rational((uint)fps, 1u),
            OutputWidth = (uint)dstW,
            OutputHeight = (uint)dstH,
            Usage = VideoUsage.PlaybackNormal,
        };
        _enumerator = _videoDevice.CreateVideoProcessorEnumerator(content);
        _processor = _videoDevice.CreateVideoProcessor(_enumerator, 0);

        InputTexture = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)srcW,
            Height = (uint)srcH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _outputNv12 = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });

        _stagingNv12 = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)dstW,
            Height = (uint)dstH,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.NV12,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        _outputView = _videoDevice.CreateVideoProcessorOutputView(_outputNv12, _enumerator,
            new VideoProcessorOutputViewDescription
            {
                ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorOutputView { MipSlice = 0 },
            });

        // Studio-range BT.709 in, studio-range NV12 out (standard SDR mirroring).
        _videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, VideoFrameFormat.Progressive);
    }

    /// <summary>Converts the current <see cref="InputTexture"/> into <paramref name="nv12"/> (Y plane then interleaved UV).</summary>
    public void Convert(byte[] nv12)
    {
        var inputView = _videoDevice.CreateVideoProcessorInputView(InputTexture, _enumerator,
            new VideoProcessorInputViewDescription
            {
                FourCC = 0,
                ViewDimension = VideoProcessorInputViewDimension.Texture2D,
                Texture2D = new Texture2DVideoProcessorInputView { MipSlice = 0, ArraySlice = 0 },
            });
        try
        {
            var stream = new VideoProcessorStream
            {
                Enable = true,
                OutputIndex = 0,
                InputFrameOrField = 0,
                PastFrames = 0,
                FutureFrames = 0,
                InputSurface = inputView,
            };
            _videoContext.VideoProcessorBlt(_processor, _outputView, 0, 1, [stream]);
        }
        finally
        {
            inputView.Dispose();
        }

        _context.CopyResource(_stagingNv12, _outputNv12);
        var map = _context.Map(_stagingNv12, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            int rowPitch = (int)map.RowPitch;
            // Y plane: dstH rows of dstW bytes.
            for (int y = 0; y < _dstH; y++)
                Marshal.Copy(IntPtr.Add(map.DataPointer, y * rowPitch), nv12, y * _dstW, _dstW);
            // UV plane follows the Y plane at rowPitch*dstH; dstH/2 rows of dstW bytes.
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
        _outputView.Dispose();
        _stagingNv12.Dispose();
        _outputNv12.Dispose();
        InputTexture.Dispose();
        _processor.Dispose();
        _enumerator.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }
}
