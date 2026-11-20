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
| Worker tests | `unittest` | `worker/tests` | Package import, `--version`, `--health`, envelope validation and decoding. |

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
