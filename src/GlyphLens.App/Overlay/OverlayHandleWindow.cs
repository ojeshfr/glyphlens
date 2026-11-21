using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace GlyphLens.App.Overlay;

/// <summary>
/// A small, always-clickable button shown next to the overlay while it is in pass-through mode. The overlay itself lets every
/// click through, so without this handle the user could only leave pass-through with the keyboard shortcut.
/// </summary>
internal sealed class OverlayHandleWindow : Window
{
    private const double SizeDips = 30;

    public OverlayHandleWindow(Action unlock)
    {
        ArgumentNullException.ThrowIfNull(unlock);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = SizeDips;
        Height = SizeDips;
        Title = "GlyphLens overlay unlock";
        AutomationProperties.SetAutomationId(this, "GlyphLensOverlayUnlockHandle");

        var button = new Button
        {
            Content = "", // a cursor/unlock glyph from Segoe MDL2 Assets
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Width = SizeDips,
            Height = SizeDips,
            BorderThickness = new Thickness(0),
            ToolTip = "Pass-through is on. Click to use the overlay again (Ctrl+Alt+I).",
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        // follow the current palette (light or dark) and switch with it
        button.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
        button.SetResourceReference(Control.ForegroundProperty, "OnAccentBrush");
        AutomationProperties.SetAutomationId(button, "OverlayUnlockButton");
        AutomationProperties.SetName(button, "Switch to interactive");
        button.Click += (_, _) => unlock();
        Content = button;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int style = NativeOverlay.GetWindowLong(hwnd, NativeOverlay.GwlExStyle);
            _ = NativeOverlay.SetWindowLong(hwnd, NativeOverlay.GwlExStyle, style | NativeOverlay.WsExToolWindow | NativeOverlay.WsExNoActivate);
            if (Environment.GetEnvironmentVariable("GLYPHLENS_DISABLE_CAPTURE_EXCLUSION") != "1")
            {
                _ = NativeOverlay.SetWindowDisplayAffinity(hwnd, NativeOverlay.WdaExcludeFromCapture);
            }

            HwndSource.FromHwnd(hwnd)?.AddHook(static (nint _, int msg, nint _, nint _, ref bool handled) =>
            {
                if (msg == NativeOverlay.WmMouseActivate)
                {
                    handled = true;
                    return new IntPtr(NativeOverlay.MaNoActivate);
                }

                return IntPtr.Zero;
            });
        };
    }

    /// <summary>Puts the handle at the top-right corner of the overlay, over the header.</summary>
    public void PlaceNear(PixelRect overlay)
    {
        _ = new WindowInteropHelper(this).EnsureHandle();
        double scale = NativeOverlay.GetDpiForWindow(new WindowInteropHelper(this).Handle) / 96.0;
        int size = (int)(SizeDips * scale);
        int x = overlay.Right - size - (int)(18 * scale);
        int y = overlay.Y + (int)(16 * scale);
        _ = NativeOverlay.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, x, y, size, size, NativeOverlay.SwpNoZOrder | NativeOverlay.SwpNoActivate);
    }
}
