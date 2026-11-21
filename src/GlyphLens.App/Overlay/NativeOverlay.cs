using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GlyphLens.App.Overlay;

/// <summary>The Win32 calls the overlay window needs.</summary>
internal static class NativeOverlay
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;

    public const uint WdaNone = 0x0;
    public const uint WdaMonitor = 0x1;
    public const uint WdaExcludeFromCapture = 0x11;

    public const int WmMouseActivate = 0x0021;
    public const int WmNcHitTest = 0x0084;
    public const int WmHotkey = 0x0312;
    public const int MaNoActivate = 3;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;

    public const int HtClient = 1;
    public const int HtLeft = 10;
    public const int HtRight = 11;
    public const int HtTop = 12;
    public const int HtTopLeft = 13;
    public const int HtTopRight = 14;
    public const int HtBottom = 15;
    public const int HtBottomLeft = 16;
    public const int HtBottomRight = 17;

    public static readonly IntPtr HwndMessage = new(-3);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "RegisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", EntryPoint = "UnregisterHotKey", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    /// <summary>The usable area (without the taskbar) of every monitor, in physical pixels, and of the primary one.</summary>
    public static (List<PixelRect> All, PixelRect Primary) WorkAreas()
    {
        var all = new List<PixelRect>();
        PixelRect primary = default;
        bool havePrimary = false;
        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var work = new PixelRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
                all.Add(work);
                if ((info.Flags & 1) != 0)
                {
                    primary = work;
                    havePrimary = true;
                }
            }

            return true;
        }, IntPtr.Zero);

        if (all.Count == 0)
        {
            primary = new PixelRect(0, 0, 1280, 720);
            all.Add(primary);
        }
        else if (!havePrimary)
        {
            primary = all[0];
        }

        return (all, primary);
    }
}

/// <summary>Registers system-wide shortcuts through a message-only window (no visible window is needed).</summary>
internal sealed class Win32HotkeyNative : IHotkeyNative
{
    private readonly HwndSource _source;
    private readonly List<int> _ids = [];
    private bool _disposed;

    public Win32HotkeyNative()
    {
        _source = new HwndSource(new HwndSourceParameters("GlyphLensHotkeys") { ParentWindow = NativeOverlay.HwndMessage, Width = 0, Height = 0 });
        _source.AddHook(WndProc);
    }

    public event EventHandler<int>? HotkeyPressed;

    public bool Register(int id, uint modifiers, uint virtualKey)
    {
        if (!NativeOverlay.RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
        {
            return false;
        }

        _ids.Add(id);
        return true;
    }

    public void Unregister(int id)
    {
        if (_ids.Remove(id))
        {
            _ = NativeOverlay.UnregisterHotKey(_source.Handle, id);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (int id in _ids.ToArray())
        {
            Unregister(id);
        }

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeOverlay.WmHotkey)
        {
            HotkeyPressed?.Invoke(this, wParam.ToInt32());
            handled = true;
        }

        return IntPtr.Zero;
    }
}
