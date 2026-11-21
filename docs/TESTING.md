# Testing

## Principles

- Tests cover code that exists. No placeholder assertions, no tests for features that are not built.
- Deterministic: fixed inputs, no wall-clock or randomness dependence, no network, no AI models.
- Fast: the full suite runs in seconds on CI.

## Suites

| Suite | Framework | Location | Covers |
| --- | --- | --- | --- |
| `GlyphLens.Core.Tests` | xUnit | `tests/GlyphLens.Core.Tests` | Model construction and validation, language codes, capture models, `ProtocolVersion` parsing and compatibility, Core dependency rules. |
| `GlyphLens.Infrastructure.Tests` | xUnit | `tests/GlyphLens.Infrastructure.Tests` | IPC envelope serialization and validation, contract (model) JSON round-trips, Infrastructure dependency rules. |
| `GlyphLens.Windows.Tests` | xUnit | `tests/GlyphLens.Windows.Tests` | Windows dependency rules, target-id parsing, row compaction, error mapping, service guard rails; Windows OCR engine tests (see "OCR tests"); worker pipe/launcher tests; plus **opt-in hardware tests** of real capture (see below). |
| `GlyphLens.App.Tests` | xUnit | `tests/GlyphLens.App.Tests` | `CaptureViewModel`, `WorkerViewModel`, `OcrViewModel`, `OverlayState` and `AsyncRelayCommand` against fake services (no window is created). |
| Worker tests | `unittest` | `worker/tests` | Envelope, framing, dispatcher, server; language identification (script rules, shaping, real backends when installed); translation service, registry and routes, validating importer (zip-slip, bombs, BPE refusal), segmentation incl. Chinese sentences, hallucination guard, contraction clean-up; `ocr.detect` with a fake recogniser; network guard; a real named-pipe session. 395 tests. Last run 2026-11-21: `worker\.venv` (Python 3.12.10, engines, real fr model and real OCR files) OK with 6 skipped; the CI command on a real Python 3.11.18 and on the system Python 3.12.10 (standard library only) OK with 26 skipped (the real-engine tests skip with a visible reason). |

## Running

```powershell
dotnet test GlyphLens.sln -c Release
```

```powershell
dotnet test tests/GlyphLens.Core.Tests --filter "FullyQualifiedName~ProtocolVersion"
```

```powershell
cd worker; python -m unittest discover -s tests -t . -v
```

## Architecture tests

`tests/*/Architecture/*DependencyTests.cs` inspect compiled assembly references to enforce:

- Core targets no OS platform and references only the BCL.
- Infrastructure references Core and nothing UI- or Windows-adapter-related.
- Windows targets Windows and references neither WPF nor upper layers.

If one fails, fix the dependency, do not weaken the test.

## Protocol parity

The .NET (`WorkerMessageSerializerTests`) and Python (`test_envelope.py`) suites assert the same
envelope rules and the same list of request types. Any protocol change must update both.

## Hardware tests (opt-in)

`tests/GlyphLens.Windows.Tests/Integration` drives the real Windows Graphics Capture stack: display and window
capture, repeated start/stop with a kernel-handle leak guard, closing the captured window, and disposal during
capture. They need an interactive desktop and a GPU, so they are **skipped unless** `GLYPHLENS_RUN_CAPTURE_TESTS=1`
(the skip reason is shown in the test output), and CI never runs them:

```powershell
$env:GLYPHLENS_RUN_CAPTURE_TESTS = "1"; dotnet test tests/GlyphLens.Windows.Tests -c Release
```

What has actually been run, and what still needs a human with other hardware, is recorded in
[CAPTURE-MANUAL-TESTS.md](CAPTURE-MANUAL-TESTS.md).

## OCR tests

