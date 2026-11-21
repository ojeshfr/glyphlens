# GlyphLens Worker IPC Protocol

**Protocol version:** 1.0
**Status (Phase 2a):** transport, framing, envelope validation, the handshake and three requests
(`worker.initialize`, `worker.health`, `worker.shutdown`) are **implemented** on both sides. Everything else
(`ocr.detect`, `translation.*`, `models.*`) is **not implemented**: the worker answers it with an error, never with a result.

Implementation map:

| Side | Code |
| --- | --- |
| .NET framing / connection / manager | `src/GlyphLens.Infrastructure/IPC/` (`WorkerFraming`, `WorkerConnection`, `WorkerProcessManager`, `WorkerMessageSerializer`) |
| .NET pipe server, job object, launcher | `src/GlyphLens.Windows/Worker/` |
| Python worker | `worker/glyphlens_worker/` (`runtime/framing.py`, `runtime/pipe.py`, `runtime/dispatcher.py`, `runtime/server.py`, `protocols/envelope.py`) |

Python dependency compatibility for the future engines is recorded separately in
[research/PYTHON-ENGINE-COMPATIBILITY.md](research/PYTHON-ENGINE-COMPATIBILITY.md).

## 1. Transport

- **Windows Named Pipe**, byte mode, full duplex, local machine only. The application is the **server**; the worker is the
  single **client**.
- Pipe name: `glyphlens-<32 lowercase hex>` (128 random bits), generated per session by the application
  (`NamedPipeWorkerListenerFactory`). The worker refuses any other name (exit code 2).
- **Access control** (explicit DACL replacing the default): (1) *deny* `NT AUTHORITY\NETWORK` everything, so a connection that
  arrives over the network redirector is refused; (2) *allow* the current user full control. Nobody else (Everyone, Users,
  Authenticated Users, Anonymous) is granted anything. The owner is the current user.
- `maxNumberOfServerInstances = 1`: one client at a time. The first instance is created before the worker is launched; after a
  rejected client is dropped, the next instance is created for the next accept.
- No TCP or HTTP listener exists.
- Whether a loopback SMB client is also rejected by the OS (beyond the explicit deny entry) has **not been verified**.

### Framing

Each message is a 4-byte **unsigned little-endian** length followed by that many bytes of UTF-8 JSON (no BOM).

- Zero-length frames are rejected.
- Frames larger than **16 MiB** (16 777 216 bytes) are rejected. The length is checked **before** any payload buffer is
  allocated or read, so a hostile length cannot cause a large allocation.
- Reads loop until the header or payload is complete (partial reads and writes are handled on both sides); a stream ending
  mid-frame is a framing error. Clean end of stream between frames means the peer closed.
- A framing violation cannot be resynchronised and is fatal for the connection.

## 2. Authentication and session token

The application generates a random 256-bit token per session (hex) and passes it to the worker in the environment variable
`GLYPHLENS_WORKER_TOKEN` (not on the command line, which other processes can read). The worker removes it from its environment
at startup and **echoes it** as `sessionToken` in `worker.initialize.result`. The application compares it in constant time; a
client that does not present the token (or never answers the handshake) is disconnected and the pipe accepts again, at most
`MaxRejectedClients` (3) times within the connect timeout. A peer that answers the handshake with an `error` response, or reports an
incompatible protocol major, is not retried: the start fails immediately. The token is never logged.

## 3. Message envelope

Every message is a JSON object with these camelCase fields (unknown extra fields are ignored):

| Field | Type | Rules |
| --- | --- | --- |
| `protocolVersion` | string | `MAJOR.MINOR`, digits only, major >= 1. |
| `requestId` | string | 1-128 non-blank characters. The app uses a GUID. Responses echo the request id. |
| `messageType` | string | Lowercase dotted identifier `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$`. |
| `payload` | object or null | The field must be present; may be `null`. |
| `timestamp` | string | ISO 8601 with a UTC offset. |

## 4. Implemented messages

Successful response type = request type + `.result`; failures use `error`. Both echo the `requestId`.

### `worker.initialize` (must be first)

Request payload: `{ "clientProtocolVersion": "1.0" }`. Result payload:

