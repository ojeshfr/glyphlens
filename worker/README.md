# GlyphLens Worker

The local inference worker for GlyphLens. It will eventually be packaged as `GlyphLens.Worker.exe`
and host OCR, language identification and offline translation, talking to `GlyphLens.exe` over a
Windows Named Pipe (see [`docs/IPC-PROTOCOL.md`](../docs/IPC-PROTOCOL.md)).

## Status (Phase 0)

Implemented:

- Package layout and entry point.
- `--version` and `--health` diagnostics.
- Protocol envelope validation (`glyphlens_worker.protocols.envelope`), mirroring the .NET side.

Not implemented: OCR, language detection, translation, model management, the Named Pipes transport.
`--health` reports every engine as `not_implemented`.

## Usage

From the `worker/` directory (no install or third-party packages needed):

```powershell
python -m glyphlens_worker --version
python -m glyphlens_worker --health
python -m unittest discover -s tests -v
```

## Layout

| Path | Responsibility |
| --- | --- |
| `glyphlens_worker/main.py` | Command-line entry point and diagnostics. |
| `glyphlens_worker/config/` | Worker settings (model directory, limits). Future. |
| `glyphlens_worker/protocols/` | IPC message envelope and validation. |
| `glyphlens_worker/runtime/` | Pipe server, request dispatch, cancellation. Future. |
| `glyphlens_worker/engines/ocr/` | PaddleOCR adapter. Future. |
| `glyphlens_worker/engines/translation/` | Argos Translate adapter. Future. |
| `glyphlens_worker/engines/language_detection/` | Local language identification. Future. |
| `glyphlens_worker/models/` | Installed-model metadata and verification. Future. |

## Rules

- No network access at inference time. Model downloads, when added, are explicit user actions only.
- Never persist screen pixels or recognised text.
