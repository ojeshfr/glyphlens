# ADR-0008: Explicit one-click environment setup

- **Status:** Accepted
- **Date:** 2026-11-21
- **Amends:** [ADR-0003](ADR-0003-offline-processing.md) (how models and engines get onto the machine; processing stays local)

## Context

The first preview release was a zip with the app and the worker source. To translate Chinese a user had to create a Python
environment, `pip install` pinned engines, download the PP-OCRv5 files and an Argos package and import them by hand. A friend who tried
it ended with "Chinese OCR failed" and "Simplified Chinese model: not installed"; their system Python (3.14) has no wheels for the
pinned engines anyway. ADR-0003 already allowed "explicit, user-initiated, integrity-verified" model downloads, but nothing implemented
them, and engines were left entirely to the user.

## Decision

- **Normal operation stays offline. Nothing is downloaded automatically**: not at start-up, not by a health check, not by translation,
  not in the background. Recognition and translation never touch the network (unchanged).
- The Overview page offers **Install everything** when something needed for Chinese translation is missing (no usable Python, the OCR
  packages or model files, the translation runtime, or the Simplified Chinese model). The first click only opens a consent panel that
  lists every download, its host, its approximate size and licence, and the folders it installs to. Downloading starts only after
  **Download and install**. It can be cancelled at any time.
- Everything is pinned in one place (`GlyphLens.Core/Setup/SetupManifest.cs`): uv 0.13.7 (zip and `uv.exe` SHA-256), the uv-managed
  CPython build (`cpython-3.12.16-windows-x86_64-none`, which the pinned uv verifies against its own built-in hash list), the four
  PP-OCRv5 files at a fixed Hugging Face commit, and the two Argos 1.9 packages (zh, zt). The engine packages are installed by uv from
  a hash-locked requirements file (`worker/requirements/engines.lock.txt`, 70 packages, every one `==`-pinned with SHA-256 hashes,
  `--require-hashes --no-deps --no-build`), generated from the tested environment.
- A file whose size or SHA-256 differs from the pin is deleted and the step fails with "the file on the server changed; do not use it".
  Argos publishes no signatures, so the pinned hash is the trust anchor.
- Everything goes below `%LOCALAPPDATA%\GlyphLens\runtime` (or `GLYPHLENS_RUNTIME_DIR`) and the existing models folder. No administrator
  rights; uv is told not to register Python in the registry and not to add anything to `PATH`; inherited `PYTHON*`, `UV_*`, `PIP_*` and
  `CONDA*` variables are removed for uv. Translation models are imported through the existing validated importer.

## Alternatives considered

| Option | Why not chosen |
| --- | --- |
| Keep the manual steps | Not usable for the people the app is for (observed). |
| Bundle Python, packages and models in the release | About 700 MB per release, redistribution duties for every model and package, and every update re-ships all of it. Possible later as an offline installer. |
| Use the system Python | Often missing or a version without wheels (3.14 on the first tester's PC); changes to it would affect other software. |
| Download on first start without asking | Violates the "nothing downloads automatically" rule and the consent the privacy promise depends on. |
| `pip` instead of uv | Needs an existing Python; uv brings a verified, self-contained Python and installs with hash checking in one tool. |

## Consequences

- GlyphLens' network use is now: the update check (docs/UPDATES.md) and this user-started setup. SECURITY.md, the README and the
  in-app privacy text say so.
- The pins have to be refreshed deliberately (docs/DEVELOPMENT.md, "One-click setup"); the tests check the manifest's and the lock's shape.
- About 360 MB is downloaded; the runtime takes about 670 MB and the models about 190 MB on disk.
- Not covered: the authenticity of upstream files beyond "same bytes as when pinned", the publishers' own supply chain, and the Python
  build's provenance beyond uv's pinned hash.
