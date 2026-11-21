# Development Guide

## Required SDKs and tools

| Tool | Version | Notes |
| --- | --- | --- |
| Windows | 10 (2004 / build 19041+) or 11, x64 | WPF and Windows Graphics Capture require Windows. Capture tests need a GPU/desktop session. |
| .NET SDK | 10.0.100+ (any 10.0 feature band) | Pinned by `global.json` with `rollForward: latestFeature`. |
| Python | 3.11+ | 3.11 is the CI baseline. Tested locally on 3.12 as well. |
| Git | any recent | |
| IDE (optional) | Visual Studio 2026 / Rider / VS Code with C# Dev Kit | |

No global tools, NuGet feeds or Python packages beyond the defaults are needed in Phase 0.

## Installation prerequisites

```powershell
winget install Microsoft.DotNet.SDK.10
winget install Python.Python.3.11
winget install Git.Git
```

Verify:

```powershell
dotnet --list-sdks
python --version
```

## Building

```powershell
dotnet restore GlyphLens.sln
dotnet build GlyphLens.sln -c Release
```

Warnings are errors (`Directory.Build.props`). Fix the cause instead of suppressing a warning; if a
suppression is truly necessary, scope it narrowly and explain why in a comment.

## Running

```powershell
dotnet run --project src/GlyphLens.App
```

The executable is `src/GlyphLens.App/bin/<Configuration>/net10.0-windows10.0.19041.0/GlyphLens.exe`.
The window is the Phase 1 capture diagnostic: choose Display or Window, pick a target, Start capture, and the
live preview, status, frame size and frame number appear. Stop capture (or close the app) releases everything.

## Testing

```powershell
dotnet test GlyphLens.sln -c Release
```

```powershell
cd worker; python -m unittest discover -s tests -t . -v
```

See [TESTING.md](TESTING.md).

## Formatting

```powershell
dotnet format GlyphLens.sln --verify-no-changes
```

Run `dotnet format GlyphLens.sln` to apply fixes. Style rules live in `.editorconfig`.

## Debugging

- **App:** open `GlyphLens.sln`, set `GlyphLens.App` as the startup project, F5.
- **Tests:** use the IDE test explorer, or `dotnet test --filter "FullyQualifiedName~ProtocolVersion"`.
- **Capture:** run the hardware tests (`GLYPHLENS_RUN_CAPTURE_TESTS=1`, see [TESTING.md](TESTING.md)). Capture is
  native/GPU code: when something misbehaves, check `CaptureError.Detail` (shown as a tooltip on the error box) for the
  exception type and HRESULT. Process handle growth can be inspected by counting `Section` handles (see
  `tests/GlyphLens.Windows.Tests/Integration/ProcessHandleCounter.cs`).
- **Source files:** keep text files UTF-8 without BOM. Avoid PowerShell 5.1 `Get-Content`/`Set-Content` round trips on files
  containing non-ASCII characters (it re-encodes them); use an editor or `[IO.File]::WriteAllText` with `UTF8Encoding($false)`.
- **Worker:** `python -m glyphlens_worker --health` from `worker/`; debug with your IDE's Python
  debugger using module `glyphlens_worker`.
- **Future (Phase 2+):** the app will log worker lifecycle and protocol errors locally; payload
  contents are never logged.

## Worker setup

The worker uses only the standard library, so no virtual environment is required. The application starts it itself from
the diagnostics panel (**Start worker**); it is never started automatically.

- **Interpreter:** Python 3.11 or newer. Discovery order: `GLYPHLENS_PYTHON` (a configured path is never silently replaced), then `python.exe`
  on `PATH` (Microsoft Store `WindowsApps` stubs are skipped), then the `py` launcher. Each candidate is run once to confirm its version.
- **Worker package:** found via `GLYPHLENS_WORKER_DIR` (the directory containing `glyphlens_worker`) or by walking up from the application directory
  (works from a repo build output).
- **Run it by hand:** `python -m glyphlens_worker --version`, `--health` (a standalone, unconnected report). `--pipe` is for the application only: it
  requires a generated pipe name and `GLYPHLENS_WORKER_TOKEN`.
- **Worker tests:** `cd worker; python -m unittest discover -s tests -t .`
- **Worker integration tests (.NET -> real Python):** `dotnet test tests/GlyphLens.Windows.Tests --filter "FullyQualifiedName~Worker"`. They run
  whenever Python 3.11+ and the worker package are found (CI installs Python) and are skipped with a visible reason otherwise. They need no models,
  GPU or network. Manager logic against fake workers is in `tests/GlyphLens.Infrastructure.Tests/IPC`; the view model in `tests/GlyphLens.App.Tests`.
  Current pass/fail status is not recorded here; run them.
- **Debugging:** worker request logs (id, type, outcome, ms) go to the .NET trace output; the worker's stderr tail is included in start-failure messages.

The engines are optional extras. For translation and Chinese OCR use a project-local virtual environment next to the worker: the app
prefers `worker\.venv` over any other Python it finds.

```powershell
py -3.12 -m venv worker\.venv
worker\.venv\Scripts\python.exe -m pip install -e "worker[translation,ocr]"
# optional: other source languages (Lingua needs Python 3.12+, about 290 MB): add ",lingua"; on Python 3.11 use ",langid"
```

`.venv/` is git-ignored. Models live in `%LOCALAPPDATA%\GlyphLens\models` (override: `GLYPHLENS_MODELS_DIR`, an absolute local path) and
must never be committed: translation packages are imported from the Models panel (a `.argosmodel` file you downloaded), and the OCR
files go into `<models>\ocr\PP-OCRv5_mobile_det_onnx` and `...\PP-OCRv5_mobile_rec_onnx` (the official ONNX exports). The worker never
downloads anything. See [eval/zh-en/README.md](../eval/zh-en/README.md) for the evaluation and end-to-end scripts.

The overlay shortcuts are Ctrl+Alt+T/O/P/I/C. `GLYPHLENS_DISABLE_CAPTURE_EXCLUSION=1` disables the overlay's capture exclusion for the
verification control run only.

## Development conventions

- Follow the dependency rules in [ARCHITECTURE.md](ARCHITECTURE.md); the architecture tests will fail otherwise.
- MVVM: views bind to view models; no business logic in code-behind.
- Contracts first: new capabilities start as Core interfaces/models with tests.
- No fake implementations that return invented OCR text or translations.
- No network access in the capture/OCR/translation path.
- Never commit screenshots, captured frames, model weights, logs with personal data or secrets.
- Record significant decisions as ADRs in `docs/decisions/`.
- See [CONTRIBUTING.md](CONTRIBUTING.md) for branches, commits and PRs.
