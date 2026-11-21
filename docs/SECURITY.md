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

## Text recognition (Phase 2b)

- Recognition runs **locally and offline** with the built-in Windows OCR (`Windows.Media.Ocr`) inside the GlyphLens process.
  The recognition code adds no network call, downloads nothing and installs no packages or language packs; Windows manages the OCR
  languages. (That Windows OCR itself makes no network requests is Microsoft's documented behaviour; it was not independently
  verified here, for example with a network capture.)
- Recognition happens only on user action: **Read text** or the **Live** toggle, on the captured frame the user chose to
  capture. Live reading ends when the user turns it off, when capture stops or faults, and when the application exits.
- Recognised text lives **only in memory** (the OCR view model and the overlay state). It is never written to disk, logged or
  uploaded. It is sent to the local worker only when the user turns translation on (see "Translation" below). It is cleared when
  capture stops.
- Error messages never include recognised or captured text: an unexpected exception is shown by its type only.
- The clipboard is touched **only** when the user presses Copy.
- The recognised text and boxes are visible in GlyphLens's own window, so they appear in any screenshot of it; this is a
  consideration when sharing screenshots (see the issue template guidance).

## Translation and language identification

- Both run **locally** in the Python worker (a child process of the app, reached over the authenticated named pipe). Recognised text
  is sent to the worker **only while translation is switched on**, and never leaves the machine.
