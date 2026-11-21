using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using GlyphLens.App.ViewModels;
using Microsoft.Win32;

namespace GlyphLens.App.Overlay;

/// <summary>
/// The floating translation window: a native WPF window that stays above ordinary windows, never takes the keyboard focus, can
/// be moved, resized, collapsed, made translucent and switched to pass-through, and is excluded from screen capture so GlyphLens
/// never reads (and translates) its own output.
/// </summary>
/// <remarks>
/// <para>
/// Behaviour that WPF has no property for is done with Win32 on the window handle: <c>WS_EX_NOACTIVATE</c> and
/// <c>WS_EX_TOOLWINDOW</c> (no focus stealing, no taskbar or Alt+Tab entry), <c>WM_NCHITTEST</c> for the resize edges,
/// <c>WS_EX_TRANSPARENT</c> for pass-through (applied only while the user chose it) and
/// <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> for the capture exclusion. The window shows what the
/// <see cref="OverlayViewModel"/> holds; it does no recognition or translation.
/// </para>
/// <para>Positions are physical pixels and are re-fitted to the connected monitors when the display configuration changes.</para>
/// </remarks>
public partial class OverlayWindow : Window
{
    private const int DefaultWidthDips = 400;
    private const int DefaultHeightDips = 560;
    private const int MinWidthDips = 320;
    private const int ResizeBorder = 7;
    private const int CollapsedHeightDips = 64;

    private readonly OverlayViewModel _viewModel;
    private readonly OverlaySettingsStore _store;
    private readonly DispatcherTimer _saveTimer;
    private OverlaySettings _settings;
    private OverlayHandleWindow? _handle;
    private double _expandedHeightPixels;
    private bool _placing;

    public OverlayWindow(OverlayViewModel viewModel, OverlaySettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(store);

        _viewModel = viewModel;
        _store = store;
        _settings = store.Load();
        DataContext = viewModel;

        InitializeComponent();

        _viewModel.Opacity = _settings.Opacity;
        _viewModel.FontSize = _settings.FontSize;
        _viewModel.IsPinned = _settings.Pinned;
        _viewModel.IsCollapsed = _settings.Collapsed;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            SaveSettings();
        };

        Header.MouseLeftButtonDown += OnHeaderMouseDown;
        LocationChanged += (_, _) => OnGeometryChanged();
        SizeChanged += (_, _) => OnGeometryChanged();
        _viewModel.PropertyChanged += OnViewModelChanged;
        ((INotifyCollectionChanged)_viewModel.Cards).CollectionChanged += OnCardsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += OnClosed;

