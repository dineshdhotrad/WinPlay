// SPDX-License-Identifier: GPL-3.0-or-later
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace WinPlay.Capture;

/// <summary>Outcome of a single <see cref="DesktopDuplicator.TryCaptureInto"/> call.</summary>
internal enum FrameStatus
{
    /// <summary>A new desktop frame was copied into the destination texture.</summary>
    Captured,
    /// <summary>No new frame within the timeout — the screen is unchanged; reuse the previous frame.</summary>
    NoChange,
    /// <summary>
    /// The duplication interface was invalidated (resolution/mode change, the secure desktop
    /// for UAC or the lock screen, another duplication client, or a GPU TDR). Recoverable by
    /// re-acquiring the duplication on the same device.
    /// </summary>
    AccessLost,
    /// <summary>The D3D device was removed or reset (GPU driver crash/upgrade). Needs a full
    /// device rebuild.</summary>
    DeviceLost,
}

/// <summary>
/// Captures the primary desktop via the DXGI Desktop Duplication API. GPU-composited,
/// low-overhead, and vendor-independent (Intel/AMD/NVIDIA/Qualcomm/MediaTek on Windows).
/// Each captured frame is copied on the GPU into a caller-owned texture and the DXGI frame
/// is released immediately, minimizing duplication back-pressure. One instance per capture
/// thread; not thread-safe.
///
/// <para>Capture loss is a normal, expected event (a mode change, the UAC/lock secure
/// desktop, or a GPU reset all revoke the duplication). Rather than throw, capture calls
/// report a <see cref="FrameStatus"/> so the caller can recover: <see cref="FrameStatus.AccessLost"/>
/// is repaired cheaply by <see cref="TryRecreateDuplication"/> on the existing device;
/// <see cref="FrameStatus.DeviceLost"/> requires constructing a fresh instance.</para>
/// </summary>
internal sealed class DesktopDuplicator : IDisposable
{
    private IDXGIOutputDuplication _duplication;

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
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
        Format = Format.B8G8R8A8_UNorm;
        _duplication = AcquireDuplication();
    }

    /// <summary>(Re)acquires the desktop duplication on the primary output, refreshing the
    /// current desktop dimensions. Throws on failure (device removed / duplication unavailable).</summary>
    private IDXGIOutputDuplication AcquireDuplication()
    {
        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out IDXGIOutput output).CheckError();
        using (output)
        using (var output1 = output.QueryInterface<IDXGIOutput1>())
        {
            var desc = output.Description;
            Width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
            Height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
            return output1.DuplicateOutput(Device);
        }
    }

    /// <summary>
    /// Re-acquires the duplication on the existing device after an <see cref="FrameStatus.AccessLost"/>,
    /// updating <see cref="Width"/>/<see cref="Height"/> if the desktop resolution changed.
    /// Returns false if the device itself is gone (the caller must rebuild the instance) or the
    /// duplication is momentarily unavailable (e.g. the secure desktop is still up — retry later).
    /// </summary>
    public bool TryRecreateDuplication()
    {
        try
        {
            _duplication.Dispose();
            _duplication = AcquireDuplication();
            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
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
    /// Copies the next desktop frame into <paramref name="dest"/> on the GPU and releases the
    /// DXGI frame. Returns a <see cref="FrameStatus"/> the caller acts on: capture loss is
    /// reported (not thrown) so mirroring can recover rather than die.
    /// </summary>
    public FrameStatus TryCaptureInto(ID3D11Texture2D dest, int timeoutMs)
    {
        Result acquire = _duplication.AcquireNextFrame((uint)timeoutMs, out OutduplFrameInfo _, out IDXGIResource? resource);

        if (acquire == Vortice.DXGI.ResultCode.WaitTimeout)
        {
            resource?.Dispose();
            return FrameStatus.NoChange;
        }
        if (acquire == Vortice.DXGI.ResultCode.AccessLost || acquire == Vortice.DXGI.ResultCode.AccessDenied)
        {
            resource?.Dispose();
            return FrameStatus.AccessLost;
        }
        if (acquire == Vortice.DXGI.ResultCode.DeviceRemoved || acquire == Vortice.DXGI.ResultCode.DeviceReset)
        {
            resource?.Dispose();
            return FrameStatus.DeviceLost;
        }
        acquire.CheckError(); // any other result is genuinely unexpected

        try
        {
            using var texture = resource!.QueryInterface<ID3D11Texture2D>();
            Context.CopyResource(dest, texture);
            return FrameStatus.Captured;
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
