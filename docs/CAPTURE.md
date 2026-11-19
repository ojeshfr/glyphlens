# Screen Capture (Phase 1)

> Status: **implemented** for display and window capture with a live preview. Region capture is **not**
> implemented (next capture milestone). Verification status of each behaviour is in
> [CAPTURE-MANUAL-TESTS.md](CAPTURE-MANUAL-TESTS.md); nothing here claims more than that file records.

## What it does

GlyphLens can capture a whole display or a single application window with Windows Graphics Capture (WGC), publish
the newest frame to in-process consumers, and show it in a live preview. The preview exists to verify the capture
engine; later phases attach OCR to the same frame source.

## Components

| Project | Type | Role |
| --- | --- | --- |
| `GlyphLens.Core` | `CaptureTarget`, `CaptureTargetKind`, `CaptureStatus`, `CaptureError`, `CaptureErrorKind`, `FrameMetadata`, `CapturedFrame`, `DpiScale`, `PixelFormat` | Platform-independent models. |
| `GlyphLens.Core` | `IScreenCaptureService`, `ICaptureTargetProvider`, `IFrameSource` | Contracts. |
| `GlyphLens.Core` | `CaptureStateMachine`, `LatestFrameMailbox`, `CaptureException` | Platform-independent logic: legal state transitions, latest-wins frame publication. |
| `GlyphLens.Windows` | `WindowsGraphicsCaptureService` | Session lifecycle, status events, shared GPU device ownership. |
| `GlyphLens.Windows` | `CaptureSessionManager` | Owns one session's native objects; frame loop; target watchdog. |
| `GlyphLens.Windows` | `Direct3DDeviceManager` | Direct3D 11 device (hardware, WARP fallback) in native and WinRT form. |
| `GlyphLens.Windows` | `FrameProcessor` | GPU frame to pooled CPU buffer (staging texture, row compaction). |
| `GlyphLens.Windows` | `WinRtInterop`, `NativeMethods` | COM/WinRT interop and P/Invoke. |
| `GlyphLens.Windows` | `CaptureTargetProvider` | Enumerates displays and windows. |
| `GlyphLens.App` | `CaptureViewModel`, `CaptureView`, `CapturePreview` | MVVM view model, view, and the preview control. |

Dependency direction is unchanged (see [ARCHITECTURE.md](ARCHITECTURE.md)): the App references Core, Windows and
Infrastructure; Windows references Core only.

### Replaced Phase 0 contract

The Phase 0 `IScreenCaptureService.CaptureRegionAsync` (one-shot) and the unpooled `CapturedFrame` could not
express a live session, drop policy or frame ownership, and nothing implemented them. They were replaced rather
than duplicated. `IOcrEngine` still takes a `CapturedFrame`.

## Data flow

```
GraphicsCaptureItem (window or monitor)
        │  IGraphicsCaptureItemInterop (HWND / HMONITOR → item)
        ▼
Direct3D11CaptureFramePool (free-threaded, 2 buffers, BGRA8)     ◄── shared Direct3D 11 device
        │  FrameArrived (thread-pool thread)
        ▼
CaptureSessionManager   keep newest queued frame, drop the rest · frame-rate limit (default 30 fps)
        ▼
FrameProcessor          GPU copy → staging texture → Map → compact rows → pooled byte[]
        ▼
CapturedFrame (ref-counted, pooled)  ──►  LatestFrameMailbox (one slot, latest wins)
                                                │  TryAcquireLatest / WaitForNewerAsync
                                   ┌────────────┴────────────┐
                              CapturePreview            (future) OCR consumer
                          (UI render tick, WriteableBitmap)
```

### Backpressure and memory

- The mailbox holds **one** frame. Publishing replaces and releases the previous frame, so a slow consumer can never
  create a backlog (unit test: 1000 published frames, 1 live buffer).
- Frames are reference counted. A consumer that is still processing frame N keeps it valid after N+1 replaces it;
  the buffer returns to `ArrayPool<byte>` when the last reference is released.