```json
{ "workerVersion": "0.1.0", "protocolVersion": "1.0", "python": "3.12.10",
  "sessionToken": "<echo of GLYPHLENS_WORKER_TOKEN>",
  "engines": { "ocr": "not_implemented", "languageDetection": "not_implemented", "translation": "not_implemented" } }
```

A different **major** client version is refused (`unsupported_protocol_version`); repeating initialize is harmless.

### `worker.health`

Request payload `null`. Result payload (state values are lower snake case):

```json
{ "process": "ready", "ipc": "connected", "protocol": "initialized",
  "engines": { "ocr": "not_implemented", "languageDetection": "not_implemented", "translation": "not_implemented" },
  "models": "not_installed", "workerVersion": "...", "protocolVersion": "1.0", "python": "...", "pid": 1234 }
```

Each field is a separate fact: a live worker never marks an engine ready. The application shows these as upper snake case
(`NOT_IMPLEMENTED`, `NOT_INSTALLED`). Unrecognised values parse as `unknown`, never as healthy.

### `worker.shutdown`

Request payload `null`. Result `{ "status": "stopping" }`, then the worker exits with code 0.

### Translation and language identification (protocol 1.0, frozen for the translation MVP)

Everything below is local. The worker makes **no network request** while handling these messages; model installation reads a
file the user chose and never downloads one. The worker also installs a fail-closed guard at start-up that makes Python-level
socket connects and name lookups raise (it cannot see native code; see [research/TRANSLATION-COMPATIBILITY.md](research/TRANSLATION-COMPATIBILITY.md)).
Request and result payloads are private screen content: they are never logged, and error messages never repeat them.

**Engines.** Translation runs Argos *model packages* directly with CTranslate2 and SentencePiece; the `argostranslate` library is
deliberately not used (its first translation contacts GitHub, and it needs about 1 GB of dependencies; see
[ADR-0006](decisions/ADR-0006-direct-argos-model-engine.md)). Language identification decides Chinese (Simplified or Traditional), Japanese, Korean and plain English by script rules without any library;
Lingua (Python 3.12+) or py3langid is used for other languages when installed.

**Common limits** (the worker enforces them; the application stays inside them):

| Limit | Value |
|---|---|
| Texts per `language.detect` | 64 |
| Items per `translation.translate` | 32 (the application sends at most 8 at a time) |
| Characters per text | 4096 for both messages (Python code points; the application counts UTF-16 units, which are never fewer, so a text that passes the application's check passes the worker's) |
| Item `id` | 1-64 characters, unique within the request, echoed back |
| Language codes | lowercase, `^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$`; the worker uses the two-letter form Argos uses (`de`, `zh`) |

**Component states** reported by `worker.health` / `worker.initialize` for the new engines (wire values lower snake case):

| Field | Value | Meaning |
|---|---|---|
| `engines.languageDetection` | `installed` | always available: the script rules (Chinese, Japanese, Korean, plain English) need no library; Lingua or py3langid adds other languages when installed |
| | `ready` | has answered a request |
| `engines.translation` | `unavailable` | the translation runtime (CTranslate2 and SentencePiece) is missing or failed to load |
| | `not_installed` | the runtime is present but no translation package is installed |
| | `installed` | at least one package is installed; no translation has loaded a model yet |
| | `ready` | the runtime loaded and a package was listed or used successfully |
| `engines.ocr` | `unavailable` / `not_installed` / `installed` / `ready` | OCR runtime missing or failed / runtime present but the PP-OCRv5 files are missing / files present / has recognised a frame |
| `models` | `not_installed` / `installed` / `unavailable` | whether any package is installed (`unavailable` = the storage could not be read) |

Health never imports a heavy library: it uses cheap checks (`importlib.util.find_spec`, a directory scan) plus what earlier
requests already learned. The application "warms up" the engines by sending `models.list` after the worker starts.

#### `language.detect`

```json
// request
{ "texts": ["Hallo Welt", "..."], "maxAlternatives": 3 }
// result: one entry per text, same order
{ "detector": "script+lingua 2.2.0",
  "results": [ { "language": "de", "confidence": 0.98,
                 "alternatives": [ { "language": "nl", "confidence": 0.01 } ], "issue": "none" },
               { "language": null, "confidence": null, "alternatives": [], "issue": "too_short" } ] }
```

