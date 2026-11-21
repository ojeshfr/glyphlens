# GlyphLens Worker

The local inference worker for GlyphLens. It talks to `GlyphLens.exe` over a Windows Named Pipe (see
[`docs/IPC-PROTOCOL.md`](../docs/IPC-PROTOCOL.md)) and, in this build, provides **language identification** and
**offline translation**. It never touches the network.

## Status

Implemented:

- Transport, framing, envelope validation, the handshake (`worker.initialize`, `worker.health`, `worker.shutdown`).
- `language.detect`: Lingua (Python 3.12+) or py3langid (Python 3.11), built over every Argos language.
- `translation.translate`, `models.list`, `models.status`, `models.install`: Argos model packages run directly with
  CTranslate2 and SentencePiece (`argostranslate` is never imported; see
  [ADR-0006](../docs/decisions/ADR-0006-direct-argos-model-engine.md)), with a validating importer, route finding
  (direct or one English pivot) and an honest state for every component.
- A fail-closed Python-level network guard, installed at start-up.

Not implemented: OCR (`ocr.detect`) and `translation.cancel` answer `engine_unavailable`.

Known limits:

- **BPE packages are not supported.** Nine official into-English packages use a BPE tokenizer (`bpe.model`): cs, da, es,
  fi, hu, id, lt, ro, uk. Spanish is among them. `models.install` refuses them with `unsupported_package`. Supporting them
  needs a vendored `apply_bpe` and `sacremoses`; it is a roadmap item.
- Non-Latin source languages (ja, zh, ko, ar, ru) and two-hop routes have only been tested with fakes, not with real models.
- The sentence splitter is simple (an abbreviation such as `M. Dupont` splits wrongly); OCR text arrives line by line.
- A text of fewer than three letters is not identified (`too_short`), which includes two-character CJK words.
- The network guard only sees Python code. CTranslate2 and SentencePiece are native libraries; native sockets are invisible
  to it. Verify with a firewall rule or a packet capture if that matters to you.

## Install the engines

The worker starts with the standard library alone; without a library an engine reports `unavailable` and says how to fix
it. Install the libraries into the interpreter the application launches (`GLYPHLENS_PYTHON`, or the first suitable
`python`), with exact versions:

```powershell
# Python 3.12 (recommended): translation and the most accurate language identification (about 410 MB installed)
python -m pip install ctranslate2==4.9.0 sentencepiece==0.2.3 lingua-language-detector==2.2.0

# Python 3.11: translation and the lighter language identification (about 125 MB installed)
python -m pip install ctranslate2==4.9.0 sentencepiece==0.2.3 py3langid==0.4.0

# the same through the package's extras, from this directory
python -m pip install ".[translation,lingua]"      # Python 3.12
python -m pip install ".[translation,langid]"      # Python 3.11
```

Lingua 2.2.0 needs Python 3.12 or newer; on 3.11 only a much heavier older release exists (878 MB, 15 s first call), so
the worker uses py3langid there. Lingua cannot identify Galician or Kyrgyz.

## Models

Models are Argos `.argosmodel` files that **you** download and import; nothing is ever downloaded by the worker (the
application's Models view sends `models.install` with the path you chose). Official packages are listed in the
[Argos package index](https://github.com/argosopentech/argospm-index); for example
`https://argos-net.com/v1/translate-fr_en-1_9.argosmodel`. Licences differ per package (most state CC-BY 4.0); the licence
text inside the package is kept next to the model, so check it before redistributing. There is no signature to verify, so
importing a package is a trust decision.

| | |
| --- | --- |
| Directory | `%LOCALAPPDATA%\GlyphLens\models`, or the absolute local path in `GLYPHLENS_MODELS_DIR` (a relative or network path is ignored and reported as a problem). Created on the first installation, never at start-up. |
| Layout | one directory per package, `<from>_<to>\` with `glyphlens-model.json` (the worker's manifest: archive and `model.bin` SHA-256, sizes), `metadata.json`, `sentencepiece.model`, `model\` and the licence text. |
| Import | validated: absolute local path, `.argosmodel`, at most 1 GiB, one top-level directory, allow-listed entries, no zip-slip, links or reserved names, entry count, ratio and size limits enforced while streaming. Only the needed files are extracted (never the Stanza data) into a staging directory that is moved into place atomically; staging is removed on any failure. |
| Version string | `ct2-<ctranslate2 version>/seg<splitter revision>\|argos:<from>-<to>:<package version>:<first 12 hex of the model.bin hash>` per hop, joined by `>`. It changes when a package is installed, replaced or re-imported. |
| Routes | a direct package, else the shortest path of at most two hops (`de -> en -> fr`), with `pt`/`pb` and `zh`/`zt` standing in for each other. Missing packages are named, for example `translate-ja_en`. |

## Usage

From the `worker/` directory (no install or third-party packages needed for the tests):

```powershell
python -m glyphlens_worker --version
python -m glyphlens_worker --health
python -m unittest discover -s tests -t . -v
```

Tests that need the engine libraries are skipped when they are not installed. One opt-in test imports a real model and
translates French to English with the network guard on:

```powershell
$env:GLYPHLENS_TEST_FR_EN_MODEL = 'D:\path\to\translate-fr_en-1_9.argosmodel'
python -m unittest tests.test_real_model -v
```

## Layout

| Path | Responsibility |
| --- | --- |
| `glyphlens_worker/main.py` | Command-line entry point, diagnostics, network guard at start-up. |
| `glyphlens_worker/config/settings.py` | Pipe name, session token and model directory. |
| `glyphlens_worker/protocols/` | IPC message envelope and validation. |
| `glyphlens_worker/runtime/` | Pipe server, request dispatch, health, service wiring, network guard. |
| `glyphlens_worker/engines/language_detection/` | Lingua and py3langid backends, the `language.detect` service. |
| `glyphlens_worker/engines/translation/` | CTranslate2 backend, segmentation, routes, translator cache, the translation service. |
| `glyphlens_worker/engines/payloads.py` | Shared request limits and shape checks. |
| `glyphlens_worker/engines/ocr/` | OCR. Not implemented. |
| `glyphlens_worker/models/` | Manifest, registry (scan) and the validating importer. |
| `glyphlens_worker/languages.py` | The Argos language catalogue and code mapping. |

## Rules

- No network access, ever. The worker imports no Argos library and makes no request; model installation reads a file the
  user chose.
- Payload content (screen text, file contents) is private: never logged, never in an error message. Logs and errors carry
  request ids, message types, counts, durations and error codes only.
- Nothing is read or written outside the model directory. The worker handles one request at a time.
- Never persist screen pixels or recognised text.

## Chinese OCR (`ocr.detect`)

PaddleOCR's PP-OCRv5 mobile text detection and recognition models run with ONNX Runtime (the `paddlepaddle` wheel is not needed):

```powershell
python -m pip install -e ".[ocr]"      # paddleocr==3.7.0, onnxruntime
```

Place the two official ONNX model folders (`PP-OCRv5_mobile_det_onnx` and `PP-OCRv5_mobile_rec_onnx`, each with `inference.onnx` and
`inference.yml`) in `<models>\ocr\` (default `%LOCALAPPDATA%\GlyphLens\models\ocr\`). The worker never downloads them. It reads
Simplified and Traditional Chinese, English, Japanese and digits and answers with the lines, their pixel boxes and the recogniser's own
confidence. ONNX Runtime is limited to 4 threads on purpose (measured fastest on a 20-thread hybrid CPU).
`worker.health` reports `engines.ocr` as `unavailable`, `not_installed`, `installed` or `ready`. Protocol: `docs/IPC-PROTOCOL.md`.
