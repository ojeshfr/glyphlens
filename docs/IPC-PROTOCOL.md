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

### Not implemented

`ocr.detect`, `translation.translate`, `translation.cancel`, `models.list`, `models.status` are known types and receive
`error` / `engine_unavailable`. Any other type receives `unknown_message_type`. Cancellation semantics for engine requests are
therefore not yet exercised.

## 5. Errors

```json
{ "protocolVersion": "1.0", "requestId": "<id>", "messageType": "error",
  "payload": { "code": "not_initialized", "message": "...", "retryable": false }, "timestamp": "..." }
```

Codes the worker actually emits: `invalid_message`, `unsupported_protocol_version`, `unknown_message_type`,
`not_initialized`, `engine_unavailable`, `payload_too_large`, `internal_error`. (`timeout`, `cancelled`,
`duplicate_request_id`, `model_not_installed` from the earlier draft are not used yet.)

- Error messages and logs never contain payload content.
- A well-framed but invalid message (bad JSON, invalid UTF-8, bad envelope, missing request id, unsupported version) gets an error
  response (`requestId` is echoed if readable, else `"unknown"`) and the worker keeps serving. **Five invalid messages in a row**
  are fatal (exit 4); a valid message resets the count.
- A framing violation (zero length, oversized, truncated): best-effort error, then exit 4.
- On the .NET side, any invalid message from the worker closes the connection and fails all waiting requests.

## 6. Correlation, concurrency, timeouts

- Requests are correlated by `requestId`; responses may arrive in any order. At most 32 requests may wait at once (more are
  rejected, not queued). Late or unsolicited responses are ignored and logged without content.
- The worker handles one request at a time.
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
