using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ReyDesk.Windows.Media;

/// <summary>
/// Bridges Vortice's Direct3D11 device to the WinRT types Windows.Graphics.Capture
/// needs (IDirect3DDevice/IDirect3DSurface), and creates a GraphicsCaptureItem for a
/// monitor - none of this has a managed wrapper in the Windows SDK projection itself,
/// only native COM interop functions/interfaces. Adapted from two independently
/// published, widely-referenced samples rather than derived from scratch (getting a
/// vtable order or IID wrong here fails silently/crashes, not a compile error):
///   - Device bridge: thesimonho/WindowsCapture's Direct3D11Helper.cs (MIT), rewritten
///     against Vortice.DXGI's IDXGIDevice3 instead of that sample's SharpDX one.
///   - GraphicsCaptureItem creation: a widely-circulated "Graphics capture interop
///     with C#/WinRT" gist (reflectronic), written specifically for CsWinRT (the
///     projection this project's net8.0-windows TFM already uses) rather than the
///     older .NET-Framework-era WindowsRuntimeMarshal APIs.
/// </summary>
internal static class Direct3D11Interop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Id3D11Texture2DGuid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, in Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, in Guid iid);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true, ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    /// <summary>Wraps a Vortice D3D11 device (created with BGRA support, required by
    /// Windows.Graphics.Capture) as the WinRT IDirect3DDevice a capture frame pool
    /// needs.</summary>
    public static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice3>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevicePtr);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(unchecked((int)hr));
        }
        var device = MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);
        Marshal.Release(graphicsDevicePtr);
        return device;
    }

    /// <summary>Gets the underlying DXGI texture behind a captured WinRT surface, as a
    /// Vortice <see cref="ID3D11Texture2D"/> ready to bind/copy on the D3D11 device
    /// that produced it.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var guid = Id3D11Texture2DGuid;
        var texturePtr = access.GetInterface(ref guid);
        return new ID3D11Texture2D(texturePtr);
    }

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmonitor)
    {
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var itemPointer = interop.CreateForMonitor(hmonitor, GraphicsCaptureItemGuid);
        var item = GraphicsCaptureItem.FromAbi(itemPointer);
        Marshal.Release(itemPointer);
        return item;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const uint MONITOR_DEFAULTTOPRIMARY = 1;

    /// <summary>HMONITOR for the primary display - multi-monitor selection is out of
    /// scope for this milestone (matches DeviceCapabilities.supports_multi_monitor
    /// being an unused future flag, see docs/protocol.md).</summary>
    public static IntPtr GetPrimaryMonitorHandle() => MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
}