- Frames arriving faster than `WindowsCaptureOptions.MaxFramesPerSecond` are discarded **before** any GPU readback.
- `FrameMetadata.Sequence` counts frames *arrived* in the session, so gaps mean dropped frames.
- Pixels exist in memory only. Nothing is written to disk or sent anywhere.

## Windows APIs used

| API | Use |
| --- | --- |
| `Windows.Graphics.Capture` (`GraphicsCaptureItem`, `Direct3D11CaptureFramePool.CreateFreeThreaded`, `GraphicsCaptureSession`) | Capture. |
| `IGraphicsCaptureItemInterop` (`CreateForWindow`, `CreateForMonitor`) | Create a capture item for an HWND or HMONITOR. |
| `CreateDirect3D11DeviceFromDXGIDevice`, `IDirect3DDxgiInterfaceAccess` | Bridge between the WinRT surface and `ID3D11Texture2D`. |
| Direct3D 11 (`D3D11CreateDevice`, staging texture, `CopyResource`, `Map`, `Flush`) | GPU frame readback. |
| `EnumDisplayMonitors`, `GetMonitorInfo`, `GetDpiForMonitor` | Display enumeration, size, DPI. |
| `EnumWindows`, `DwmGetWindowAttribute` (cloaked, extended frame bounds), window styles | Window enumeration and filtering. |
| `SetThreadDpiAwarenessContext` | Enumerate in physical pixels even from a non-DPI-aware host. |
| `IsWindow`, `GetMonitorInfo` | Target liveness watchdog. |

### Why COM interop exists

WGC's WinRT surface cannot create a capture item for an arbitrary HWND/HMONITOR, so the activation factory's
`IGraphicsCaptureItemInterop` is required, and a WinRT surface must be converted back to a Direct3D texture through
`IDirect3DDxgiInterfaceAccess`. These are called through vtable function pointers (no `[ComImport]`), with every raw
interface pointer released before the method returns; the returned managed wrappers own their own references.

## Dependencies

| Dependency | Version | License | Why | Notes |
| --- | --- | --- | --- | --- |
| Microsoft Windows SDK .NET projection (CsWinRT) | implicit with TFM | Microsoft | `Windows.Graphics.Capture` types | Supplied by the SDK for the `windows10.0.19041.0` TFM; no extra package reference. |
| `Vortice.Direct3D11` (+ `Vortice.DXGI`, `SharpGen.Runtime`) | 3.8.3 | MIT | Direct3D 11 / DXGI bindings | Actively maintained (last push 2026-10-05); ships a `net10.0` target; no known vulnerabilities (`dotnet list package --vulnerable`). Its transitive `SharpGen.Runtime` is **2.4.2-beta**, a prerelease; revisit when a stable release exists. |

Considered and not used: hand-written Direct3D 11 COM vtables (error-prone for ~7 interfaces), and a GDI
`BitBlt`/`CopyFromScreen` fallback (explicitly out of scope: WGC is required).

## Target framework

`GlyphLens.Windows`, `GlyphLens.App` and their test projects target `net10.0-windows10.0.19041.0`, so the minimum
supported OS is **Windows 10 version 2004 (build 19041)**, the first build supporting monitor capture through
`CreateForMonitor`. `GlyphLens.Core` and `GlyphLens.Infrastructure` stay `net10.0`.

## Coordinate system

- Captured frames are in **physical pixels**, origin at the **top-left of the target**, Y down. They are never
  scaled by display DPI.
- `ScreenRectangle` and `CaptureTarget.Bounds` are in **virtual-screen physical pixels**; X/Y can be negative (a
  monitor left of or above the primary one).
- For a display capture, `FrameMetadata.ScreenBounds` is the display's rectangle, so frame pixel `(x, y)` is
  virtual-screen pixel `(ScreenBounds.X + x, ScreenBounds.Y + y)`. For a window capture `ScreenBounds` is `null`
  (windows move and the frame includes DWM-managed borders; a per-frame mapping is future work).
- The process is **PerMonitorV2** DPI aware (`app.manifest`). `CaptureTargetProvider` also switches the calling
  thread to PerMonitorV2 while enumerating, so sizes are physical pixels even in a non-aware host (verified: a
  test host that is not DPI aware reports target sizes equal to the delivered frame sizes).
