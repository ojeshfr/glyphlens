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

- **Interpreter:** Python 3.11 or newer. Discovery order: `GLYPHLENS_PYTHON` (a configured path is never silently replaced), then the one-click
  setup's `<runtime>\venv`, then `worker\.venv`, then `python.exe` on `PATH` (Microsoft Store `WindowsApps` stubs are skipped), then the `py` launcher. Each candidate is run once to confirm its version.
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

## One-click setup ("Install everything")

The Overview page's **Install everything** installs uv, a uv-managed CPython 3.12, the engine packages, the PP-OCRv5 files and the
Argos zh/zt models (design: [ADR-0008](decisions/ADR-0008-explicit-one-click-setup.md); claims: [SECURITY.md](SECURITY.md)).

- **Folders.** Runtime: `%LOCALAPPDATA%\GlyphLens\runtime` or `GLYPHLENS_RUNTIME_DIR` (absolute local path, otherwise refused and
  reported): `tools\uv.exe`, `python\`, `venv\`, `uv-cache\`, `downloads\`, `engines.lock.txt`, `setup.log`. Models: the usual models
  folder (`GLYPHLENS_MODELS_DIR`). For experiments point both variables at a scratch folder; never test against your real profile.
- **Interpreter order** (`WorkerLocator`): configured path, `GLYPHLENS_PYTHON`, `<runtime>\venv`, `worker\.venv`, `PATH`, `py`.
  After the setup the app forgets the interpreter it found and restarts the worker (`WorkerEnvironmentSwitch`).
- **Code.** Pinned data: `src/GlyphLens.Core/Setup/SetupManifest.cs` (one file: URLs, sizes, SHA-256, uv version, Python build).
  Logic: `GlyphLens.Infrastructure/Setup` (`EnvironmentInstaller`, `HttpFileDownloader`, `UvArchive`, `EngineLock`, `SetupLogFile`);
  processes: `GlyphLens.Windows/Setup/JobProcessRunner` (job object, kill the tree); UI: `SetupViewModel` + `Views/SetupPanel.xaml`.
- **Changing a pin.** Download the new file into an empty folder, hash it yourself (`Get-FileHash -Algorithm SHA256`), compare it with
  the publisher's value where one exists (uv's `.sha256` asset, Hugging Face's `X-Linked-ETag`), update `SetupManifest.cs` and the
  manifest tests, then run the opt-in install test (docs/TESTING.md). For uv choose a release at least 14 days old and also pin
  `UvExecutableSize`/`UvExecutableSha256` (the `uv.exe` inside the zip). Measure `ApproximatePackageDownloadBytes` again when the lock
  changes.

### Engine lock file

`worker/requirements/engines.lock.txt` is generated, never edited by hand. Inputs: `engines.in` (the four direct pins) and
`engines-constraints.txt` (`pip freeze` of the tested `worker\.venv`, so every transitive version is the tested one). With the pinned
uv (any `uv.exe` of the version in `SetupManifest.UvVersion`), from `worker/requirements`:

```powershell
$env:UV_NO_CONFIG = "1"
uv pip compile engines.in -c engines-constraints.txt --generate-hashes --no-build --python-version 3.12 --python-platform x86_64-pc-windows-msvc -o engines.lock.txt
```

The header of the file repeats the command. Refresh `engines-constraints.txt` first when the tested environment changes
(`worker\.venv\Scripts\python.exe -m pip freeze`, keeping the comment line). The file is embedded in `GlyphLens.Infrastructure` and the
`EngineLockTests` check that every line is `==`-pinned with SHA-256 hashes, the direct pins, the absence of Lingua/py3langid/the worker
package, and that every version equals the constraints file. Keep it LF (`.gitattributes`): its SHA-256 marks an installed environment.

## Desktop shortcut and icon

- On the first run of `GlyphLens.exe` (not when started through `dotnet GlyphLens.dll`) `DesktopShortcutService` creates
  `GlyphLens.lnk` on the desktop, once (flag `desktopShortcutHandled` in `preferences.json`). Set `GLYPHLENS_NO_DESKTOP_SHORTCUT=1` when
  you run development builds and do not want the shortcut. Tests use a temporary folder only.
- The icon `src/GlyphLens.App/Assets/GlyphLens.ico` (16-256 px) is built by `tools/make-icon.py`:
  `worker\.venv\Scripts\python.exe -I tools\make-icon.py [logo.png]` (a square PNG of at least 1024x1024; without an argument the
  placeholder mark is drawn).

## Development conventions

- Follow the dependency rules in [ARCHITECTURE.md](ARCHITECTURE.md); the architecture tests will fail otherwise.
- MVVM: views bind to view models; no business logic in code-behind.
- Contracts first: new capabilities start as Core interfaces/models with tests.
- No fake implementations that return invented OCR text or translations.
- No network access in the capture/OCR/translation path.
- Never commit screenshots, captured frames, model weights, logs with personal data or secrets.
- Record significant decisions as ADRs in `docs/decisions/`.
- See [CONTRIBUTING.md](CONTRIBUTING.md) for branches, commits and PRs.