- **Windows OCR engine** (`tests/GlyphLens.Windows.Tests/Ocr`): known text is rendered into frames with
  `System.Drawing.Common` (a **test-only** dependency; Microsoft, MIT) and recognised by the real Windows OCR. The tests check the
  recognised text, that the bounds are frame-relative and overlap where the text was drawn, line order, honest metadata
  (`Confidence` is `null`), a blank frame, a frame with row padding, an explicitly selected language, concurrent calls, and a
  language that is not installed. Tests that need an installed OCR language use `[WindowsOcrFact]`: when Windows has none (a
  stripped install or a CI image) they are **skipped with a visible reason**, not silently passed. Tests that need no language
  always run: the 10 000 px frame limit, cancellation, null frame, language-list validity, and the mapping helpers
  (`UnionBounds`, `ToTightPixels`). Whether the language-dependent tests run on the CI image is not recorded here.
- **Core/Infrastructure:** nullable `Confidence`, `OcrException`, and the JSON round trip of a region without confidence.
- **OCR view model** (`tests/GlyphLens.App.Tests`): against a fake engine and the fake capture service: read once, results and
  overlay, live loop (reads the frame already on screen, never queues work during a burst of frames, stops on capture stop or an
  engine error), results cleared when capture stops, errors that never leak text, language selection, and Copy only on command.
- Not covered by automated tests: non-English languages, rotated text, small or low-contrast text, and real application
  content. See [CAPTURE-MANUAL-TESTS.md](CAPTURE-MANUAL-TESTS.md) section 3b.

## One-click setup, desktop shortcut and the full Chinese chain

Counts of 2026-11-21 after this feature: **Core 514, Infrastructure 576, App 385 (+1 skipped), Windows 165 (+10 skipped: 7 hardware, 3
install)**; Python worker 395.

- **Default (no network, no models):** `SetupManifestTests` (https only, 64-hex hashes, sizes > 0, pinned Hugging Face commit, hosts,
  OCR destinations), `RuntimeDirectoryTests` (override rules), `HttpFileDownloaderTests` (fake HTTP: HTTPS only, https-to-http redirect
  refused, relative redirects, redirect limit, size enforced while streaming on an endless body, Content-Length mismatch, early end,
  wrong hash, cancellation and stall delete the `.part`, skip when already verified, User-Agent only), `UvArchiveTests` (zip-slip,
  folders, unexpected or duplicate entries, wrong size/hash), `EngineLockTests` (embedded = repository file; every line `==` with
  SHA-256; direct pins; versions equal the tested environment; no Lingua, py3langid or worker package), `EnvironmentInstallerTests`
  (fakes for HTTP, processes, worker and catalogue but real files in a temporary folder: order, uv arguments and environment, nothing
  written outside the two folders, second run skips everything, resume after failure, cancellation, disk space, wrong pin, final check),
  `JobProcessRunnerTests` (real `cmd.exe`/`ping.exe`: output, exit code, environment, closed stdin, timeout, the whole tree killed on
  cancel), `ShellLinkShortcutFilesTests` (a real `.lnk` written and read back in a temporary folder), `WorkerLocatorOrderTests`
  (managed venv, then `worker\.venv`, then `PATH`; a real venv made with `python -m venv`), `SetupViewModelTests`,
  readiness offer tests, `WorkerEnvironmentSwitchTests`, `DesktopShortcutServiceTests` (temporary desktop and preferences; never the
  real ones).
- **Opt-in, needs the internet:** `$env:GLYPHLENS_RUN_INSTALL_TESTS = "1"`, optional `$env:GLYPHLENS_INSTALL_TEST_ROOT` (default
  `%TEMP%\glyphlens-install-test`; it gets `runtime` and `models`). Never run by CI; skipped with a visible reason otherwise.
  - `tests/GlyphLens.Windows.Tests/Setup/RealInstallIntegrationTests.cs`: the real installer end to end, then a second run that must skip
    everything and download nothing; a real cancellation during the uv download and during the package install (no `.part` file, no
    process from the runtime folder left); a real download with a deliberately wrong pin (refused with the "server changed" message).
  - `tests/GlyphLens.App.Tests/RealChineseChainTests.cs`: no usable Python (worker `Unavailable`), Install everything with the app's
    `WorkerEnvironmentSwitch`, the worker found by `WorkerLocator` in the managed venv, a real OCR request on rendered Chinese text and
    real zh/zt-to-English translations through the C# clients, and a worker running on another Python restarted onto the managed one.
    It changes process environment variables (in a non-parallel collection) and restores them.
  - Run them one project at a time, for example
    `dotnet test tests/GlyphLens.Windows.Tests -c Release --filter "FullyQualifiedName~RealInstallIntegrationTests"`.