- **Physical pixels and WPF device-independent units are not interchangeable.** Use `DpiScale` (per display) to
  convert. The preview bitmap is declared at 96 DPI, so one frame pixel is one layout unit and WPF's
  `Stretch.Uniform` does all scaling; frame pixels are never reinterpreted as DPI-scaled units.

## Error handling

| Situation | `CaptureErrorKind` | Behaviour |
| --- | --- | --- |
| WGC not supported | `Unsupported` | Start fails; service `Faulted`. |
| Target gone or unknown at start | `TargetNotFound` | Start fails; the UI refreshes its target list. |
| Window closed / display disconnected during capture | `TargetClosed` | Detected by the watchdog (see below); capture stops, resources released, `Faulted`. |
| Window of an elevated process | `AccessDenied` | Start fails with an explanation. |
| GPU reset/removed | `DeviceLost` | Capture stops; the shared device is discarded and recreated on the next start. |
| Repeated frame failures | `FrameProcessing` | Faulted after `MaxConsecutiveFrameFailures` (default 5). |
| Anything else | `Unexpected` | Generic message; technical detail kept separately (`CaptureError.Detail`, shown as a tooltip). |
| User cancels / app exits during start | (none) | `OperationCanceledException`; status returns to `Idle`; not shown as an error. |

Application shutdown calls `DisposeAsync`, which stops the capture and releases resources (bounded to 5 s).

## Lifecycle rules

- `Idle → Starting → Capturing → Stopping → Idle`; `Starting`/`Capturing → Faulted`; `Faulted → Starting | Idle`.
  Enforced by `CaptureStateMachine` and tested for every state pair.
- Start/stop are serialised; each start creates a **new** session, each stop/fault disposes it completely.
- After `Dispose`, a session never invokes its callbacks again (callbacks take the same lock and observe the
  disposed flag). Late callbacks from a replaced session are ignored by the service.

## Findings from hardware verification

Two defects were found by running on real hardware and fixed; both are covered by regression tests.

1. **`GraphicsCaptureItem.Closed` is unreliable.** Killing the captured window's process raised no event for 6 s.
   Windows delivers it through a window-event hook that needs a message-pumping thread, and sessions are created on
   thread-pool threads. A **liveness watchdog** (500 ms; `IsWindow` / monitor validity) now reports `TargetClosed`;
   the event remains as a secondary, faster signal.
2. **Kernel handle leak (2 `Section` handles per start/stop).** The frame pool's shared textures are destroyed
   lazily by Direct3D 11 and WGC releases them asynchronously after `Close()`. With a per-session device that was
   destroyed immediately, they leaked until process exit. Fix: **one Direct3D device for the service's lifetime**,
   flushed at each start and after each stop following a short delay, a garbage collection (to finalize the
   projected WinRT objects) and a flush. Measured: 2.00 handles/cycle before, 0 after (30 cycles, integration test).

## Known limitations

- **Region capture is not implemented.** It will be a separate selection feature (next capture milestone).
- Minimised windows deliver no frames (they are excluded from the window list). The list is a snapshot; use Refresh.
- Protected (DRM) content may render black; WGC does not report that, so it cannot be detected.
- Windows of elevated processes cannot be captured unless GlyphLens is elevated; elevation is deliberately not requested.
- Capturing a display that shows the GlyphLens window itself produces an infinite-mirror effect (the window is
  excluded from the window list, not from display capture). Excluding the app from capture is planned with the overlay.
- Windows draws its own capture indicator border by default; GlyphLens does not change this (not visually verified).
- One display and one GPU were available during development; multi-monitor, mixed-DPI and portrait behaviour is
  covered by unit tests of the math and logic only (see the manual checklist).
- A background full garbage collection runs once per stop (see finding 2). Stopping is rare and user-initiated.
- If no hardware GPU is available, the WARP software device is used (not exercised).

## Privacy

Frames stay in memory, are never saved or transmitted, there is no telemetry, the capture status is always shown in
the UI, capture stops when the app exits, and no administrator rights are requested. See [SECURITY.md](SECURITY.md).
