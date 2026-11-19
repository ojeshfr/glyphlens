using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using GlyphLens.Core.Abstractions;
using GlyphLens.Core.Models;
using GlyphLens.Windows.Capture;
using GlyphLens.Windows.Platform;

namespace GlyphLens.Windows.Display;

/// <summary>
/// Enumerates the displays and top-level application windows that Windows Graphics Capture can capture.
/// </summary>
/// <remarks>
/// <para>
/// Enumeration runs with the calling thread temporarily switched to per-monitor-v2 DPI awareness, so sizes and
/// positions are always physical pixels (the units of captured frames) even when the host process is not
/// DPI-aware, such as a test host. The previous awareness is restored afterwards.
/// </para>
/// <para>
/// Windows are included when they are visible, not minimised, not cloaked (hidden UWP/virtual-desktop windows), have a
/// title and a non-empty frame, are top-level (unowned) application windows, and do not belong to GlyphLens itself.
/// </para>
/// </remarks>
public sealed unsafe class CaptureTargetProvider : ICaptureTargetProvider
{
    public IReadOnlyList<CaptureTarget> GetTargets()
    {
        var previousContext = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiContextPerMonitorAwareV2);
        try
        {
            var targets = new List<CaptureTarget>();
            targets.AddRange(EnumerateDisplays());
            targets.AddRange(EnumerateWindows());
            return targets;
        }
        finally
        {
            if (previousContext != 0)
            {
                _ = NativeMethods.SetThreadDpiAwarenessContext(previousContext);
            }
        }
    }

    private static List<CaptureTarget> EnumerateDisplays()
    {
        var monitors = new List<nint>();
        var handle = GCHandle.Alloc(monitors);
        try
        {
            _ = NativeMethods.EnumDisplayMonitors(0, 0, &MonitorCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var displays = new List<(string DeviceName, CaptureTarget Target)>();
        foreach (var monitor in monitors)
        {
            var info = new NativeMonitorInfoEx { Size = sizeof(NativeMonitorInfoEx) };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                continue;
            }

            var width = info.Monitor.Width;
            var height = info.Monitor.Height;
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            var deviceName = new string(info.DeviceName);
            var isPrimary = (info.Flags & NativeMethods.MonitorInfoFlagPrimary) != 0;
            DpiScale? dpi = NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MonitorDpiTypeEffective, out var dpiX, out _) == 0 && dpiX > 0
                ? DpiScale.FromDpi(dpiX)
                : null;

            var target = new CaptureTarget(
                CaptureTargetId.ForDisplay(monitor),
                CaptureTargetKind.Display,
                DescribeDisplay(deviceName),
                width,
                height,
                isPrimary,
                bounds: new ScreenRectangle(info.Monitor.Left, info.Monitor.Top, width, height),
                dpi: dpi);
            displays.Add((deviceName, target));
        }

        return [.. displays.OrderByDescending(d => d.Target.IsPrimary).ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase).Select(d => d.Target)];
    }

    private static string DescribeDisplay(string deviceName)
    {
        // "\\.\DISPLAY2" -> "Display 2"
        const string Prefix = @"\\.\DISPLAY";
        if (deviceName.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(deviceName.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            return $"Display {number}";
        }

        return string.IsNullOrWhiteSpace(deviceName) ? "Display" : deviceName;
    }

    private static List<CaptureTarget> EnumerateWindows()
    {
        var handles = new List<nint>();
        var handle = GCHandle.Alloc(handles);
        try
        {
            _ = NativeMethods.EnumWindows(&WindowCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        var ownProcessId = (uint)Environment.ProcessId;
        var windows = new List<CaptureTarget>();
        foreach (var window in handles)
        {
            var target = TryDescribeWindow(window, ownProcessId);
            if (target is not null)
            {
                windows.Add(target);
            }
        }

        return windows;
    }

    private static CaptureTarget? TryDescribeWindow(nint window, uint ownProcessId)
    {
        if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsIconic(window))
        {
            return null;
        }

        if (NativeMethods.GetWindow(window, NativeMethods.GetWindowOwner) != 0)
        {
            return null; // owned popups and dialogs are captured through their owner
        }

        var exStyle = NativeMethods.GetWindowLongPtr(window, NativeMethods.WindowLongExStyle);
        if ((exStyle & NativeMethods.ExStyleToolWindow) != 0 && (exStyle & NativeMethods.ExStyleAppWindow) == 0)
        {
            return null;
        }

        int cloaked;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmWindowAttributeCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return null;
        }

        var title = GetWindowTitle(window);
        if (string.IsNullOrWhiteSpace(title) || IsShellWindow(window))
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == ownProcessId)
        {
            return null;
        }

        NativeRect frame;
        if (NativeMethods.DwmGetWindowAttribute(window, NativeMethods.DwmWindowAttributeExtendedFrameBounds, &frame, sizeof(NativeRect)) != 0
            || frame.Width <= 0 || frame.Height <= 0)
        {
            return null;
        }

        return new CaptureTarget(
            CaptureTargetId.ForWindow(window),
            CaptureTargetKind.Window,
            title,
            frame.Width,
            frame.Height,
            processName: TryGetProcessName(processId),
            bounds: new ScreenRectangle(frame.Left, frame.Top, frame.Width, frame.Height));
    }

    private static string GetWindowTitle(nint window)
    {
        var length = NativeMethods.GetWindowTextLength(window);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            var copied = NativeMethods.GetWindowText(window, text, buffer.Length);
            return copied > 0 ? new string(text, 0, copied) : string.Empty;
        }
    }

    private static bool IsShellWindow(nint window)
    {
        const int Capacity = 64;
        char* buffer = stackalloc char[Capacity];
        var length = NativeMethods.GetClassName(window, buffer, Capacity);
        var className = new ReadOnlySpan<char>(buffer, length);
        return className.SequenceEqual("Progman") || className.SequenceEqual("WorkerW") || className.SequenceEqual("Shell_TrayWnd");
    }

    private static string? TryGetProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null; // the process exited, or access was denied
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int MonitorCallback(nint monitor, nint hdc, NativeRect* rect, nint data)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(monitor);
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0; // never let an exception cross the native boundary
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int WindowCallback(nint window, nint data)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(window);
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }
}
