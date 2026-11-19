# Security and Privacy

GlyphLens reads whatever is on the user's screen: private messages, documents, meetings. Its
security model exists to make sure that content never leaves the machine and is kept no longer
than necessary.

## Local-first processing

- Screen capture, UI Automation, OCR, language detection and translation all run locally.
- The inference worker is a local child process of the app, never a remote service.

## No cloud translation

- GlyphLens does not call any cloud OCR or translation API.
- There is **no silent cloud fallback**. If a local model is missing, the feature reports
  "unavailable" instead of reaching out to the network.
- Adding any network-dependent processing would require an ADR and explicit, opt-in user consent.

## No automatic screen uploads

- Screen pixels and recognised text are never uploaded, including in telemetry or crash reports.
- GlyphLens currently has no telemetry. Any future diagnostics must be opt-in and content-free.

## Screen capture (Phase 1)

- Capture starts only when the user presses Start capture, for the one display or window they selected.
- The capture status (idle / starting / capturing / stopping / error) is always visible in the main window, and
  Windows additionally shows its own capture indicator border by default (not suppressed).
- Capture stops when the user presses Stop, when the target goes away, and when the application exits.
- No administrator privileges are requested. Windows of elevated apps are not captured (the user is told why).
- Frames are never written to disk, logged, uploaded or included in error messages. The preview shows the live
  frame in the app's own window only.
- Known privacy consideration: while capturing a display, the GlyphLens window itself is visible in the capture.

## No unnecessary screen-data persistence

- Captured frames are held in memory only and discarded after processing.
- Logs must not contain recognised text, translations or pixels.
- Translation cache and history (future, SQLite) are stored locally in the user profile, can be
  cleared by the user, and history is opt-in.
- Bug reports must not include private screenshots (see the issue template).

## Secure local worker communication

See [IPC-PROTOCOL.md §1 and §12](IPC-PROTOCOL.md):

- Windows Named Pipe with a random per-session name, current-user-only ACL, remote clients rejected,
  single client connection.
- Strict envelope validation, a 16 MiB message limit and an allow-list of message types.
- No protocol message executes code, opens network connections or reads arbitrary paths.
- The worker exits when the app exits (Job Object, pipe break).

## Model provenance

- Models are installed only by explicit user action, from documented sources.
- Each installed model records its source, version, license and a SHA-256 checksum, verified
  before loading.
- Model files are never committed to the repository (`.gitignore` blocks common weight formats).
- Pickle-based or code-executing model formats are avoided where alternatives exist.

## Dependency auditing

- Dependabot monitors NuGet, pip and GitHub Actions dependencies.
- New dependencies require a reason in the PR, a license check, and (for Python) available Windows
  wheels for the supported Python version.
- Current runtime NuGet dependency: `Vortice.Direct3D11` 3.8.3 (MIT, Direct3D 11 bindings; transitively
  `Vortice.DXGI` and `SharpGen.Runtime` 2.4.2-beta). `dotnet list package --vulnerable --include-transitive`
  reported no known vulnerabilities on 2026-11-19. The prerelease transitive dependency is tracked in
  [ADR-0005](decisions/ADR-0005-windows-graphics-capture.md).
- Python engine dependencies will be pinned in a lock file once validated (Phase 2+).
- Release builds will ship an SBOM and third-party notices (Phase 8).
- CI workflows run with read-only `contents` permission.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately through GitHub's
[private vulnerability reporting](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability)
on this repository ("Security" tab → "Report a vulnerability"), or contact the repository owner
directly. Include affected version/commit, reproduction steps and impact. Do not include private
screen content in the report.

We aim to acknowledge reports within 7 days. As a pre-release project, there are no supported
release versions yet; fixes land on `main`.
