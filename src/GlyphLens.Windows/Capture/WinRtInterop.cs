using System.Runtime.InteropServices;
using GlyphLens.Windows.Platform;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GlyphLens.Windows.Capture;

/// <summary>
/// Bridges classic COM/Win32 objects and WinRT objects for Windows Graphics Capture.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the WinRT <c>GraphicsCaptureItem</c> cannot be created for an arbitrary HWND/HMONITOR
/// from managed WinRT code alone; Windows exposes <c>IGraphicsCaptureItemInterop</c> on the class's
/// activation factory for that. Likewise a <c>Direct3D11CaptureFramePool</c> needs an
/// <c>IDirect3DDevice</c> (created by <c>CreateDirect3D11DeviceFromDXGIDevice</c>), and each captured
/// <c>IDirect3DSurface</c> must be turned back into an <c>ID3D11Texture2D</c> through
/// <c>IDirect3DDxgiInterfaceAccess</c>.
/// </para>
/// <para>
/// The COM interfaces are called through their vtables with function pointers. That keeps the interop
/// trimming/AOT-safe and avoids the legacy [ComImport] marshaller. Every raw interface pointer obtained here
/// is released before the method returns; the returned managed wrappers own their own references.
/// </para>
/// </remarks>
internal static unsafe class WinRtInterop
{
    private const int IUnknownReleaseSlot = 2;
    private const int InteropCreateForWindowSlot = 3;
    private const int InteropCreateForMonitorSlot = 4;
    private const int DxgiAccessGetInterfaceSlot = 3;

    // IGraphicsCaptureItemInterop {3628E81B-3CAC-4C60-B7F4-23CE0E0C3356}
    private static readonly Guid GraphicsCaptureItemInteropIid = new(0x3628E81B, 0x3CAC, 0x4C60, 0xB7, 0xF4, 0x23, 0xCE, 0x0E, 0x0C, 0x33, 0x56);

    // IGraphicsCaptureItem {79C3F95B-31F7-4EC2-A464-632EF5D30760}
    private static readonly Guid GraphicsCaptureItemIid = new(0x79C3F95B, 0x31F7, 0x4EC2, 0xA4, 0x64, 0x63, 0x2E, 0xF5, 0xD3, 0x07, 0x60);

    // IDirect3DDxgiInterfaceAccess {A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1}
    private static readonly Guid DxgiInterfaceAccessIid = new(0xA9B3D012, 0x3DF2, 0x4EE3, 0xB8, 0xD1, 0x86, 0x95, 0xF4, 0x57, 0xD3, 0xC1);

    // ID3D11Texture2D {6F15AAF2-D208-4E89-9AB4-489535D34F9C}
    private static readonly Guid Texture2DIid = new(0x6F15AAF2, 0xD208, 0x4E89, 0x9A, 0xB4, 0x48, 0x95, 0x35, 0xD3, 0x4F, 0x9C);

    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    internal static GraphicsCaptureItem CreateItemForWindow(nint windowHandle) =>
        CreateItem(windowHandle, InteropCreateForWindowSlot);

    internal static GraphicsCaptureItem CreateItemForMonitor(nint monitorHandle) =>
        CreateItem(monitorHandle, InteropCreateForMonitorSlot);

    /// <summary>Wraps a Vortice D3D11 device as a WinRT <see cref="IDirect3DDevice"/>.</summary>
    internal static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var raw));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(raw);
        }
        finally
        {
            Marshal.Release(raw);
        }
    }

    /// <summary>
    /// Returns the Direct3D 11 texture behind a captured surface. The caller owns (and must dispose)
    /// the returned texture.
    /// </summary>
    internal static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var surfacePointer = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var accessGuid = DxgiInterfaceAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(surfacePointer, in accessGuid, out var access));
            try
            {
                var textureGuid = Texture2DIid;
                nint texture;
                var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)access)[DxgiAccessGetInterfaceSlot];
                Marshal.ThrowExceptionForHR(getInterface(access, &textureGuid, &texture));
                return new ID3D11Texture2D(texture);
            }
            finally
            {
                Marshal.Release(access);
            }
        }
        finally
        {
            Marshal.Release(surfacePointer);
        }
    }

    private static GraphicsCaptureItem CreateItem(nint nativeHandle, int vtableSlot)
    {
        var factory = GetItemInteropFactory();
        try
        {
            var itemGuid = GraphicsCaptureItemIid;
            nint item;
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[vtableSlot];
            Marshal.ThrowExceptionForHR(create(factory, nativeHandle, &itemGuid, &item));
            try
            {
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(item);
            }
            finally
            {
                Marshal.Release(item);
            }
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    private static nint GetItemInteropFactory()
    {
        nint hstring = 0;
        fixed (char* className = GraphicsCaptureItemClass)
        {
            Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(className, (uint)GraphicsCaptureItemClass.Length, out hstring));
        }

        try
        {
            var iid = GraphicsCaptureItemInteropIid;
            Marshal.ThrowExceptionForHR(NativeMethods.RoGetActivationFactory(hstring, &iid, out var factory));
            return factory;
        }
        finally
        {
            _ = NativeMethods.WindowsDeleteString(hstring);
        }
    }
}