        // Create the native window now (without showing it) so that the styles are in place before the first Show.
        _ = new WindowInteropHelper(this).EnsureHandle();
    }

    /// <summary>The native window handle (created in the constructor).</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>The window rectangle in physical pixels.</summary>
    public PixelRect CurrentRect
    {
        get
        {
            _ = NativeOverlay.GetWindowRect(Handle, out var r);
            return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
    }

    /// <summary>Applies the pass-through state, the shown/hidden state and the placement. Called once by the application.</summary>
    public void Attach()
    {
        ApplyPlacement(_settings.Rect);
        ApplyInputMode();
        ApplyVisibility();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = Handle;
        int style = NativeOverlay.GetWindowLong(hwnd, NativeOverlay.GwlExStyle);
        _ = NativeOverlay.SetWindowLong(hwnd, NativeOverlay.GwlExStyle, style | NativeOverlay.WsExToolWindow | NativeOverlay.WsExNoActivate);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        _viewModel.CaptureProtectionText = ApplyCaptureExclusion(hwnd);
    }

    /// <summary>
    /// Excludes this window from screen capture. Returns the text shown in the overlay: whether the protection is on, and if not,
    /// what to do instead. Display affinity <c>WDA_EXCLUDEFROMCAPTURE</c> needs Windows 10 version 2004 (build 19041) or newer.
    /// </summary>
    internal static string ApplyCaptureExclusion(IntPtr hwnd)
    {
        // Diagnostic switch for verification only: it lets a test prove that its "overlay was not captured" check can fail.
        if (Environment.GetEnvironmentVariable("GLYPHLENS_DISABLE_CAPTURE_EXCLUSION") == "1")
        {
            return "Capture protection: DISABLED by GLYPHLENS_DISABLE_CAPTURE_EXCLUSION (diagnostics only).";
        }

        if (NativeOverlay.SetWindowDisplayAffinity(hwnd, NativeOverlay.WdaExcludeFromCapture)
            && NativeOverlay.GetWindowDisplayAffinity(hwnd, out uint affinity)
            && affinity == NativeOverlay.WdaExcludeFromCapture)
        {
            return "Capture protection: on - the overlay is not captured, so it is never translated.";
        }

        return "Capture protection: unavailable on this Windows build. Capture a window instead of the whole display, or move the overlay off the captured area.";
    }

    // ---- window behaviour --------------------------------------------------------------------------------------------

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeOverlay.WmMouseActivate:
                // Clicks reach the controls but never activate the window, so the application underneath keeps the focus.
                handled = true;
                return new IntPtr(NativeOverlay.MaNoActivate);

            case NativeOverlay.WmNcHitTest when _viewModel.IsInteractive:
                int hit = HitTest(lParam);
                if (hit != NativeOverlay.HtClient)
                {
                    handled = true;
                    return new IntPtr(hit);
                }

                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>Maps the cursor position to a resize edge or corner (the window has no system frame).</summary>
    private int HitTest(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var rect = CurrentRect;
        double scale = NativeOverlay.GetDpiForWindow(Handle) / 96.0;
        int border = Math.Max(5, (int)(ResizeBorder * scale));
        int inset = (int)(10 * scale); // the transparent margin that holds the shadow

        bool left = x < rect.X + inset + border;
        bool right = x >= rect.Right - inset - border;
        bool top = y < rect.Y + inset + border;
        bool bottom = y >= rect.Bottom - inset - border && !_viewModel.IsCollapsed;
        if (x < rect.X + inset - border || x > rect.Right - inset + border || y < rect.Y + inset - border || y > rect.Bottom - inset + border)
        {
            return NativeOverlay.HtClient;
        }

        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => NativeOverlay.HtTopLeft,
            (_, true, true, _) => NativeOverlay.HtTopRight,
            (true, _, _, true) => NativeOverlay.HtBottomLeft,
            (_, true, _, true) => NativeOverlay.HtBottomRight,
            (true, _, _, _) => NativeOverlay.HtLeft,
            (_, true, _, _) => NativeOverlay.HtRight,
            (_, _, true, _) => NativeOverlay.HtTop,
            (_, _, _, true) => NativeOverlay.HtBottom,
            _ => NativeOverlay.HtClient,
        };
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && _viewModel.IsInteractive && e.OriginalSource is not Button)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The button was released before the drag started.
            }
        }
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.IsInteractive):
                ApplyInputMode();
                break;
            case nameof(OverlayViewModel.IsVisible):
                ApplyVisibility();
                break;
            case nameof(OverlayViewModel.IsCollapsed):
                ApplyCollapsed();
                OnGeometryChanged();
                break;
            case nameof(OverlayViewModel.Opacity):
            case nameof(OverlayViewModel.FontSize):
            case nameof(OverlayViewModel.IsPinned):
                _saveTimer.Stop();
                _saveTimer.Start();
                break;
        }
    }

    /// <summary>Interactive: the window receives the mouse. Pass-through: the mouse goes to whatever is underneath.</summary>
    private void ApplyInputMode()
    {
        var hwnd = Handle;
        int style = NativeOverlay.GetWindowLong(hwnd, NativeOverlay.GwlExStyle);
        style = _viewModel.IsInteractive
            ? style & ~NativeOverlay.WsExTransparent
            : style | NativeOverlay.WsExTransparent | NativeOverlay.WsExLayered;
        _ = NativeOverlay.SetWindowLong(hwnd, NativeOverlay.GwlExStyle, style);
        UpdateHandle();
    }

    private void ApplyVisibility()
    {
        if (_viewModel.IsVisible)
        {
            if (!IsVisible)
            {
                ApplyPlacement(CurrentRect.Width > 0 && _placing ? CurrentRect : _settings.Rect);
                Show();
            }
        }
        else if (IsVisible)
        {
            SaveSettings();
            Hide();
        }

        UpdateHandle();
    }

    private void ApplyCollapsed()
    {
        var rect = CurrentRect;
        double scale = NativeOverlay.GetDpiForWindow(Handle) / 96.0;
        if (_viewModel.IsCollapsed)
        {
            _expandedHeightPixels = rect.Height;
            MinHeight = CollapsedHeightDips;
            _ = NativeOverlay.SetWindowPos(Handle, IntPtr.Zero, rect.X, rect.Y, rect.Width, (int)(CollapsedHeightDips * scale), NativeOverlay.SwpNoZOrder | NativeOverlay.SwpNoActivate);
        }
        else
        {
            int height = _expandedHeightPixels > 0 ? (int)_expandedHeightPixels : (int)(DefaultHeightDips * scale);
            _ = NativeOverlay.SetWindowPos(Handle, IntPtr.Zero, rect.X, rect.Y, rect.Width, height, NativeOverlay.SwpNoZOrder | NativeOverlay.SwpNoActivate);
        }
    }

    // ---- placement ---------------------------------------------------------------------------------------------------

    /// <summary>Puts the window where it belongs on the connected monitors (the saved place, or the top-right of the primary monitor).</summary>
    internal void ApplyPlacement(PixelRect? desired)
    {
        _placing = true;
        var (all, primary) = NativeOverlay.WorkAreas();
        double scale = NativeOverlay.GetDpiForWindow(Handle) / 96.0;
        var size = (Width: (int)(DefaultWidthDips * scale), Height: (int)(DefaultHeightDips * scale));
        var wanted = desired ?? OverlayPlacement.Default(primary, size);
        int maxWidth = _settings.MaxWidth > 0 ? _settings.MaxWidth : int.MaxValue;
        var fitted = OverlayPlacement.Fit(wanted, all, primary, ((int)(MinWidthDips * scale), (int)((_viewModel.IsCollapsed ? CollapsedHeightDips : 120) * scale)), maxWidth);
        _ = NativeOverlay.SetWindowPos(Handle, IntPtr.Zero, fitted.X, fitted.Y, fitted.Width, fitted.Height, NativeOverlay.SwpNoZOrder | NativeOverlay.SwpNoActivate);
        UpdateHandle();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => ApplyPlacement(CurrentRect));

    private void OnGeometryChanged()
    {
        UpdateHandle();
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveSettings()
    {
        if (!IsVisible)
        {
            return;
        }

        var rect = CurrentRect;
        _settings = _settings with
        {
            X = rect.X,
            Y = rect.Y,
            Width = rect.Width,
            Height = _viewModel.IsCollapsed && _expandedHeightPixels > 0 ? (int)_expandedHeightPixels : rect.Height,
            Opacity = _viewModel.Opacity,
            FontSize = _viewModel.FontSize,
            Pinned = _viewModel.IsPinned,
            Collapsed = _viewModel.IsCollapsed,
        };
        _ = _store.Save(_settings);
    }

    // ---- pass-through handle ------------------------------------------------------------------------------------------

    /// <summary>A pass-through window cannot be clicked, so a small always-clickable handle next to it switches back to interactive.</summary>
    private void UpdateHandle()
    {
        bool needed = _viewModel.IsVisible && !_viewModel.IsInteractive && IsVisible;
        if (!needed)
        {
            _handle?.Hide();
            return;
        }

        _handle ??= new OverlayHandleWindow(() => _viewModel.IsInteractive = true);
        _handle.PlaceNear(CurrentRect);
        if (!_handle.IsVisible)
        {
            _handle.Show();
        }
    }

    // ---- cards: keep the newest in view only when the user is already at the bottom --------------------------------

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
        {
            return;
        }

        // The user is reading the newest card (or the list fits): keep following. Otherwise leave the reading position alone and say
        // that something new arrived below it.
        bool atBottom = CardsScroll.VerticalOffset >= CardsScroll.ScrollableHeight - 24;
        if (atBottom)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CardsScroll.ScrollToEnd);
        }
        else
        {
            _viewModel.HasUnseenResults = true;
        }
    }

    private void OnCardsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel.HasUnseenResults && CardsScroll.VerticalOffset >= CardsScroll.ScrollableHeight - 24)
        {
            _viewModel.HasUnseenResults = false;
        }
    }

    private void OnShowNewResultsClick(object sender, RoutedEventArgs e)
    {
        CardsScroll.ScrollToEnd();
        _viewModel.HasUnseenResults = false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _saveTimer.Stop();
        _handle?.Close();
    }
}
