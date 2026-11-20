# GlyphLens Roadmap

Each phase ends with a green CI build and the completion criteria below met. Phases may overlap
where dependencies allow, but no phase is "done" without its tests.

---

## Phase 0 — Repository and foundation ✅ (current)

- **Scope:** Repository, solution, module boundaries, contracts, documentation, CI.
- **Deliverables:** .NET 10 solution (Core, Windows, Infrastructure, App), Python worker skeleton,
  domain models and interfaces, IPC envelope + validation, docs and ADRs 0001–0004, GitHub Actions, Dependabot.
- **Dependencies:** None.
- **Completion criteria:** Release build with zero warnings; all tests pass locally and in CI;
  architecture rules documented and enforced by tests; private GitHub repository published.
- **Testing requirements:** Model validation, protocol version parsing/compatibility, envelope
  serialization, dependency-boundary tests, worker diagnostics tests.

## Phase 1 — Windows screen capture 🟡 (display and window capture implemented; region capture and wider hardware verification remain)

**Implemented (see [CAPTURE.md](CAPTURE.md)):** Windows Graphics Capture of a display or a window; target
enumeration with sizes, DPI and primary flag; session lifecycle and a validated state machine; latest-wins,
reference-counted frame publication; frame-rate limiting; error mapping; liveness watchdog; WPF diagnostic UI
with live preview; automated, hardware (opt-in) and UI-Automation verification.
**Not yet done:** region capture; verification on multi-monitor, mixed-DPI, portrait, Windows 10 and elevated or
minimised windows (see [CAPTURE-MANUAL-TESTS.md](CAPTURE-MANUAL-TESTS.md)).

- **Scope:** Capture monitors, windows and regions with Windows Graphics Capture; monitor and DPI enumeration.
- **Deliverables:** `IScreenCaptureService` implementation in `GlyphLens.Windows/Capture`;
  display enumeration in `Display/`; virtual-screen ↔ per-monitor DIP conversion; versioned
  Windows TFM for CsWinRT; a developer-only diagnostic to verify capture locally (no frame persistence by default).
- **Dependencies:** Phase 0.
- **Completion criteria:** Correct BGRA frames on single and multi-monitor setups with mixed scaling
  (100/125/150/200 %), negative virtual-screen coordinates, and graceful handling of protected/minimised windows.
- **Testing requirements:** Unit tests for coordinate/DPI conversion and frame validation;
  integration tests that run only on interactive Windows sessions (skipped in headless CI) for real capture.

## Phase 2 — UI Automation and OCR

**Phase 2a (worker communication) implemented, test status not yet recorded:** Named Pipes transport with ACL and session-token
authentication, framing, `worker.initialize`/`health`/`shutdown`, `IWorkerProcessManager` with bounded restarts and a Job Object, the Python
pipe client, and the WPF worker diagnostics panel. See [IPC-PROTOCOL.md](IPC-PROTOCOL.md) and
[research/PYTHON-ENGINE-COMPATIBILITY.md](research/PYTHON-ENGINE-COMPATIBILITY.md). **Not implemented:** UI Automation, the PaddleOCR engine,
language detection, models, `ocr.detect`, periodic health polling.

**Phase 2b (text recognition with Windows OCR) implemented:** `WindowsOcrEngine` behind `IOcrEngine` (built-in Windows OCR,
offline, no downloads or packages, languages = the OCR packs installed in Windows), `IOcrLanguageSource`, `OcrException`,
and the app's Text recognition panel (language selector, Read text, Live, results list, boxes over the preview, Copy). One
region per recognised line with frame-relative pixel bounds; `ScreenTextRegion.Confidence` is now `double?` because Windows OCR
reports none. Verified on one machine with English only, using a controlled test window; see
[CAPTURE-MANUAL-TESTS.md](CAPTURE-MANUAL-TESTS.md) (section 3b). **Not yet done in Phase 2:** the UI Automation adapter, the
PaddleOCR engine in the worker (`ocr.detect`, model store), and verification of non-English text, rotated or small
low-contrast text, and real application content.