- The Python real-engine tests can use the installed environment: `<runtime>\venv\Scripts\python.exe -m unittest discover -s tests -t .`
  from `worker/` with `GLYPHLENS_TEST_OCR_MODELS_DIR=<models>`, `GLYPHLENS_TEST_OCR_IMAGE=<a PNG with Chinese text>` and
  `GLYPHLENS_TEST_FR_EN_MODEL`.

## Translation, OCR and overlay tests

- **.NET** (Core 479, Infrastructure 497, App 345, Windows 155 of which 7 hardware tests are skipped by design; counts of 2026-11-21): contracts, `TranslationPipeline` (detection, manual language, uncertainty,
  missing model, cache, cancellation, one-at-a-time, grouping), region grouping geometry, `LruTranslationCache`, the worker clients
  (strict response validation, chunking, error mapping), `WorkerOcrEngine`, `OcrEngineSelector`, `TranslationViewModel`,
  `OverlayViewModel` (cards update in place, bounded, copy, modes, pause), `OverlayPlacement` (disconnected monitor, negative
  coordinates, minimum/maximum size), the settings store, the hotkey service (conflicts) and real-worker integration
  (`RealWorkerProtocolTests`, `WorkerLifecycleIntegrationTests`).
- **Opt-in real-engine tests (Python)**: `GLYPHLENS_TEST_FR_EN_MODEL` (a real `.argosmodel`), `GLYPHLENS_TEST_OCR_MODELS_DIR` +
  `GLYPHLENS_TEST_OCR_IMAGE` (real PP-OCRv5 files and a PNG). Without them these tests are **skipped with a visible reason**; CI runs the
  model-independent tests only. No model is ever committed.
- **Real-application scripts** (`eval/zh-en`, not run by CI because they need the interactive desktop, models and the mouse): the Chinese
  end-to-end tests (Simplified 13/13, Traditional 13/13, multi-line messages 14/14, automatic OCR choice 12/12 in two scenarios, incremental OCR and resource measurements; all re-run on 2026-11-21 on the final build) and the overlay verification (35/35, re-run the same day, plus a control run that proves the
  self-capture check can fail). See [research/CHINESE-FIRST-EVALUATION.md](research/CHINESE-FIRST-EVALUATION.md). They were run once
  successfully on one machine (Windows 11 build 26200, one 2560x1600 display at 150 % scale); the first versions of the scripts
  failed in several places that were script bugs, and mouse steps can be disturbed by a person using the mouse at the same time.
  `drive-keyboard-e2e.ps1` (2026-11-21) checks accessible names, keyboard-only use of every page, all five global shortcuts pressed while another
  application is in front, the overlay's "new results" button and scroll-following, and the minimum window sizes. Its keyboard, shortcut and
  "new results" checks passed on the build before that day's layout fixes; at the minimum size it found real clipping on the Capture page and in the
  overlay footer, which was fixed in XAML, and **the rerun on the fixed build has not been done yet** (see IMPLEMENTATION-STATUS.md, checkpoint 6).
  **Not tested**: more than one monitor, mixed DPI, other DPI scales, real screen readers, Windows 10, other builds for the display-affinity call.

## Future test categories (planned)

- **Interactive UI Automation tests** (later): driving the app UI automatically. Phase 1 verified this manually
  with an ad-hoc script; it is not part of the repository.
- **Worker engine tests** (Phase 2+): run against small, license-clean, generated fixtures; full
  model tests opt-in only.
- **Offline verification** (Phase 4): translation with networking disabled.
- **Performance benchmarks** (Phase 7): not part of the default test run.

## CI

`.github/workflows/ci.yml` runs on `windows-latest`: restore → format check → Release build →
.NET tests (hardware tests skipped) → Python 3.11 worker tests. Any failing step fails the workflow.
