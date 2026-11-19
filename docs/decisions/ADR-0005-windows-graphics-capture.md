# ADR-0005: Windows Graphics Capture with a shared Direct3D device

- **Status:** Accepted (Phase 1)
- **Date:** 2026-11-19

## Context

GlyphLens needs real-time capture of displays and individual windows on Windows 10 and 11, feeding future OCR,
without administrator rights and without saving or sending screen content. The Phase 0 contract was a one-shot
`CaptureRegionAsync` returning an unpooled buffer.

## Decision

- Use **Windows Graphics Capture** (`Windows.Graphics.Capture`) with a **free-threaded** frame pool and a Direct3D 11
  device, reading frames back through a **staging texture** into pooled CPU buffers.
- Take WinRT types from the SDK's projection by targeting `net10.0-windows10.0.19041.0`; no WinRT wrapper package.
- Use **Vortice.Direct3D11** (MIT) for Direct3D 11/DXGI; call `IGraphicsCaptureItemInterop` and
  `IDirect3DDxgiInterfaceAccess` through vtable function pointers.
- Replace the one-shot contract with a session service plus a **single-slot, latest-wins, reference-counted** frame
  publication (`IFrameSource`) so consumers can never create an unbounded queue.
- Own **one Direct3D device per service**, shared by successive sessions, flushed around each session.
- Detect target closure with a **liveness watchdog** in addition to `GraphicsCaptureItem.Closed`.
- Cap processing at 30 fps by default, discarding excess frames before GPU readback.

## Alternatives considered

| Option | Why not chosen |
| --- | --- |
| GDI `BitBlt` / `CopyFromScreen` | Not hardware-accelerated, misses many modern windows, cannot capture occluded windows. Explicitly excluded. |
| DXGI Desktop Duplication | Display-only (no per-window capture), one duplication per output, more fragile around session changes. |
| Per-session Direct3D device | Leaks the pool's kernel Section handles (measured 2 per cycle); see below. |
| Hand-written Direct3D 11 COM interop | About seven interfaces with exact vtable layouts; high defect risk versus a maintained MIT binding. |
| `Windows.Graphics.Capture` through a picker only | The system picker cannot be driven from our own target list and is user-modal. |
| Unbounded or N-deep frame queue | Latency grows when OCR is slower than capture; stale frames are worthless to a live translator. |

## Advantages

- Hardware-accelerated, works for occluded windows and modern UI frameworks, supported by Microsoft.
- Bounded memory and latency by construction.
- Native resources are released deterministically and verified by a regression test.

## Disadvantages

- Minimum OS is Windows 10 2004 (19041).
- COM interop and a transitive **prerelease** dependency (`SharpGen.Runtime` 2.4.2-beta) must be maintained.
- Stopping triggers one background full GC to make COM release deterministic.
- GPU readback costs a copy per processed frame (bounded by the frame-rate cap).
- Frames cannot reveal DRM-protected (black) content.

## Consequences

- `GlyphLens.Windows` and `GlyphLens.App` moved to a versioned Windows TFM; `Core` and `Infrastructure` did not.
- The OCR milestone consumes `IFrameSource.WaitForNewerAsync`; it must dispose every frame it acquires.
- Region capture and window-to-screen coordinate mapping are future work.

## Evidence (hardware findings that shaped this decision)

- `GraphicsCaptureItem.Closed` did not fire after the captured window's process was killed (6 s observed) when the
  item was created on a thread-pool thread, hence the watchdog.
- With a per-session device, kernel `Section` handles grew by exactly the pool buffer count per cycle and were freed
  only by a context flush (`Flush` released 60 of 60 in a controlled test). A shared device with
  wait → finalize → flush measured 0 growth over 30 cycles.

## Assumptions requiring validation

- Behaviour on multi-monitor, mixed-DPI and portrait setups (only math is unit-tested).
- Behaviour on Windows 10, on hybrid-GPU laptops with display switching, and with the WARP fallback.
- Whether the capture border can or should be disabled in a later release (currently left at the Windows default).
- A stable `SharpGen.Runtime` release replacing the beta.
