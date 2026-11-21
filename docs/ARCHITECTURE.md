# GlyphLens Architecture

> Status: the module structure, contracts and dependency rules described here exist. **Capture**
> (section 4), **OCR** (section 5: Windows OCR and the worker's Chinese-capable PaddleOCR engine), **offline
> translation** (section 7), and the **floating overlay** (section 8) are implemented. Accessibility text,
> text fusion and the side panel are **planned**. Sections marked *(planned)* describe intended design,
> which later phases may refine through ADRs.

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
│   engines/language_detection (script rules; Lingua/py3langid optional) · engines/translation │
│   (CTranslate2 + SentencePiece on Argos packages) · engines/ocr (PaddleOCR PP-OCRv5, ONNX RT)│
│   models (local model store: validated import, manifest, hashes)                              │
└───────────────────────────────────────────────────────────────────────────────────────────────┘
```

Nothing leaves the machine. There is no server component.

## 2. Module responsibilities

| Module | Target | Responsibility | Must not |
| --- | --- | --- | --- |
| `GlyphLens.Core` | `net10.0` | Domain models (`ScreenTextRegion`, `TranslationRequest`, `LanguageDetectionResult`, `RouteStatus`, …), service interfaces (`IOcrEngine`, `ILanguageDetector`, `ITranslationEngine`, `ITranslationCache`, `IModelCatalog`, `IWorkerRequestChannel`, …), the translation pipeline (`TranslationPipeline`, `TextNormalizer`, `LruTranslationCache`), `ProtocolVersion`. | Reference WPF, Windows APIs, OCR/translation engines, or any other GlyphLens project. |
| `GlyphLens.Windows` | `net10.0-windows10.0.19041.0` | Windows adapters: Windows Graphics Capture (implemented), monitor/window enumeration (implemented), Windows OCR (`WindowsOcrEngine`, implemented in Phase 2b), the worker pipe/launcher (implemented), UI Automation, global hotkeys. Implements `IScreenCaptureService`, `ICaptureTargetProvider`, `IOcrEngine` + `IOcrLanguageSource` (Windows OCR), `IAccessibilityTextSource` (not yet). | Use WPF; reference App or Infrastructure. |
| `GlyphLens.Infrastructure` | `net10.0` | Worker IPC (implemented: envelope codec, framing, `WorkerConnection`, `WorkerProcessManager`; the Windows-only pipe server and launcher live in `GlyphLens.Windows/Worker`), later SQLite persistence, translation cache, logging, settings. Implements `IWorkerProcessManager` and `IWorkerRequestChannel`, and the IPC proxies `WorkerLanguageDetector`, `WorkerTranslationEngine` and `WorkerModelCatalog` (`Translation/`). `WorkerOcrEngine` is the OCR proxy. | Reference WPF, App or Windows. |
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

## 5. Accessibility and OCR pipeline *(Windows OCR and worker OCR implemented; accessibility planned)*

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
  - **`WorkerOcrEngine`** (Infrastructure) sends the frame as PNG (`PngFrameEncoder`, in memory) to the worker's `ocr.detect` and returns
    the lines with the recogniser's own confidence (PaddleOCR PP-OCRv5 mobile on ONNX Runtime: Simplified and Traditional Chinese, English,
    Japanese, digits). `OcrEngineSelector` lists it ("Chinese + English (local OCR worker)") next to the Windows OCR languages in the
    OCR language list and routes each recognition. Measured: 1.2 % CER on 10 real window captures, 0.4-1.3 s per window.

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

## 7. Translation pipeline *(implemented)*

`TranslationPipeline` (Core) turns the regions of one recognition pass into translations. It depends only on Core interfaces
(`ILanguageDetector`, `IModelCatalog`, `ITranslationEngine`, `ITranslationCache`), so it is unit-tested with fakes and has no
knowledge of Python or Argos.

1. **Normalise** each region's text (`TextNormalizer`: Unicode NFC, collapsed whitespace, case kept). Identical texts in one pass are
   handled once.
2. **Source language**: the user's manual choice, or `ILanguageDetector` → worker `language.detect` (Lingua / py3langid). A
   detection is trusted only when the detector's own probability is at least the threshold (default 0.6); otherwise the region is
   `LanguageUncertain` with the alternatives and the user can choose the language. A detector that reports no probability is trusted
   without inventing one. Detections are remembered in a small in-memory LRU so unchanged on-screen text is not identified again.
3. **Skip** text that needs no translation: no letters (`NoText`) and text already in the target language (`AlreadyTarget`, so
   English is never re-translated).
4. **Route**: `IModelCatalog.GetRouteStatusAsync` (worker `models.status`) says whether a local model path exists (direct or via a
   pivot) and returns its `ModelVersion`. No model → `ModelMissing` with the required package name; unsupported language →
   `UnsupportedLanguage`. Nothing is translated "best effort".
5. **Cache**: `ITranslationCache` (`LruTranslationCache`, bounded, in memory only, never persisted) keyed by
   *(normalised text, source, target, model version)*. Installing or replacing a model changes the version, so stale results are
   unreachable; `InvalidateModel` and `Clear` also exist.
6. **Translate** the misses in one `TranslationEngine.TranslateBatchAsync` call (worker `translation.translate`, at most 8 items
   per request, one request in flight). A failing item does not fail the others.
7. **Result**: one `RegionTranslation` per input region, in order, carrying the original `ScreenTextRegion` (identity and
   frame-relative coordinates), the status, the translated text, the source language and the detector's confidence.

Passes are serialised and a pass waiting for its turn honours cancellation, so a caller that cancels the previous pass before
starting the next never builds a backlog. The worker handles one request at a time over a blocking pipe, so cancelling an
in-flight translation means abandoning its result (batches are small, so it finishes within about a second); `translation.cancel`
stays reserved. There is no cloud fallback.

**In the app** (`TranslationViewModel`): translation follows every recognition pass (one-off or Live). A newer pass cancels the
older one; rows whose text is already known keep their translation while the new pass runs (only changed rows change); the Models
panel lists the installed packages, the storage folder and imports a user-chosen `.argosmodel` file. Turning translation on starts
the worker if it is stopped. Caches are cleared when capture stops.

## 8. Overlay architecture *(implemented — first version)*

The floating overlay is the primary translation view; the main window configures it (capture target, OCR engine, languages, models,
worker diagnostics) and shows a "Translation overlay" card with Show / Hide / mode / pause and the shortcut status.

- `OverlayWindow` (App, `Overlay/`): a borderless, transparent, light-themed WPF window (400x560 DIPs by default, minimum width 320,
  resizable, draggable by its header) that is **not** a page of the main window. It is `Topmost` while pinned, has `WS_EX_TOOLWINDOW`
  and `WS_EX_NOACTIVATE` (no taskbar entry, no focus stealing; `WM_MOUSEACTIVATE` returns `MA_NOACTIVATE`), resizes through
  `WM_NCHITTEST`, collapses to its header, has an opacity slider (0.35-1) and is hidden/shown without closing the application.
- **Two input modes.** *Interactive*: the window receives the mouse (select text, copy, move, resize). *Pass-through*: `WS_EX_TRANSPARENT`
  is set so clicks go to the window underneath; it is applied only while the user chose it. A pass-through window cannot be clicked, so a
  small always-clickable `OverlayHandleWindow` appears beside it to switch back (and Ctrl+Alt+I does the same).
- **Self-capture prevention.** `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` (Windows 10 2004, build 19041, or newer) is applied and
  read back; the overlay shows whether it is on. A window capture never contains the overlay anyway. If the call fails (an older
  build) the overlay says so and recommends capturing a window instead of the display; **a region-masking fallback is not
  implemented.** Verified on Windows 11 build 26200 with a control run (see TESTING.md); NOT tested on other builds.
- `OverlayViewModel` does no OCR and no translation: it listens to `TranslationViewModel.TranslationsPublished` and keeps at most 200
  `OverlayCard`s (newest last). A card is updated in place when the same text, or the same screen region with changed text, comes
  back, so a Live update changes only the cards that changed and the list never rebuilds. Cards are memory-only; "Clear" empties them.
  Original/translation copy buttons are per card; the OCR confidence is shown only when the recogniser reported one.
- `OverlayPlacement` (pure, tested): positions are physical pixels of the virtual desktop; a saved position on a disconnected monitor
  moves to the top-right of the primary monitor, a partly off-screen window is pulled back, sizes are clamped. `OverlaySettingsStore`
  keeps only the layout in `%LOCALAPPDATA%\GlyphLens\overlay.json` (never text). A display-settings change re-fits the window.
- `GlobalHotkeyService` registers Ctrl+Alt+T (translation), O (overlay), P (pause), I (input mode), C (clear results) with
  `RegisterHotKey` through a message-only window; a shortcut another program owns is reported in the main window, never forced.
- Lifecycle: `ShutdownMode = OnExplicitShutdown`. Closing the main window while the overlay is showing keeps the translation session
  alive; hiding the overlay after that ends the application (so it can never linger invisibly). "Settings" in the overlay reopens
  the main window.
- Not implemented: multi-monitor overlays (one overlay window; moving it between monitors works by dragging, tested only as logic),
  per-card region highlighting on the captured window, history beyond the session, a tray icon.

### 8.1 Main window, readiness, OCR selection and incremental OCR *(implemented)*

- **Main window = manager.** `MainWindow` is a navigation shell (`MainViewModel.SelectedPage`): *Overview* (readiness checklist and overlay controls), *Capture*
  (target, measured frame rate, text recognition, results), *Models* (translation models, OCR models folder, worker diagnostics) and *Settings* (overlay
  opacity/text size/pin, shortcuts, privacy, version). There is no History page because nothing is persisted. Colours come only from `Themes/Tokens.xaml`
  (a test forbids literal colours elsewhere); see [DESIGN.md](../DESIGN.md).
- **Readiness** (`ReadinessViewModel`): rows for Python, worker, Chinese OCR, the zh/zt models, an offline check and capture, each derived from the worker's health,
  the model catalogue, the capture status or `IProcessConnectionProbe` (`WindowsConnectionProbe`: owning-process TCP/UDP tables). "Not checked yet" is shown instead of
  guessing; actions are real (start worker, import model, open OCR folder).
- **OCR engine choice** (`LocalOcrAdvisor` pure policy + `OcrEngineAdvisor`): when Chinese is the language and the worker reports the local OCR ready it is selected unless
  the user picked an engine; otherwise the missing part is named. The overlay header state comes from `OverlayStatusEvaluator`.
- **Recognition pipeline additions**: `OcrChromeFilter` sets aside title-bar glyphs and the window's own title; `IncrementalOcrEngine` (decorator, `FrameDiff` exact
  tile comparison + `DirtyRegionPlanner`) reuses an unchanged result and re-reads only changed areas widened to whole text lines, falling back to a full read for
  scrolling, large or scattered changes, text touching a changed area's edge, a different target/size/engine, every 12 partial passes and every 30 s
  (`GLYPHLENS_FULL_OCR=1` disables it).

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

**Implemented (offline translation milestone):**

- `language.detect`, `translation.translate`, `models.list`, `models.status`, `models.install` (see [IPC-PROTOCOL.md](IPC-PROTOCOL.md)).
- Translation engine: our own thin engine on `ctranslate2` + `sentencepiece` that runs Argos model packages directly. The
  `argostranslate` library is deliberately not used ([ADR-0006](decisions/ADR-0006-direct-argos-model-engine.md)): its first
  translation contacts GitHub and it needs about 1 GB of dependencies.
- Models live in `%LOCALAPPDATA%\GlyphLens\models` (override: `GLYPHLENS_MODELS_DIR`), one folder per language pair, imported only
  from a file the user chooses, through a validating extractor (zip-slip, symlinks, size/ratio caps, entry allow-list, staging and
  atomic move, SHA-256 manifest). Nothing is downloaded by the app or the worker.
- A fail-closed network guard is installed at worker start-up (Python-level only; native code is not covered).
- `worker.health` reports `languageDetection`, `translation` and `models` truthfully and cheaply (it never imports heavy libraries).
- Dependencies are optional extras (`translation`, `lingua`, `langid`); the base worker has none.

**Not implemented:** OCR in the worker (`ocr.detect`), `translation.cancel`, BPE-tokenizer packages (cs, da, es, fi, hu, id, lt, ro,
uk), periodic health polling.

**Planned:**

- Model download, when implemented, is a separate explicit user action with integrity verification. Engine dependency
  compatibility: [research/PYTHON-ENGINE-COMPATIBILITY.md](research/PYTHON-ENGINE-COMPATIBILITY.md) and
  [research/TRANSLATION-COMPATIBILITY.md](research/TRANSLATION-COMPATIBILITY.md).

See [IPC-PROTOCOL.md](IPC-PROTOCOL.md) and [ADR-0004](decisions/ADR-0004-worker-communication.md).

## 11. Data flow *(partly implemented)*

```
Screen ──► Capture (Windows) ──► CapturedFrame ──► ocr.detect ──► OCR regions ─┐
   │                                                                          ├─► Fusion ─► Language detection
   └────► UI Automation (Windows) ──────────────────────────► Accessibility regions ┘                │
                                                                                                    ▼
                                         Overlay / Side panel ◄── TranslationResult ◄── Cache ◄─► translation.translate
```

Implemented so far: Capture → `CapturedFrame` → Windows OCR **or** the worker's Chinese-capable OCR (`ocr.detect`, PNG over the pipe) →
regions (results list and preview boxes) → paragraph grouping → language identification (`language.detect`) → in-memory translation
cache → `translation.translate` → cards in the floating overlay and rows in the translation panel. The `ocr.detect` hop to the worker, fusion with accessibility text and the overlay/side-panel presentation modes
are planned.

Persistent data: only the imported translation models (and their manifests) are stored, in the local model directory. The
translation cache and language-detection cache are in memory only. Planned (Phase 6+): user settings, optional history. Screen
pixels and recognised text are never persisted.