- **No network activity in the recognition and translation path** (the app's only network uses are the updater and the user-started "Install everything", see below). The translation engine is `ctranslate2` + `sentencepiece` reading model files from
  disk. The `argostranslate` library is deliberately not used: it contacts GitHub at the first translation
  ([ADR-0006](decisions/ADR-0006-direct-argos-model-engine.md), [evidence](research/TRANSLATION-COMPATIBILITY.md)). The worker
  also installs a fail-closed Python-level network guard at start-up. **Limits of that guard**: it does not see sockets opened by
  native code (CTranslate2, SentencePiece), so it is defence in depth, not proof. A Windows Firewall outbound rule or a
  `netstat` check on the worker process is the independent check (the end-to-end test records it).
- **No automatic model downloads.** Installing a model is an explicit user action: either importing a `.argosmodel` file the user
  downloaded themselves, or "Install everything" (see "One-click setup" below), which downloads the pinned packages only after the
  user confirmed and then imports them through this same importer. The import validates the archive before installing: absolute local path (UNC and network paths are
  refused), `.argosmodel` extension, size limit, zip-slip / absolute-path / symlink checks, an entry allow-list, entry-count,
  expansion-ratio and total-size caps enforced while streaming, a metadata schema check, extraction only of the needed files into a
  staging directory, then an atomic move. **Argos publishes no signatures or checksums**, so importing a package is a trust
  decision: the model file is parsed by native code. The worker records the archive's SHA-256 so the user can compare it with
  the download source.
- Models are stored in `%LOCALAPPDATA%\GlyphLens\models` and are never committed to the repository. Licences differ per
  package (most are CC-BY 4.0): the README inside the package is the source of truth.
- Recognised and translated text is held only in memory: the translation cache and the language-detection cache are bounded LRU
  maps that are never persisted, are cleared when capture stops, and are keyed with the model version so a replaced model never
  serves old results.
- Logs and error messages never contain recognised or translated text, file names from archives, or paths; they carry request ids,
  message types, counts, error codes and durations.
- The clipboard is touched only by the separate **Copy original** / **Copy translation** buttons.
- A language detector can be wrong on short text. The app never silently guesses: low confidence shows "language uncertain" and the
  user can choose the language.

## OCR in the worker and the overlay

- With the local Chinese OCR selected, each frame is encoded as PNG **in memory** and sent over the authenticated local pipe to the
  worker (`ocr.detect`). It is never written to disk or logged; the worker answers with text and boxes. The OCR model files
  (PP-OCRv5 ONNX) are placed in `<models>\ocr` by the user or by "Install everything" (pinned hashes); the worker never downloads them and passes the paths to PaddleOCR so it
  does not try to fetch anything (the network guard would refuse it).
- The floating overlay is excluded from screen capture (`WDA_EXCLUDEFROMCAPTURE`) so GlyphLens never reads its own translations back. This
  was checked with a control run: with the exclusion deliberately disabled the display capture's OCR read the overlay's own labels (5),
  with it enabled none (0). The diagnostic switch `GLYPHLENS_DISABLE_CAPTURE_EXCLUSION=1` exists only for that control. It is not a
  security boundary: any program can still capture the overlay with the OS tools that ignore the flag, and on Windows builds without
  the flag the overlay says the protection is unavailable.
- The overlay remembers only its layout (position, size, opacity, pin, collapsed) in `%LOCALAPPDATA%\GlyphLens\overlay.json`.
  Cards (original and translated text) live in memory only and are cleared on request.
- Global shortcuts use `RegisterHotKey`: they are not a keyboard hook and see no other keystrokes.

## Offline evidence and importer audit (2026-11-21)

What is **proven** and what is **not**, stated separately.

Proven by tests or measurements on one Windows 11 machine:

- **No remote connection during use.** `drive-incremental-e2e.ps1` sampled the TCP and UDP tables every ~2 s for 61 s of Live recognition and
  translation, for the app and all of its descendant processes (4 processes: the app, the venv launcher, the worker and its child): 28 samples,
  **0 remote TCP connections, 0 UDP endpoints**. `drive-chinese-e2e.ps1` repeats the check after a translation pass (3 worker-side processes).
  The Overview page runs the same kind of check on request through `WindowsConnectionProbe` (`GetExtendedTcpTable` / `GetExtendedUdpTable`); its
  tests use real sockets, including a connection to the PC's own network address that must be counted as remote.
- **The worker's Python socket guard fails closed** and recorded 0 attempts over the evaluation runs (`worker/tests/test_network_guard.py`).
- **The local link to the worker is a named pipe** with a per-session token, not a TCP socket (so it never appears in the connection tables).
- **The model importer** (`worker/tests/test_importer.py`, 40+ cases) refuses: symlinks (file, entry and ignored data), zip-slip and absolute names,
  device names, duplicate and case-colliding entries, encrypted entries, zip64, too many entries, oversized or lying central directories,
  expansion ratio and real size enforced while streaming, unexpected or multiple top-level entries, bad metadata; it installs atomically
  (temporary folder, then replace), removes leftovers of a crashed import, and leaves nothing behind after a damaged archive.
- **No screen data is persisted**: frames and text stay in memory; the overlay saves only its layout (`%LOCALAPPDATA%\GlyphLens\overlay.json`, the only
  file under that folder after the test runs; imported models went to the folder named by `GLYPHLENS_MODELS_DIR`). Logging avoids payloads by design (error paths format types, counts and codes only); that is by code
  review, **not** by a dedicated automated test.
- **No automatic downloads**: no code path in the app or the worker fetches a model on its own; models are imported from a file the user
  chose, or downloaded by "Install everything" after the user confirmed (added later the same day, see "One-click setup").

**Not proven** (do not assume these):

- Native libraries (CTranslate2, ONNX Runtime, OpenCV) are not covered by the Python guard. The connection tables show their sockets only at the moment
  they are read: a connection opened and closed between two samples would not be seen, and DNS or other traffic by another process is outside the check.
- No packet capture, no firewall-blocked run and no run with the network adapter disabled was done (the mission forbids changing firewall or adapters).
- The pipe name and token protect against other local *processes* only if they cannot read the worker's environment; a process running as the
  same user with the right privileges can still read the app's memory. This is not a defence against malware running as the user.
- The importer checks structure and size, not authenticity: a package is trusted only as far as the user trusts where they got it (no signature or checksum
  is verified against a publisher). Checksums of the packages used here are recorded in the research documents.
- The overlay's capture exclusion is verified only on Windows 11 build 26200; it is not a security boundary (OS tools that ignore the flag can still capture it).

## No unnecessary screen-data persistence

- Captured frames are held in memory only and discarded after processing.
- Logs must not contain recognised text, translations or pixels.
- Translation cache and history (future, SQLite) are stored locally in the user profile, can be
  cleared by the user, and history is opt-in.
- Bug reports must not include private screenshots (see the issue template).

## Secure local worker communication

Implemented in Phase 2a (see [IPC-PROTOCOL.md](IPC-PROTOCOL.md)):

- Windows Named Pipe, unique per session (`glyphlens-` + 128 random bits). The worker refuses any other name.
- Explicit pipe DACL: deny `NT AUTHORITY\NETWORK`, allow only the current user; one server instance, so one client at a time.
  (Whether a loopback SMB client is rejected beyond that deny entry is not verified.)
- Per-session random token passed in the environment (not the command line) and echoed in `worker.initialize.result`; clients that
  fail it are disconnected (bounded retries). A same-user process that wins the pipe first still cannot become the worker.
  Same-user processes remain inside the trust boundary otherwise.
- Strict envelope validation, a 16 MiB limit checked before allocation, zero-length rejected, malformed input answered with an error
  or a closed connection, and an allow-list of message types. No message executes code, opens network connections or reads paths.
- The worker is launched with `python -E -X utf8`, ignoring `PYTHONPATH`/`PYTHONHOME`.
- The worker cannot outlive the app: kill-on-close Job Object (short assignment race documented) and the worker exits when the
  pipe closes.
- Logs record request id, message type, outcome and duration only, never payloads or the token.
- The base worker has no third-party dependencies. The translation and language-identification engines are optional extras
  (`ctranslate2`, `sentencepiece`, `lingua-language-detector` or `py3langid`, pinned in `worker/pyproject.toml`). The worker makes
  no network requests; nothing is downloaded or installed at startup, by health checks or by translation.

## One-click setup ("Install everything") - network use started by the user

Added 2026-11-21 ([ADR-0008](decisions/ADR-0008-explicit-one-click-setup.md)). Normal operation stays offline; this is the only way
GlyphLens itself downloads software or models, and it never starts on its own.

- **When.** Only after the user clicks **Install everything** on the Overview page (offered when something needed for Chinese is
  missing) and then **Download and install** in the consent panel, which lists every download, its host, its size, its licence and the
  install folders. Nothing runs at start-up. Cancel stops it at any time.
- **What is contacted.** `github.com` (and GitHub's download servers it redirects to) for uv 0.13.7; `releases.astral.sh` for the
  Python 3.12.16 build (fetched by uv, which checks it against its own pinned hash); `pypi.org` / `files.pythonhosted.org` for the 70
  locked packages (fetched by uv with `--require-hashes --no-deps --no-build`); `huggingface.co` (and its CDN) for the four PP-OCRv5
  files at a fixed commit; `argos-net.com` for the zh and zt models. About 360 MB in total. Requests carry a `GlyphLens/<version>`
  User-Agent (GlyphLens' own downloads) or uv's own, no cookies, no identifiers, nothing from the screen. The system proxy is used
  (and handed to uv when no proxy variable is set).
- **What is verified.** GlyphLens' own downloads are HTTPS only (also after redirects; HTTPS to HTTP is refused); size and SHA-256 are
  enforced while streaming (a body larger than pinned is abandoned at once; `Content-Length` is not trusted alone); bytes go to
  `<name>.part` and are moved into place only after verification; on any failure or cancellation the partial file is deleted. Only
  `uv.exe` is extracted from uv's zip, and only if the zip has exactly the expected entries (no folders, no `..`) and `uv.exe` has its
  pinned SHA-256. A mismatch fails with "the file on the server changed ... do not use it". Argos publishes no signatures, so the
  pinned hash is the trust anchor: it proves "the same bytes as when the hash was pinned", not who made them.
- **What is written.** Only below the runtime folder (`%LOCALAPPDATA%\GlyphLens\runtime`, override `GLYPHLENS_RUNTIME_DIR`, which
  must be an absolute local path: otherwise it is refused and reported, never replaced) - `tools\uv.exe`, `python\` (the CPython
  build), `venv\` (the environment the worker then uses), `uv-cache\` (emptied after a successful install), `downloads\` (partial and
  waiting files), `engines.lock.txt`, `setup.log` - and the model files in the models folder. No administrator rights. uv runs with
  `--no-registry --no-bin --no-config` and its install and cache folders inside the runtime folder, so it writes no registry keys and
  nothing on `PATH` (checked after the real runs: no new key under `HKCU\Software\Python\Astral`). Inherited `PYTHON*`, `UV_*`, `PIP_*`,
  `VIRTUAL_ENV` and `CONDA*` variables are removed for uv and the Python checks; every Python check runs with `-I`.
- **Child processes.** uv and the checks run without a window, without a shell, with standard input closed, each in a kill-on-close job
  object; cancelling, a timeout or GlyphLens exiting ends the whole process tree (tested with real processes).
- **The log** (`setup.log`) holds the commands and the installers' output with timestamps, capped in size; never screen content.
- **Not covered.** The publishers' own supply chain (PyPI, Hugging Face, Argos, Astral); the Python build is trusted through the pinned
  uv's hash list; packages from PyPI keep their own licences and are not reviewed here line by line.

## Desktop shortcut (first run)

On its first run `GlyphLens.exe` creates `GlyphLens.lnk` on the user's desktop (the Shell's `IShellLinkW`, off the UI thread). It is
done once: a flag (`desktopShortcutHandled`) in `%LOCALAPPDATA%\GlyphLens\preferences.json` makes sure a shortcut the user deleted is
not created again; an existing shortcut is left alone; a failure is silent and retried at the next start (at most once per run).
`GLYPHLENS_NO_DESKTOP_SHORTCUT=1` turns it off.

## Updates (network use: the update check)

Added 2026-11-21 at the owner's request: automatic update checks, on by default. This is a deliberate change to the earlier "makes no
network requests" position; the privacy text in Settings and the website say so.

- **What is sent.** An HTTPS GET of the update feed (a small signed JSON file) about once a day, first about 30 s after start-up, and
  again 6 h after a failure. The request carries a `GlyphLens/<version>` User-Agent, no cookies and no identifier. The server and
  anything on the path see the user's IP address and that a GlyphLens of that version is running. Nothing from the screen, no text,
  no settings. The check can be turned off in Settings; with no feed URL or signing key built in (the state of this repository) the
  updater is inert and contacts nothing.
- **What is verified.** The feed is accepted only if its ECDSA P-256 / SHA-256 signature verifies with the public key built into
  the application, so a feed altered by whoever controls the web host is rejected. The manifest must name an HTTPS `.exe` or `.msi`,
  a plausible size (at most 1 GB) and a SHA-256. A downloaded installer is kept only if its size and SHA-256 match the signed
  manifest (partial files are deleted), is re-hashed immediately before it is run, and is run only when the user presses
  "Install and restart". Redirects are followed at most 3 times and must stay on HTTPS; the feed is capped at 64 KB.
- **Not covered.** (1) Custody of the private signing key is the owner's: if it leaks, an attacker can sign a malicious
  update (release steps in `docs/UPDATES.md`). (2) The installer itself is not Authenticode-signed here, so Windows SmartScreen will
  warn. (3) There is no rollback and no protection against a replayed older (but validly signed) feed, because versions are only
  compared as newer/not newer. (4) Everything above is covered by unit tests with fakes; no real update server or feed exists yet, so
  the real network path was not exercised end to end.

## Model provenance

- Models are installed only by explicit user action, from documented sources.
- Each imported model records its archive SHA-256, the SHA-256 of its weights, its package version and the import time in a
  manifest. The weights hash is part of the model version used in the translation cache key. (The upstream index offers no
  checksum or signature, so the hash records what was imported; it does not prove authenticity.)
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
- Python engine dependencies are pinned in `worker/pyproject.toml` extras (`ctranslate2==4.9.0`, `sentencepiece==0.2.3`,
  `lingua-language-detector==2.2.0`, `py3langid==0.4.0`) after the compatibility research. The one-click setup installs the OCR and
  translation engines from `worker/requirements/engines.lock.txt` (70 packages, all `==`-pinned with SHA-256 hashes, generated from the
  tested environment; regeneration in docs/DEVELOPMENT.md).
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
