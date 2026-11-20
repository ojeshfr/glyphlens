# GlyphLens Architecture

> Status: the module structure, contracts and dependency rules described here exist, and the
> **capture** stage (section 4) is implemented for displays and windows (Phase 1). Every other
> pipeline stage is **planned**. Sections marked *(planned)* describe intended design, which later
> phases may refine through ADRs.

## 1. High-level architecture

GlyphLens is two cooperating local processes:

```
┌──────────────────────────────── GlyphLens.exe (.NET 10, WPF) ────────────────────────────────┐
│                                                                                               │
│   GlyphLens.App  ── MVVM shell, overlay windows, side panel, composition root                 │
│        │                                                                                      │
│        ├── GlyphLens.Windows        capture · UI Automation · displays/DPI · hotkeys          │
│        ├── GlyphLens.Infrastructure IPC connection · worker manager · SQLite · cache (later)  │
│        └── GlyphLens.Core           models · service contracts · protocol version             │
│                                                                                               │
└───────────────────────────────────────────────┬───────────────────────────────────────────────┘
                                                │  Windows Named Pipe (local, current user only)
                                                │  versioned UTF-8 JSON messages
┌───────────────────────────────────────────────┴───────────────────────────────────────────────┐
│ GlyphLens.Worker.exe (packaged Python)                                                        │
│   runtime (pipe client, dispatch; cancellation planned)                                       │
│   engines/ocr (PaddleOCR) · engines/language_detection · engines/translation (Argos Translate)│
│   models (installed model metadata, provenance, integrity)                                    │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Nothing leaves the machine. There is no server component.

## 2. Module responsibilities

| Module | Target | Responsibility | Must not |
| --- | --- | --- | --- |
| `GlyphLens.Core` | `net10.0` | Domain models (`ScreenTextRegion`, `TranslationRequest`, …), service interfaces (including `IOcrEngine`, `IOcrLanguageSource`, `OcrException`), `ProtocolVersion`. | Reference WPF, Windows APIs, OCR/translation engines, or any other GlyphLens project. |
| `GlyphLens.Windows` | `net10.0-windows10.0.19041.0` | Windows adapters: Windows Graphics Capture (implemented), monitor/window enumeration (implemented), Windows OCR (`WindowsOcrEngine`, implemented in Phase 2b), the worker pipe/launcher (implemented), UI Automation, global hotkeys. Implements `IScreenCaptureService`, `ICaptureTargetProvider`, `IOcrEngine` + `IOcrLanguageSource` (Windows OCR), `IAccessibilityTextSource` (not yet). | Use WPF; reference App or Infrastructure. |
| `GlyphLens.Infrastructure` | `net10.0` | Worker IPC (implemented: envelope codec, framing, `WorkerConnection`, `WorkerProcessManager`; the Windows-only pipe server and launcher live in `GlyphLens.Windows/Worker`), later SQLite persistence, translation cache, logging, settings. Implements `IWorkerProcessManager`; will implement `ILanguageDetector`, `ITranslationEngine` and a PaddleOCR `IOcrEngine` as IPC proxies and `ITranslationCache` (not yet). | Reference WPF, App or Windows. |
| `GlyphLens.App` | `net10.0-windows10.0.19041.0` (WPF, x64) | Views, view models, overlay and side-panel windows, composition root. Produces `GlyphLens.exe`. | Contain OCR/translation/detection logic or business logic in code-behind. |
| `worker/glyphlens_worker` | Python ≥ 3.11 | OCR, language identification, translation, model management behind the IPC protocol. | Own any UI; make network requests at inference time; persist screen content. |

## 3. Dependency directions

```
GlyphLens.App ──► GlyphLens.Windows ────────┐
      │                                     ▼
      ├──────────► GlyphLens.Infrastructure ─► GlyphLens.Core
      │                                     ▲
      └─────────────────────────────────────┘

