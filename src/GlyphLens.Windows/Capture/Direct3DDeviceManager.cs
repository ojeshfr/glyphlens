using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.DirectX.Direct3D11;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Owns the Direct3D 11 device used for one capture session, in both its native (Vortice) and WinRT forms.
/// </summary>
/// <remarks>
/// A hardware BGRA-capable device is created, with the WARP software rasteriser as a fallback for systems
/// without a usable GPU. The device is marked multithread-protected because the free-threaded frame pool
/// uses it from worker threads. The manager is owned by the capture service and shared by successive sessions
/// (see <see cref="Flush"/> for why it must outlive a session); it is replaced after a device-lost fault.
/// </remarks>
internal sealed class Direct3DDeviceManager : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
        FeatureLevel.Level_10_1,
        FeatureLevel.Level_10_0,
    ];

    private readonly object _sync = new();
    private bool _disposed;

    private Direct3DDeviceManager(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice, bool isSoftware)
    {
        Device = device;
        Context = context;
        WinRtDevice = winRtDevice;
        IsSoftwareDevice = isSoftware;
    }

    internal ID3D11Device Device { get; }

    internal ID3D11DeviceContext Context { get; }

    /// <summary>The same device as a WinRT object, for <c>Direct3D11CaptureFramePool</c>.</summary>
    internal IDirect3DDevice WinRtDevice { get; }

    /// <summary>True when running on the WARP software rasteriser (no hardware GPU available).</summary>
    internal bool IsSoftwareDevice { get; }

    internal static Direct3DDeviceManager Create()
    {
        var isSoftware = false;
        ID3D11Device? device;
        ID3D11DeviceContext? context;
        var result = D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            FeatureLevels,
            out device,
            out context);

        if (result.Failure || device is null || context is null)
        {
            isSoftware = true;
            result = D3D11.D3D11CreateDevice(
                null,
                DriverType.Warp,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out device,
                out context);
            result.CheckError();
        }

        try
        {
            using (var multithread = device!.QueryInterface<ID3D11Multithread>())
            {
                multithread.SetMultithreadProtected(true);
            }

            var winRtDevice = WinRtInterop.CreateWinRtDevice(device);
            return new Direct3DDeviceManager(device, context!, winRtDevice, isSoftware);
        }
        catch
        {
            context?.Dispose();
            device?.Dispose();
            throw;
        }
    }

    /// <summary>Returns the device-removed reason as an HRESULT; 0 while the device is healthy.</summary>
    internal int GetDeviceRemovedReason()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Device.DeviceRemovedReason.Code;
    }

    /// <summary>
    /// Flushes the immediate context so Direct3D actually destroys resources that were released.
    /// </summary>
    /// <remarks>
    /// Direct3D 11 defers destruction of released resources until the context is flushed. Windows Graphics Capture
    /// releases a frame pool's shared textures (each backed by a kernel Section handle) asynchronously after the
    /// pool is closed. Without a flush on a device that outlives the session, those handles accumulate (measured:
    /// one per pool buffer per start/stop cycle). Safe to call from any thread and after disposal (then a no-op).
    /// </remarks>
    internal void Flush()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                Context.Flush();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // The WinRT wrapper holds its own reference to the underlying device; release it deterministically.
            (WinRtDevice as IDisposable)?.Dispose();
            Context.ClearState();
            Context.Flush();
            Context.Dispose();
            Device.Dispose();
        }
    }
}