`maxAlternatives` is optional (0-5, default 3). `issue` is `none`, `too_short` (fewer than 3 letters; one CJK character is a whole word and is identified), `no_text` (no letters) or
`unknown` (the detector had no answer). `confidence` is the library's own probability, or `null` when it reports none; the worker
never invents one. The worker does not decide what is "confident enough": the application applies its threshold and lets the
user override the language.

#### `translation.translate`

```json
// request: sourceLanguage is required per item (the application detects or the user chooses first)
{ "targetLanguage": "en", "items": [ { "id": "r1", "text": "Hallo Welt", "sourceLanguage": "de" } ] }
// result: one entry per item, same order; one failing item does not fail the others
{ "engine": "ct2-4.9.0",
  "results": [
    { "id": "r1", "status": "ok", "translatedText": "Hello world", "sourceLanguage": "de", "targetLanguage": "en",
      "modelVersion": "ct2-4.9.0/seg4|argos:de-en:1.9:12f4dd41956b", "route": ["de", "en"] },
    { "id": "r2", "status": "error",
      "error": { "code": "model_missing", "message": "No installed model translates ja to en.", "requiredPackage": "translate-ja_en" } } ] }
```

`modelVersion` is an opaque string identifying the engine revision and the installed package(s) used: per hop
`argos:<from>-<to>:<package_version>:<first 12 hex of sha256(model.bin)>`, hops joined with `>` for a pivot route, prefixed by the
engine tag (`ct2-<ctranslate2 version>/seg<splitter revision>|`). It changes when a package is installed, replaced or re-imported
(even with the same package version) and is part of the application's cache key. The application never parses it. Item error codes: `model_missing`,
`unsupported_language_pair`, `same_language`, `empty_text`, `text_too_long`, `translation_failed`. Request-level failures use the
`error` message: `invalid_request` (shape, limits, duplicate ids), `engine_unavailable` (library missing or failed to load).

**Cancellation.** The worker handles one request at a time over a blocking pipe, so it cannot read `translation.cancel` while it is
translating. The application therefore cancels by (a) keeping batches small (at most 8 items, so a stale request finishes in about a
second), (b) abandoning the wait and discarding the stale result, and (c) never having more than one translation request in flight.
`translation.cancel` stays reserved (answered `engine_unavailable`).

#### `models.list`

Request payload `null` or `{}`. Loads the engine if needed (may take seconds on first use).

```json
{ "engine": "ct2-argos", "engineVersion": "4.9.0", "state": "installed",
  "storagePath": "C:\\Users\\me\\AppData\\Local\\GlyphLens\\models",
  "packages": [ { "from": "de", "to": "en", "fromName": "German", "toName": "English", "version": "1.9", "packageName": "translate-de_en" } ],
  "problems": [] }
```

`state` is `unavailable` (with `problems` explaining why), `not_installed` or `installed`.

#### `models.status`

Request `{ "source": "de", "target": "en" }`. Result:

```json
{ "source": "de", "target": "en", "available": true, "route": ["de", "en"], "modelVersion": "ct2-4.9.0/seg4|argos:de-en:1.9:12f4dd41956b",
  "missingPackage": null, "issue": "none" }
```

`route` lists the languages the translation passes through. The worker prefers a direct package, otherwise the shortest path over
the installed packages (at most 2 hops, so `de -> en -> fr` works when both packages are installed; a longer route is not offered). When not available, `issue` is
`model_missing` (with the conventional `missingPackage`, for example `translate-ja_en`), `unsupported_pair` (a language the engine
does not support, or source equal to target) or `engine_unavailable`. `missingPackage` is a naming suggestion: the worker has no
network and cannot confirm the package exists upstream.

#### `models.install`

The explicit, user-initiated way to add a model. Request `{ "path": "C:\\Users\\me\\Downloads\\translate-de_en-1_9.argosmodel" }`.