Python worker ◄── (IPC contract only, no code dependency) ── GlyphLens.Infrastructure
```

Rules:

1. Dependencies point **inward** to `GlyphLens.Core`. Core depends on nothing but the BCL.
2. `GlyphLens.Windows` and `GlyphLens.Infrastructure` are siblings and never reference each other.
   When an adapter needs another capability it depends on the Core interface, and the App wires it.
3. Only `GlyphLens.App` references WPF and composes concrete implementations.
4. The .NET and Python sides share **only** the protocol defined in [IPC-PROTOCOL.md](IPC-PROTOCOL.md).
5. No circular references (enforced by MSBuild; boundary rules are also enforced by the
   architecture tests in `tests/*/Architecture/`).

## 4. Capture pipeline *(implemented for displays and windows — Phase 1; region capture planned)*

Full detail, APIs, error handling and measured findings: [CAPTURE.md](CAPTURE.md).

1. `CaptureTargetProvider` (`GlyphLens.Windows/Display`) enumerates displays (bounds in the virtual
   screen, DPI scale) and capturable top-level windows.
2. `WindowsGraphicsCaptureService` (`GlyphLens.Windows/Capture`) runs one Windows Graphics Capture
   session at a time. Each frame is read back from the GPU into a pooled BGRA32 `CapturedFrame`
   (physical pixels).
3. Frames are published to a single-slot, latest-wins `LatestFrameMailbox` (`IFrameSource`).
   Consumers (the preview and the Windows OCR text recognition) acquire a reference-counted frame; obsolete frames are
   dropped and released, so there is never a backlog.
4. Frames stay in memory and are never written to disk.
5. Change detection (Phase 7) will avoid re-processing unchanged regions.

The process is per-monitor-v2 DPI aware (`app.manifest`), so all coordinates in Core are physical
pixels; conversion to WPF device-independent units happens only in the App (`DpiScale`) when
positioning overlays.

## 5. Accessibility and OCR pipeline *(OCR implemented with Windows OCR — Phase 2b; accessibility and PaddleOCR planned)*

Two independent sources produce `ScreenTextRegion`s for the same screen area:

- **Accessibility** (`IAccessibilityTextSource`, UI Automation): walks the element tree of the
  target window, reads `Name`/`Value`/`TextPattern` text and `BoundingRectangle`. Precise, cheap,
  exact text — but only where apps expose it.
- **OCR** (`IOcrEngine`): reads the pixels of a `CapturedFrame` and returns text regions. Works on any pixels,
  including images, video and canvas UIs.
  - **Implemented (Phase 2b): `WindowsOcrEngine`** (`GlyphLens.Windows/Ocr`) uses the built-in Windows OCR
    (`Windows.Media.Ocr`). It runs in the app process, offline, with no downloads and no extra packages. It recognises the
    OCR language packs installed in Windows (`IOcrLanguageSource.AvailableLanguages`); a language can be selected, and the
    default is the user's profile languages. If none is installed it throws `OcrException` (`EngineUnavailable` /
    `LanguageNotInstalled`) with a message pointing at Windows Settings. Frames larger than 10 000 px per side
    (`OcrEngine.MaxImageDimension`) are refused (`FrameTooLarge`). A single Windows `OcrEngine` rejects overlapping
    `RecognizeAsync` calls, so the wrapper serialises them (concurrent callers take turns).
  - **Output:** one `ScreenTextRegion` per recognised line, with pixel bounds **relative to the frame** (the union of the
    line's word boxes, rounded outward and clamped to the frame; add `FrameMetadata.ScreenBounds` for virtual-screen pixels
    of a display capture), `DetectionSource.Ocr`, the recognition language tag as `SourceLanguage`, and the frame's
    timestamp. Windows OCR reports **no confidence**, so `Confidence` is `null` (`ScreenTextRegion.Confidence` is
    `double?`); no value is invented. Text orientation is not reported.
  - **Planned:** PaddleOCR in the Python worker (`ocr.detect` over the pipe) as a second, multilingual engine behind the
    same interface.

Each region carries its `DetectionSource`, an optional confidence in [0, 1] (`null` when the engine reports none) and a
timestamp.

### Text recognition in the app (Phase 2b)

`OcrViewModel` (App) reads the **latest** frame from the capture service's `IFrameSource` (it acquires a reference-counted
frame and releases it afterwards):

- **Read text** recognises the current frame once.
- **Live** repeats it: it starts from version 0 so the frame already on screen is read immediately (display capture only
  produces a frame when the picture changes), then follows newer frames, never faster than a minimum interval (default
  600 ms). It reads one frame at a time and skips the frames that arrived meanwhile, so work is never queued. It stops on an
  engine error (instead of repeating it every cycle), when the user turns it off, when capture stops, and on disposal.
- **Results** are shown as a list (text plus pixel bounds) and as boxes over the preview. Boxes are drawn by `OcrOverlay` in
  frame pixels inside the **same `Viewbox` as the preview image**, so picture and boxes scale together and always line up.
  `OverlayState` holds the boxes and the frame size they refer to.
- **Copy** sends the recognised text to the clipboard only when the user presses it.
- Results, boxes and live reading are **cleared when capture stops or faults**, so stale text is never left on screen.
- Recognised text is kept only in memory (the view model and overlay state); it is never logged or persisted, and error
  messages never contain it.

## 6. Text fusion *(planned — Phase 3)*

A fusion stage merges both sources into one de-duplicated set of regions:

- Prefer accessibility text where its bounds overlap OCR text (exact characters, no recognition errors).
- Keep OCR-only regions where accessibility exposes nothing.
- Group fragments into lines/blocks suitable for translation.
- Run language detection (`ILanguageDetector`) on fused blocks; skip text already in the target language.

Fusion is platform-independent logic and is expected to live in a Core-level (or new
application-layer) project, so it is unit-testable without Windows or the worker.

## 7. Translation pipeline *(planned — Phase 4)*

1. Build a `TranslationRequest` (text, detected/given source language, target `en`).
2. Look up `ITranslationCache` (keyed by text, languages and model identifier).
3. On a miss, call `ITranslationEngine` → worker `translation.translate` → Argos Translate.
4. Store the `TranslationResult` (includes `ModelIdentifier` for provenance and cache invalidation).
5. Stale requests are cancelled (`translation.cancel`) when the source text disappears from screen.

There is no cloud fallback. If no local model is installed for a language pair, the result is an
explicit "unavailable" state, surfaced to the user.

## 8. Overlay architecture *(planned — Phase 5)*

- Borderless, transparent, topmost WPF windows per monitor, made click-through with
  `WS_EX_TRANSPARENT | WS_EX_LAYERED` and excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) so
  GlyphLens never OCRs its own output.
- An overlay view model holds a collection of positioned translation labels; the view renders them.
- Screen-pixel coordinates are converted to the monitor's DIPs at render time.
- Appearance (font, opacity, background) comes from user settings.

## 9. Side-panel architecture *(planned — Phase 6)*

- A normal WPF window with a live feed of `(original, translation, source language, time)` items.
- Consumes the same pipeline output as the overlay; switching modes does not change detection.
- History is opt-in and stored in SQLite; copy-to-clipboard per item.

## 10. Worker architecture *(transport and lifecycle implemented — Phase 2a; engines planned — Phases 2–4)*

**Implemented (Phase 2a):**

- `WorkerProcessManager` (Infrastructure, implements `IWorkerProcessManager`) starts, supervises and stops the worker. States:
  `Stopped → Starting → Ready`, or `Error` / `Unavailable`. `Ready` means process + pipe + handshake, not that an engine works.
- Layering keeps `GlyphLens.Infrastructure` platform-independent: it holds framing, `WorkerConnection` and the manager, and depends
  on Core interfaces (`IWorkerPipeListenerFactory`, `IWorkerProcessLauncher`, `IWorkerLog`). The Windows-only parts live in
  `GlyphLens.Windows/Worker`: `NamedPipeWorkerListener` (ACL-restricted server pipe), `WorkerJobObject` (kill-on-close),
  `PythonWorkerProcessLauncher` and `WorkerLocator`. The App composes them in `App.xaml.cs`.
- The app hosts the pipe server; the Python worker (`worker/glyphlens_worker`) is the single client, authenticated by a per-session
  token. Messages: `worker.initialize`, `worker.health`, `worker.shutdown`. Full detail: [IPC-PROTOCOL.md](IPC-PROTOCOL.md).
- Failure handling: crash/pipe-loss detection, bounded automatic restart with backoff, startup/connect/request timeouts,
  cancellation, Job Object so the worker cannot outlive the app.
- The App shows a worker diagnostics panel (`WorkerViewModel`/`WorkerView`): start, stop, restart, refresh health, status, process id,
  protocol, the worker's health rows and the last error. The worker is never started automatically.

**Not implemented:** OCR, language detection, translation, model management (all reported `not_implemented` / `not_installed` by
`worker.health`), periodic health polling, and request types beyond the three above (they return `engine_unavailable`).

**Planned:**

- Engines load lazily on `worker.initialize`; models come only from the local model store.
- The worker never opens network connections during inference. Model download, when implemented,
  is a separate explicit user action with integrity verification. Engine dependency compatibility:
  [research/PYTHON-ENGINE-COMPATIBILITY.md](research/PYTHON-ENGINE-COMPATIBILITY.md).

See [IPC-PROTOCOL.md](IPC-PROTOCOL.md) and [ADR-0004](decisions/ADR-0004-worker-communication.md).

## 11. Data flow *(planned)*

```
Screen ──► Capture (Windows) ──► CapturedFrame ──► ocr.detect ──► OCR regions ─┐
   │                                                                          ├─► Fusion ─► Language detection
   └────► UI Automation (Windows) ──────────────────────────► Accessibility regions ┘                │
                                                                                                    ▼
                                         Overlay / Side panel ◄── TranslationResult ◄── Cache ◄─► translation.translate
```

Implemented so far: Capture → `CapturedFrame` → Windows OCR → regions (results list and preview overlay). The
`ocr.detect` hop to the worker, fusion with accessibility text, language detection, translation and the presentation modes
are planned.

Persistent data (all local, SQLite, Phase 4+): translation cache, user settings, optional history,
installed-model metadata, language preferences. Screen pixels are never persisted.
