// SPDX-License-Identifier: GPL-3.0-or-later
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>
/// Captures the primary desktop via the DXGI Desktop Duplication API. GPU-composited,
/// low-overhead, and vendor-independent (Intel/AMD/NVIDIA/Qualcomm/MediaTek on Windows).
/// Enables D3D11 multithread protection so the capture device can be shared zero-copy with
/// an asynchronous Media Foundation hardware encoder via <see cref="Vortice.MediaFoundation.IMFDXGIDeviceManager"/>,
/// and automatically recreates the output duplication across fullscreen game mode switches
/// (<c>DXGI_ERROR_ACCESS_LOST</c> / transient <c>E_ACCESSDENIED</c>).
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private IDXGIOutputDuplication? _duplication;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; }
    public int Height { get; }
    public Format Format { get; }
    public string AdapterName { get; }

    public DesktopDuplicator()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, levels,
            out ID3D11Device? device).CheckError();
        Device = device!;
        Context = Device.ImmediateContext;

        using (var mt = Device.QueryInterfaceOrNull<ID3D11Multithread>())
        {
            mt?.SetMultithreadProtected(true);
        }

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        AdapterName = adapter.Description.Description;
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

    private void TryRecreateDuplication()
    {
        try
        {
            _duplication?.Dispose();
        }
        catch { }
        _duplication = null;

        try
        {
            using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
            using (output)
            using (var output1 = output.QueryInterface<IDXGIOutput1>())
            {
                _duplication = output1.DuplicateOutput(Device);
            }
        }
        catch
        {
            // During a fullscreen game mode switch or UAC transition, DuplicateOutput can
            // transiently fail with E_ACCESSDENIED or DXGI_ERROR_NOT_CURRENTLY_AVAILABLE
            // for ~100-300 ms; keep _duplication null and retry on the next tick.
        }
    }

    /// <summary>
    /// Copies the next desktop frame into <paramref name="converter"/>'s input texture on
    /// the GPU (dynamically resizing if a fullscreen game changed the display resolution)
    /// and releases the DXGI frame.
    /// </summary>
    public bool TryCaptureInto(GpuColorConverter converter, int timeoutMs)
    {
        if (_duplication is null)
        {
            TryRecreateDuplication();
            if (_duplication is null)
                return false;
        }

        Result acquire;
        OutduplFrameInfo frameInfo;
        IDXGIResource? resource;
        try
        {
            acquire = _duplication.AcquireNextFrame((uint)timeoutMs, out frameInfo, out resource);
        }
        catch
        {
            TryRecreateDuplication();
            return false;
        }

        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            resource?.Dispose();
            return false;
        }
        if (acquire == Vortice.DXGI.ResultCode.AccessLost || acquire == Vortice.DXGI.ResultCode.InvalidCall || acquire.Failure)
        {
            resource?.Dispose();
            TryRecreateDuplication();
            return false;
        }

        try
        {
            if (resource is null || frameInfo.LastPresentTime == 0)
                return false;

            using var texture = resource.QueryInterfaceOrNull<ID3D11Texture2D>();
            if (texture is null)
                return false;

            var td = texture.Description;
            converter.EnsureInputFormat((int)td.Width, (int)td.Height, td.Format);
            Context.CopyResource(converter.InputTexture, texture);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            resource?.Dispose();
            try { _duplication?.ReleaseFrame(); } catch { }
        }
    }

    public void Dispose()
    {
        try { _duplication?.Dispose(); } catch { }
        _duplication = null;
        Context.Dispose();
        Device.Dispose();
    }
}