The worker validates before installing: absolute path, `.argosmodel` extension, a regular file of at most 1 GiB, a readable zip
archive with exactly one top-level directory, an allow-list of entry names, a `metadata.json` whose `from_code`, `to_code` and
`package_version` are valid, `model/model.bin`, `model/config.json` and a `sentencepiece.model`, no entry that escapes the archive
(zip slip), no absolute names or symlinks, and bounded entry count, expansion ratio and total uncompressed size (enforced while
streaming, not from the headers). It extracts only the files it needs (the `stanza/` data is skipped), into a staging directory, then
moves it into place atomically. Packages that use a BPE tokenizer (`bpe.model`: cs, da, es, fi, hu, id, lt, ro, uk) are not
supported yet and are refused with `unsupported_package`. There is no signature to verify (Argos publishes none), so importing is a
trust decision for the user; the worker records the archive's SHA-256 in its own manifest.

```json
{ "package": { "from": "de", "to": "en", "fromName": "German", "toName": "English", "version": "1.9", "packageName": "translate-de_en" },
  "replaced": false }
```

Error codes: `invalid_request`, `package_not_found`, `invalid_package`, `unsupported_package`, `install_failed`,
`engine_unavailable`.

**Model storage.** `%LOCALAPPDATA%\GlyphLens\models\<from>_<to>\` (the files the engine needs plus a `glyphlens-model.json` manifest),
overridable with `GLYPHLENS_MODELS_DIR` (absolute path; used by tests). Nothing is written to Argos' own directories because Argos is
never imported. Model files are never part of the repository.

#### `ocr.detect`

Recognises the text lines of one screen capture with the worker's local OCR engine (PaddleOCR PP-OCRv5 mobile, run with ONNX Runtime;
see [ADR-0006](decisions/ADR-0006-direct-argos-model-engine.md) and [research/CHINESE-FIRST-EVALUATION.md](research/CHINESE-FIRST-EVALUATION.md)).
One model reads Simplified and Traditional Chinese, English, Japanese and digits, so there is no language argument.

```json
// request: one PNG (8-bit RGB), base64 in the JSON string (at most 40 megapixels and 16384 px on a side; the whole message must stay under 16 MiB)
{ "image": { "format": "png", "dataBase64": "iVBORw0KGgo..." } }
// result: lines in reading order (top to bottom, left to right within a row), pixel boxes of the submitted image
{ "engine": "paddleocr 3.7.0 PP-OCRv5 mobile, onnxruntime 1.32.0", "width": 564, "height": 687, "elapsedMs": 612,
  "regions": [ { "text": "...", "x": 34, "y": 82, "width": 382, "height": 23, "confidence": 0.987 } ] }
```

`confidence` is the recogniser's own score for the line in [0, 1]. An image without text is an empty `regions` list, not an error.
Error codes: `invalid_request` (not a PNG, bad base64, too many pixels), `engine_unavailable` (the OCR runtime or its model files are
missing; the message says how to install them), `ocr_failed` (the recogniser raised; the message holds only the exception type).
The models are read from `<models>/ocr/PP-OCRv5_mobile_det_onnx` and `.../PP-OCRv5_mobile_rec_onnx` (each with `inference.onnx` and
`inference.yml`); they are placed there by the user, never downloaded by the worker. `engines.ocr` in `worker.health` is
`unavailable` (libraries missing or failed), `not_installed` (libraries present, model files missing), `installed` or `ready`.

The frame is encoded as PNG in memory by the application and sent only over the authenticated local pipe; it is never written to disk.

#### Still reserved

`translation.cancel` is a known type and receives `error` / `engine_unavailable`. Any other type receives `unknown_message_type`.

## 5. Errors

```json
{ "protocolVersion": "1.0", "requestId": "<id>", "messageType": "error",
  "payload": { "code": "not_initialized", "message": "...", "retryable": false }, "timestamp": "..." }
