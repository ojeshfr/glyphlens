# GlyphLens

**A privacy-first, offline Windows screen translation application that detects and translates text
across desktop applications using OCR and accessibility APIs.**

> **Status: Phase 1 — screen capture.** GlyphLens can capture a display or an application window and
> show a live preview, to verify the capture engine. **It does not read (OCR / accessibility) or
> translate anything yet.** Frames stay in memory; nothing is saved or sent anywhere. See
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
| IPC envelope format and validation (.NET + Python) | ✅ Implemented (no transport) |
| WPF application: capture diagnostic with target selector, start/stop, live preview, status and errors | ✅ Implemented (Phase 1) |
| Python worker `--version` / `--health` diagnostics | ✅ Implemented |
| Screen capture of a display or window (Windows Graphics Capture) | ✅ Implemented (Phase 1) — see [docs/CAPTURE.md](docs/CAPTURE.md) |
| Region capture | ⏳ Planned — next capture milestone |
| Multi-monitor / mixed-DPI / Windows 10 verification | ⏳ Not yet verified on hardware — [checklist](docs/CAPTURE-MANUAL-TESTS.md) |
| UI Automation text extraction, OCR (PaddleOCR) | ⏳ Planned — Phase 2 |
| Text fusion, language detection | ⏳ Planned — Phase 3 |
| Offline translation (Argos Translate) | ⏳ Planned — Phase 4 |
| Floating overlay | ⏳ Planned — Phase 5 |
| Side panel and history | ⏳ Planned — Phase 6 |
| Named Pipes transport, SQLite cache, hotkeys | ⏳ Planned |
| Installer and releases | ⏳ Planned — Phase 8 |

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
| OCR (proposed) | PaddleOCR, in a local Python worker |
| Translation (proposed) | Argos Translate, in a local Python worker |
| App ↔ worker IPC (planned) | Versioned UTF-8 JSON over Windows Named Pipes |
| Storage (planned) | SQLite |
| Tests | xUnit (.NET), `unittest` (Python) |
| CI | GitHub Actions on `windows-latest` |

The OCR and translation engine choices are **proposals pending validation**; see
[ADR-0004](docs/decisions/ADR-0004-worker-communication.md).

## Repository structure

```
GlyphLens/
├── src/
│   ├── GlyphLens.App/             WPF app (MVVM), capture diagnostic UI, composition root → GlyphLens.exe
│   ├── GlyphLens.Core/            Platform-independent models, contracts, capture state machine and frame mailbox
│   ├── GlyphLens.Windows/         Windows adapters (Windows Graphics Capture, target enumeration; later UI Automation, hotkeys)
│   └── GlyphLens.Infrastructure/  IPC, persistence, cache, logging, configuration
├── worker/                        Python inference worker (future GlyphLens.Worker.exe)
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

The Python worker currently uses only the standard library; no `pip install` is needed.

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

No test requires internet access or AI models. Tests that need a real display, GPU and windows are skipped
unless you opt in (they never run silently in CI):

```powershell
$env:GLYPHLENS_RUN_CAPTURE_TESTS = "1"; dotnet test tests/GlyphLens.Windows.Tests -c Release
```

## Privacy philosophy

- **Local by default and by design.** OCR and translation run on your machine. There is no cloud
  translation and no silent cloud fallback.
- **Nothing is uploaded.** Screen content is never sent anywhere.
- **Minimal retention.** Captured frames live in memory only. Persisting translated text (history,
  cache) will be explicit and user-controllable.
- **Local-only IPC.** The app and worker communicate over a local Named Pipe restricted to the
  current user.

See [docs/SECURITY.md](docs/SECURITY.md).

## Milestones

| Phase | Goal |
| --- | --- |
| 0 | Repository and foundation ✅ |
| 1 | Windows screen capture ← **current** (display and window capture done; region capture pending) |
| 2 | UI Automation and OCR |
| 3 | Text fusion and language detection |
| 4 | Offline translation |
| 5 | Floating translation overlay |
| 6 | Translation side panel |
| 7 | Performance and compatibility |
| 8 | Packaging and release |

Details: [docs/ROADMAP.md](docs/ROADMAP.md).

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Screen capture](docs/CAPTURE.md) and its [verification record / manual checklist](docs/CAPTURE-MANUAL-TESTS.md)
- [IPC protocol](docs/IPC-PROTOCOL.md)
- [Development guide](docs/DEVELOPMENT.md)
- [Testing](docs/TESTING.md)
- [Security](docs/SECURITY.md)
- [Contributing](docs/CONTRIBUTING.md)
- [Architectural decision records](docs/decisions/)

## License

No distribution license has been selected yet; see [LICENSE-CHOICE.md](LICENSE-CHOICE.md).
Until a license is chosen, all rights are reserved by the authors.
