// SPDX-License-Identifier: GPL-3.0-or-later
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>
/// Captures the primary desktop via the DXGI Desktop Duplication API. GPU-composited,
/// low-overhead, and vendor-independent (Intel/AMD/NVIDIA/Qualcomm/MediaTek on Windows).
/// Each captured frame is copied on the GPU into a caller-owned texture and the DXGI
/// frame is released immediately, minimizing duplication back-pressure. One instance per
/// capture thread; not thread-safe.
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private readonly IDXGIOutputDuplication _duplication;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }

    public DesktopDuplicator()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        // BgraSupport enables the video processor; VideoSupport enables the color-convert path.
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
            out ID3D11Device? device).CheckError();
        Device = device!;
        Context = Device.ImmediateContext;

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            var desc = output.Description;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            _duplication = output1.DuplicateOutput(Device);
        }
        Format = Format.B8G8R8A8_UNorm;
    }

    /// <summary>Creates a texture matching the desktop, suitable as a video-processor input.</summary>
    public ID3D11Texture2D CreateBgraTexture() => Device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = Format,
        SampleDescription = new SampleDescription(1, 0),
        Usage = ResourceUsage.Default,
        BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        CPUAccessFlags = CpuAccessFlags.None,
        MiscFlags = ResourceOptionFlags.None,
    });

    /// <summary>
    /// Copies the next desktop frame into <paramref name="dest"/> on the GPU and releases
    /// the DXGI frame. Returns false if no new frame arrived within the timeout (screen
    /// unchanged) so the caller can re-encode the previous frame.
    /// </summary>
    public bool TryCaptureInto(ID3D11Texture2D dest, int timeoutMs)
    {
        Result acquire = _duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out IDXGIResource? resource);
        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            resource?.Dispose();
            return false;
        }
        acquire.CheckError();
        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(dest, texture);
            return true;
        }
        finally
        {
            resource!.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public void Dispose()
    {
        _duplication.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