- **Scope:** Accessible text extraction; OCR in the worker; the Named Pipes transport (done in 2a).
- **Deliverables:** `IAccessibilityTextSource` (UI Automation) in `GlyphLens.Windows/Accessibility`;
  (done in 2a: pipe server in the app, pipe client in the worker, `IWorkerProcessManager`, `worker.*` handlers);
  (done in 2b: Windows OCR engine, `IOcrLanguageSource`, text recognition panel with overlay);
  remaining: UI Automation adapter, `ocr.detect` handler, PaddleOCR adapter, local model store layout.
- **Dependencies:** Phase 1; **validated PaddleOCR Windows wheels for the chosen Python version**
  (see ADR-0004).
- **Completion criteria:** Text with correct bounds from Chrome, Edge, a PDF reader and a native
  Win32 app via UIA; OCR regions from image/video content; worker survives restarts; no network
  traffic from the worker (verified).
- **Testing requirements:** Protocol contract tests shared by both sides; worker handler tests
  with a tiny bundled test image (license-clean, generated); UIA tests against a test WPF window;
  crash/timeout/cancellation tests for the process manager.

## Phase 3 — Text fusion and language detection

- **Scope:** Merge accessibility and OCR regions; group into translatable blocks; detect language.
- **Deliverables:** Fusion component (platform-independent); `ILanguageDetector` via worker.
- **Dependencies:** Phase 2.
- **Completion criteria:** Overlapping duplicates removed with accessibility preferred; stable
  block grouping; language identified for supported languages; English text skipped.
- **Testing requirements:** Table-driven fusion tests with synthetic regions; language-detection
  accuracy checks on a small, license-clean sample set.

## Phase 4 — Offline translation

- **Scope:** Local translation into English; model packages; cache.
- **Deliverables:** Argos Translate adapter and `translation.*` handlers; `models.*` handlers;
  `ITranslationEngine` proxy; SQLite `ITranslationCache`; explicit, verified language-package install flow.
- **Dependencies:** Phase 3; **Argos Translate + PaddleOCR validated together in one environment**.
- **Completion criteria:** Translation works with networking disabled; missing models produce a
  clear "unavailable" state; cache hits avoid worker calls; model provenance recorded.
- **Testing requirements:** Cache tests; cancellation tests; offline test (network disabled) in CI
  where possible; no model downloads in default CI runs.

## Phase 5 — Floating translation overlay

- **Scope:** Transparent click-through overlays positioned over source text.
- **Deliverables:** Overlay windows per monitor, excluded from capture; overlay view models;
  appearance settings; global hotkeys (`GlyphLens.Windows/Hotkeys`).
- **Dependencies:** Phases 1 and 4.
- **Completion criteria:** Accurate placement across DPI/multi-monitor setups; click-through
  verified; overlays never appear in GlyphLens's own captures; multiple regions at once.
- **Testing requirements:** View-model tests for layout math; manual test checklist for target apps.

## Phase 6 — Translation side panel

- **Scope:** Separate panel with live feed, history and copy.
- **Deliverables:** Side-panel window and view models; opt-in SQLite history with clear/retention controls.
- **Dependencies:** Phase 4 (pipeline); Phase 5 shares presentation infrastructure.
- **Completion criteria:** Both modes run from the same pipeline; history off by default or
  clearly consented; copy works.
- **Testing requirements:** View-model tests; persistence tests for history retention and deletion.

## Phase 7 — Performance and compatibility

- **Scope:** Latency, CPU/GPU use, memory, and app compatibility.
- **Deliverables:** Frame change detection; region-of-interest OCR; batching; optional GPU OCR;
  compatibility matrix for the target apps (WhatsApp Desktop, Instagram, Google Meet, Chrome,
  Brave, Edge, PDF readers).
- **Dependencies:** Phases 5 and 6.
- **Completion criteria:** Documented latency/CPU budgets met on reference hardware; no memory growth over long sessions.
- **Testing requirements:** Benchmarks (BenchmarkDotNet / timing harness); soak tests; compatibility checklist.

## Phase 8 — Packaging and release

- **Scope:** Installable, signed distribution.
- **Deliverables:** Packaged `GlyphLens.Worker.exe` (frozen Python); installer (e.g. MSIX);
  third-party license notices; license decision (see LICENSE-CHOICE.md); release process.
- **Dependencies:** Phase 7; license decision.
- **Completion criteria:** Clean install/uninstall on Windows 10 and 11 x64; no network required
  after installing language packages; SBOM and notices included.
- **Testing requirements:** Install tests on clean VMs; smoke tests of the packaged build.
