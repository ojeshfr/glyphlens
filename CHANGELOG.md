# Changelog

All notable changes to GlyphLens are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
intends to use [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added (Phase 2a: worker communication)

- Named Pipe IPC between the app and the Python worker (ACL, session token, 16 MiB framing), `IWorkerProcessManager` implementation with
  bounded restarts and a Job Object, Python pipe client, and a WPF worker diagnostics panel. OCR, language detection and translation are
  still not implemented. See `docs/IPC-PROTOCOL.md`.

### Added (Phase 1: screen capture)

- Windows Graphics Capture of a display or an application window, with a live preview in the WPF app
  (target selector, Start/Stop, status indicator, frame size and number, error display). Design and findings:
  `docs/CAPTURE.md`; ADR-0005.
- Core: `CaptureTarget`, `CaptureStatus` + `CaptureStateMachine`, `CaptureError`/`CaptureException`, `FrameMetadata`,
  reference-counted pooled `CapturedFrame`, `DpiScale`, `ICaptureTargetProvider`, `IFrameSource` and the latest-wins
  `LatestFrameMailbox`.
- Windows: `WindowsGraphicsCaptureService`, `CaptureSessionManager`, `Direct3DDeviceManager`, `FrameProcessor`,
  `CaptureTargetProvider`, error mapping, target-liveness watchdog.
- New dependency: `Vortice.Direct3D11` 3.8.3 (MIT).
- Tests: Core capture tests, Windows unit tests, `GlyphLens.App.Tests` (view model), and opt-in hardware tests
  (`GLYPHLENS_RUN_CAPTURE_TESTS=1`). Verification record: `docs/CAPTURE-MANUAL-TESTS.md`.

### Changed

- **Breaking contract change:** `IScreenCaptureService` is now a session service (`StartAsync`/`StopAsync`/`Frames`)
  instead of one-shot `CaptureRegionAsync`; `CapturedFrame` is now pooled and reference counted. Nothing implemented the old contracts.
- Windows-specific projects now target `net10.0-windows10.0.19041.0` (minimum OS Windows 10 2004).
- The main window is now the capture diagnostic (dark theme via the experimental WPF `ThemeMode`).

### Fixed

- `dotnet format --verify-no-changes` (run by CI) failed on the Phase 0 template `AssemblyInfo.cs` and on
  naming rules for `private static readonly` test fields; both corrected.

### Added (Phase 0)

- Phase 0 repository foundation:
  - .NET 10 solution with `GlyphLens.Core`, `GlyphLens.Windows`, `GlyphLens.Infrastructure` and
    the `GlyphLens.App` WPF shell (`GlyphLens.exe`).
  - Domain models with validation: `ScreenTextRegion`, `ScreenRectangle`, `CapturedFrame`,
    `TranslationRequest`, `TranslationResult`, `DetectionSource`, `WorkerStatus`, `LanguageCode`.
  - Service contracts (interfaces only): `IScreenCaptureService`, `IAccessibilityTextSource`,
    `IOcrEngine`, `ILanguageDetector`, `ITranslationEngine`, `ITranslationCache`,
    `IWorkerProcessManager`.
  - IPC protocol version 1.0 envelope definition, JSON codec and validation (.NET and Python).
    No transport yet.
  - Python worker package with `--version` and `--health` diagnostics.
  - xUnit and `unittest` test suites, including architecture dependency tests.
  - GitHub Actions CI on Windows, Dependabot for NuGet, pip and GitHub Actions.
  - Architecture, protocol, roadmap, security, development, testing and contributing docs; ADRs 0001–0004.