```

Codes the worker actually emits: `invalid_message`, `unsupported_protocol_version`, `unknown_message_type`,
`not_initialized`, `engine_unavailable`, `payload_too_large`, `internal_error`, and for the translation messages
`invalid_request`, `package_not_found`, `invalid_package`, `unsupported_package`, `install_failed`. (`timeout`, `cancelled`,
`duplicate_request_id`, `model_not_installed` from the earlier draft are not used.)

- Error messages and logs never contain payload content.
- A well-framed but invalid message (bad JSON, invalid UTF-8, bad envelope, missing request id, unsupported version) gets an error
  response (`requestId` is echoed if readable, else `"unknown"`) and the worker keeps serving. **Five invalid messages in a row**
  are fatal (exit 4); a valid message resets the count.
- A framing violation (zero length, oversized, truncated): best-effort error, then exit 4.
- On the .NET side, any invalid message from the worker closes the connection and fails all waiting requests.

## 6. Correlation, concurrency, timeouts

- Requests are correlated by `requestId`; responses may arrive in any order. At most 32 requests may wait at once (more are
  rejected, not queued). Late or unsolicited responses are ignored and logged without content.
- The worker handles one request at a time. The application's request channel refuses `worker.initialize` and `worker.shutdown`
  (only the process manager sends them), never starts a worker by itself, and a request timeout does not close the connection.
- Defaults (`WorkerManagerOptions`): connect 15 s, initialize 10 s, request (health) 5 s, shutdown acknowledgement 3 s, graceful
  exit 5 s. A request timeout does not close the connection. Three consecutive health timeouts treat the worker as hung (killed
  and restarted). **Periodic health polling is not implemented**; health is queried at start and on demand.

## 7. Worker lifecycle

1. The manager creates a listener (new pipe name + token) and launches
   `python -E -X utf8 -u -m glyphlens_worker --pipe <name>` from the worker directory, no console window, stdout/stderr redirected
   and drained (last 20 sanitised error lines kept for diagnostics).
2. The worker connects with plain `open(r"\\.\pipe\<name>", "r+b", buffering=0)`, retrying while the pipe is missing or busy
   (a busy pipe is reported by the C runtime as `OSError(errno=22)` without `winerror`; access denied fails immediately).
3. The app sends `worker.initialize`, verifies token and protocol major, then `worker.health`; status becomes **Ready**.
   Ready means process + pipe + handshake, **not** that any engine works.
4. Stop: `worker.shutdown`; if not acknowledged/exited in time the process (tree) is killed.
5. Unexpected exit or lost pipe: status Error, then up to **5** automatic restarts with exponential backoff **1 s, 2 s, 4 s ... capped at
   60 s**; after that Unavailable. A worker that ran >= 60 s before failing resets the budget; an explicit start/restart resets it.
   Nothing restarts after an intentional stop; Stop cancels an in-flight start or backoff delay. A missing interpreter or worker
   package is not retried.
6. If the application dies, a kill-on-close **Job Object** ends the worker. Limitation: a process cannot be created suspended, so
   there is a short window before assignment (this matters for virtual-environment launcher stubs); the worker therefore also exits
   itself when the pipe closes (exit 5).

Interpreter discovery: explicit option, then `GLYPHLENS_PYTHON`, then `python.exe` on `PATH` (skipping `WindowsApps` Store stubs), then the
`py` launcher; each candidate is executed once to confirm Python >= 3.11. The worker package is found via `GLYPHLENS_WORKER_DIR` or by
walking up from the application directory.

## 8. Worker exit codes

| Code | Meaning |
| --- | --- |
| 0 | Clean shutdown after `worker.shutdown`. |
| 2 | Usage / invalid pipe name. |
| 3 | Could not connect to the pipe. |
| 4 | Protocol violation (framing error, or too many invalid messages). |
| 5 | Pipe lost (application closed it without shutdown, or write failed). |
| 6 | Session token missing or invalid. |

## 9. Version compatibility

Compatible when major versions match; minor versions add optional fields or message types. Breaking changes require a major bump, an
update here and an ADR. Authoritative values: `ProtocolVersion.Current` (.NET), `PROTOCOL_VERSION` (Python).

## 10. Security constraints

Local pipe only; current-user ACL with explicit network deny; single client; unique unguessable name; session token handshake; strict
validation and size limit before dispatch; no message executes code, reads arbitrary paths or opens network connections; no payloads
in logs. The worker itself makes no network requests (it has no dependencies in this milestone).
