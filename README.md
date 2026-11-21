# GlyphLens

**A privacy-first, offline Windows screen translation application that detects and translates text
across desktop applications using OCR and accessibility APIs.**

> **Status: Phase 2b — reading text with Windows OCR (on top of Phase 1 capture and Phase 2a worker communication).**
> GlyphLens can capture a display or window with a live preview, read the text in it with the built-in Windows OCR
> (offline, shown as a list and as boxes over the preview), and start, health-check and stop a separate Python worker over
> a secure local Named Pipe. **It does not translate anything yet and does not use accessibility (UI Automation) text; the
> Python worker still reports OCR, language detection and translation as NOT_IMPLEMENTED (OCR currently runs in the app, not
> in the worker).** Frames and recognised text stay in memory; nothing is saved or sent anywhere. See
> [Current development status](#current-development-status).

---

## Product vision

GlyphLens is a universal screen translator for Windows. It aims to translate the text you see in
virtually any desktop application — WhatsApp Desktop, Instagram, Google Meet, Chrome, Brave,
Microsoft Edge, PDF readers and native Windows apps — **without application-specific integrations
and without sending your screen to the cloud.**

It combines two complementary text sources:

1. **Microsoft UI Automation** for text that applications expose through accessibility APIs.
2. **OCR** for visually rendered text that accessibility APIs cannot reach (images, video, canvases).

Detected text is translated **locally** and presented in one of two modes that share the same
pipeline:

- **Overlay mode** — transparent, click-through translation labels positioned over the original text.
- **Side panel mode** — a separate window with a live feed of original and translated text, history
  and copy.

Privacy and offline operation are core requirements, not optional extras.

## Current development status

| Area | Status |
| --- | --- |
| Repository, solution, CI, docs, ADRs | ✅ Implemented (Phase 0) |
| Domain models and service contracts (`GlyphLens.Core`) | ✅ Implemented (contracts only) |
| IPC envelope, framing and validation (.NET + Python) | ✅ Implemented |
| Named Pipes transport (ACL, session token), Python worker pipe client, `worker.initialize` / `health` / `shutdown` | ✅ Implemented (Phase 2a) — see [docs/IPC-PROTOCOL.md](docs/IPC-PROTOCOL.md) |
| Worker lifecycle manager (bounded restarts, Job Object) and WPF worker diagnostics panel | ✅ Implemented (Phase 2a) |
| Text recognition with the built-in Windows OCR (`Windows.Media.Ocr`): language selector, Read text, Live reading, results list, boxes over the preview, Copy | ✅ Implemented (Phase 2b) — offline, no downloads; languages = the OCR packs installed in Windows; no confidence score (Windows OCR reports none) |
| **Chinese OCR** in the Python worker (PaddleOCR PP-OCRv5 on ONNX Runtime, `ocr.detect`) | ✅ Implemented — local model files, no download; measured 1.2 % CER on 10 real window captures, 0.4-1.3 s per window — [evaluation](docs/research/CHINESE-FIRST-EVALUATION.md) |
| **Offline Chinese-to-English translation** (Argos zh/zt packages run with CTranslate2; script-based language identification; paragraph grouping; cache) | ✅ Implemented — gist-level quality with documented failure cases; real end-to-end tests passed (Simplified and Traditional) |
| **Floating translation overlay** (WPF window above other applications, interactive / pass-through, self-capture exclusion, shortcuts) | ✅ Implemented and verified on one monitor (multi-monitor placement unit-tested only) |
| Python engine compatibility (PaddleOCR, Argos Translate, CTranslate2) | 📄 Evaluated — [report](docs/research/PYTHON-ENGINE-COMPATIBILITY.md) |
| Pipe ACL verification | 📄 [report](docs/research/PIPE-ACL-VERIFICATION.md) |
| WPF application: capture diagnostic with target selector, start/stop, live preview, status and errors | ✅ Implemented (Phase 1) |
| Python worker `--version` / `--health` / `--pipe` | ✅ Implemented |
| Screen capture of a display or window (Windows Graphics Capture) | ✅ Implemented (Phase 1) — see [docs/CAPTURE.md](docs/CAPTURE.md) |
| Region capture | ⏳ Planned — next capture milestone |
| Multi-monitor / mixed-DPI / Windows 10 verification | ⏳ Not yet verified on hardware — [checklist](docs/CAPTURE-MANUAL-TESTS.md) |
| UI Automation text extraction | ⏳ Planned — Phase 2 |
| Text fusion (accessibility + OCR) | ⏳ Planned — Phase 3 |
| Other source languages (language identification library, BPE-tokenizer Argos packages) | ⏳ Planned — Chinese first |
| Translation history, side panel | ⏳ Planned — Phase 6 |
| **Install everything** (one-click setup on the Overview page: uv, a private Python 3.12, the hash-locked OCR and translation packages, the PP-OCRv5 files and the zh/zt models; only after the user confirms) | ✅ Implemented — real install verified on one PC (about 360 MB, 2.5 min); see [ADR-0008](docs/decisions/ADR-0008-explicit-one-click-setup.md) |
| Desktop shortcut on the first run (once; not recreated after the user deletes it) | ✅ Implemented — unit-tested with real `.lnk` files in a temporary folder; not seen on a real first run |
| Installer | ⏳ Planned — Phase 8 |
| Preview release (`v0.1.1-preview`: unsigned self-contained zip, no installer; **Install everything** sets up Python, the engines and the Chinese models on the first run) | 📦 See the repository's Releases page |

## Planned capabilities

- Translate on-screen text from any Windows application into English (first target language).
- Accessibility-first extraction with OCR fallback, merged into a single set of text regions.
- Automatic source-language detection.
- Fully offline OCR and translation with downloadable, versioned language packages.
- Multi-monitor and per-monitor DPI awareness.
- Overlay and side panel presentation modes.
- Local translation cache and optional history.

## Technology stack

| Concern | Technology |
| --- | --- |
| Desktop app | C#, .NET 10 (LTS), WPF, MVVM — Windows 10/11 x64 |
| Screen capture | Windows Graphics Capture, Direct3D 11 (Vortice.Direct3D11, MIT) |
| Accessible text (planned) | Microsoft UI Automation |
| OCR | Windows built-in OCR (`Windows.Media.Ocr`), offline, no extra packages (implemented) |
| OCR, second engine (proposed) | PaddleOCR, in a local Python worker |
| Translation (proposed) | Argos Translate, in a local Python worker |
| App ↔ worker IPC | Versioned UTF-8 JSON over Windows Named Pipes, length-prefixed frames (implemented) |
| Storage (planned) | SQLite |
| Tests | xUnit (.NET), `unittest` (Python) |
| CI | GitHub Actions on `windows-latest` |

The PaddleOCR and Argos Translate engine choices are **proposals pending validation**; see
[ADR-0004](docs/decisions/ADR-0004-worker-communication.md).

## Repository structure

```
GlyphLens/
├── src/
│   ├── GlyphLens.App/             WPF app (MVVM): capture, text recognition and worker diagnostic UI, composition root → GlyphLens.exe
│   ├── GlyphLens.Core/            Platform-independent models, contracts, capture state machine and frame mailbox
│   ├── GlyphLens.Windows/         Windows adapters (Windows Graphics Capture, target enumeration, Windows OCR, worker pipe/launcher; later UI Automation, hotkeys)
│   └── GlyphLens.Infrastructure/  IPC, persistence, cache, logging, configuration
├── worker/                        Python worker: pipe client + health/shutdown (engines not yet; future GlyphLens.Worker.exe)
│   ├── glyphlens_worker/
│   └── tests/
├── tests/                         xUnit test projects (Core, Infrastructure, Windows)
├── docs/                          Architecture, protocol, roadmap, security, ADRs
└── .github/                       CI workflow, Dependabot, templates
```

## Setup

Prerequisites (see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for details):

- Windows 10 (version 2004 / build 19041 or later) or Windows 11, x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (10.0.100 or later feature band)
- Python 3.11 or later (3.11 is the CI baseline)
- Git

```powershell
git clone <your-clone-url> GlyphLens
cd GlyphLens
dotnet restore GlyphLens.sln
```

Text recognition uses the built-in Windows OCR, which needs at least one OCR-capable language installed in Windows
(Settings → Time & language; English is typical). GlyphLens never installs Windows languages; without one, the Text recognition panel
says so. (The Chinese OCR does not need a Windows language: it comes with Install everything.)

**For users of a release:** start `GlyphLens.exe`, open the **Overview** page and click **Install everything** when it is offered. The
consent panel lists what will be downloaded (uv, Python 3.12, 70 hash-locked packages, the PP-OCRv5 files, the Chinese models; about
360 MB), from which hosts, and where it goes (`%LOCALAPPDATA%\GlyphLens\runtime` and `...\models`, or `GLYPHLENS_RUNTIME_DIR` /
`GLYPHLENS_MODELS_DIR`). Nothing is downloaded before you confirm, and afterwards everything runs offline. The manual steps (your own
Python environment and `pip`, model files you download yourself) still work; see [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

**For development:** the worker itself uses only the standard library. The app finds Python in this order: `GLYPHLENS_PYTHON`, the
environment made by Install everything, `worker\.venv`, `PATH`, the `py` launcher; start it from the app's "Worker diagnostics" panel.
Engine dependency research: [docs/research/PYTHON-ENGINE-COMPATIBILITY.md](docs/research/PYTHON-ENGINE-COMPATIBILITY.md).

On its first run `GlyphLens.exe` puts a `GlyphLens` shortcut on your desktop, once (deleting it is respected; set
`GLYPHLENS_NO_DESKTOP_SHORTCUT=1` to turn this off).

## Build

```powershell
dotnet build GlyphLens.sln -c Release
```

Run the desktop shell:

```powershell
dotnet run --project src/GlyphLens.App -c Release
```

## Test

```powershell
dotnet test GlyphLens.sln -c Release
```

```powershell
cd worker; python -m unittest discover -s tests -t . -v
```

Formatting check (also run in CI):

```powershell
dotnet format GlyphLens.sln --verify-no-changes
```

Worker tests across the real process boundary (.NET manager, real pipe, real Python worker; no models) run
whenever Python 3.11+ is found, and are skipped with a visible reason otherwise:

```powershell
dotnet test tests/GlyphLens.Windows.Tests -c Release --filter "FullyQualifiedName~Worker"
```

No default test requires internet access or AI models. The real one-click install and the full Chinese chain (real worker, real OCR,
real translation) are opt-in: `$env:GLYPHLENS_RUN_INSTALL_TESTS = "1"` (see [docs/TESTING.md](docs/TESTING.md)). Tests that need a real display, GPU and windows are skipped
unless you opt in (they never run silently in CI):

```powershell
$env:GLYPHLENS_RUN_CAPTURE_TESTS = "1"; dotnet test tests/GlyphLens.Windows.Tests -c Release
```

## Privacy philosophy

- **Local by default and by design.** OCR and translation run on your machine. There is no cloud
  translation and no silent cloud fallback.
- **Downloads only when you ask.** Install everything downloads the engines and models once, after you confirm, each file checked
  against a pinned SHA-256; the update check is the only other network use (see [docs/SECURITY.md](docs/SECURITY.md)).
- **Nothing is uploaded.** Screen content is never sent anywhere.
- **Minimal retention.** Captured frames and recognised text live in memory only. Persisting translated text (history,
  cache) will be explicit and user-controllable.
- **Local-only IPC.** The app and worker communicate over a local Named Pipe restricted to the
  current user.

See [docs/SECURITY.md](docs/SECURITY.md).

## Milestones

| Phase | Goal |
| --- | --- |
| 0 | Repository and foundation ✅ |
| 1 | Windows screen capture ✅ (display and window; region capture pending) |
| 2 | UI Automation and OCR: worker communication, Windows OCR and the Chinese-capable PaddleOCR engine done; UI Automation pending |
| 3 | Text fusion and language detection (Chinese, Japanese, Korean and English by script rules done) |
| 4 | Offline translation ✅ (Chinese to English first; other languages pending) |
| 5 | Floating translation overlay ✅ (first version) |
| 6 | Translation side panel |
| 7 | Performance and compatibility |
| 8 | Packaging and release |

Details: [docs/ROADMAP.md](docs/ROADMAP.md).

## Documentation

- [Product and UI design specification](DESIGN.md) (the design target) and [implementation status](docs/IMPLEMENTATION-STATUS.md) (what exists and how it was verified)
- [Architecture](docs/ARCHITECTURE.md)
- [Screen capture](docs/CAPTURE.md) and its [verification record / manual checklist](docs/CAPTURE-MANUAL-TESTS.md)
- [IPC protocol](docs/IPC-PROTOCOL.md) · [Python engine compatibility](docs/research/PYTHON-ENGINE-COMPATIBILITY.md) · [Pipe ACL verification](docs/research/PIPE-ACL-VERIFICATION.md)
- [Development guide](docs/DEVELOPMENT.md)
- [Testing](docs/TESTING.md)
- [Security](docs/SECURITY.md)
- [Contributing](docs/CONTRIBUTING.md)
- [Architectural decision records](docs/decisions/)

## License

GlyphLens is licensed under the [Apache License 2.0](LICENSE) (copyright notice in [NOTICE](NOTICE), third-party notices in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), reasoning in [LICENSE-CHOICE.md](LICENSE-CHOICE.md)). Model files are not part of the
project and keep their own licences.
