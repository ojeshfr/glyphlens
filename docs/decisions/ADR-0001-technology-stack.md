# ADR-0001: Technology stack — .NET 10 LTS, C# and WPF

- **Status:** Accepted (Phase 0)
- **Date:** 2026-11-19

## Context

GlyphLens is a Windows-only desktop tool that must capture the screen, read UI Automation trees,
draw transparent click-through overlays and manage a local worker process. It targets Windows 10
and 11 x64 and must stay maintainable for years.

## Decision

- **Language/runtime:** C# on **.NET 10 (LTS)**. Not .NET 11, because long-term support and
  stability outweigh newer features for this project.
- **UI:** **WPF** with the MVVM pattern, enabled only in `GlyphLens.App`.
- **Target frameworks:** `net10.0` for platform-independent projects (Core, Infrastructure);
  `net10.0-windows10.0.19041.0` for Windows-specific projects (Windows, App) — initially `net10.0-windows`, versioned in
  Phase 1 for Windows Graphics Capture.
- **Platform:** x64 only.

## Alternatives considered

| Option | Why not chosen |
| --- | --- |
| WinUI 3 / Windows App SDK | Modern, but heavier packaging, weaker support for custom transparent/click-through windows, and a less mature desktop tooling story. |
| WinForms | Poor fit for transparent layered overlays and modern layout. |
| Electron / web stack | Large footprint; Win32 interop (capture, UIA, hotkeys, click-through) needs native modules. |
| C++/WinRT | Best native access, but slower development and a smaller maintainer pool. |
| Rust + native UI | Strong, but immature desktop UI and UI Automation ergonomics. |
| .NET 11 | Not LTS. |

## Advantages

- First-class Win32/WinRT interop and a mature UI Automation managed API.
- WPF gives precise control over transparent, topmost, click-through windows.
- LTS support window; strong tooling, analyzers and xUnit support.
- Easy separation of platform-independent logic (testable on any OS).

## Disadvantages

- WPF is Windows-only and in maintenance mode (stable, but receives few new features).
- WPF text rendering and DPI handling need care (mitigated by a PerMonitorV2 manifest).
- Windows Graphics Capture is a WinRT API, so the Windows-specific projects moved to the versioned TFM
  `net10.0-windows10.0.19041.0` in Phase 1 (this was validated: the SDK then supplies the WinRT projection and the minimum
  OS becomes Windows 10 2004). See [ADR-0005](ADR-0005-windows-graphics-capture.md).

## Consequences

- `GlyphLens.Core` must not reference Windows or WPF; this is enforced by tests.
- Overlay work in Phase 5 relies on Win32 extended window styles from WPF.
- A future cross-platform UI is out of scope; Core and Infrastructure keep that door open.

## Assumptions requiring validation

- Windows Graphics Capture projections work cleanly with the `net10.0-windows` + versioned TFM
  combination (validate in Phase 1).
- WPF overlay windows can reliably be excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) on the
  minimum supported Windows 10 build (validate in Phase 5).
