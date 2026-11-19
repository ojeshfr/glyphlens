# GlyphLens Worker IPC Protocol

**Protocol version:** 1.0 (draft)
**Status:** The envelope format and its validation are implemented on both sides
(`GlyphLens.Infrastructure.IPC.WorkerMessageSerializer`, `glyphlens_worker.protocols.envelope`).
The transport and all message handlers are **not implemented**. Payload schemas below are
provisional and will be finalised in the phase that implements each message type.

## 1. Transport

- **Windows Named Pipe**, full duplex, local machine only.
- The app (`GlyphLens.exe`) creates a pipe with a random, per-session name
  (`\\.\pipe\glyphlens-<random>`) and passes the name to the worker on its command line. The pipe
  is created with an ACL that grants access **only to the current user**, with
  `PipeOptions.CurrentUserOnly` and remote clients rejected (`PIPE_REJECT_REMOTE_CLIENTS`).
- The app is the pipe server; the worker connects as the single client. Only one connection is
  accepted (`maxNumberOfServerInstances = 1`).
- **Framing:** each message is a 4-byte little-endian unsigned length followed by that many bytes
  of UTF-8 JSON (no BOM). Messages larger than **16 MiB** are a protocol error.

There is no TCP/HTTP listener. The protocol is not reachable from other machines or other users.

## 2. Message envelope

Every message, in both directions, is a JSON object with exactly these top-level fields
(camelCase):

| Field | Type | Rules |
| --- | --- | --- |
| `protocolVersion` | string | `MAJOR.MINOR`, digits only, no leading zeros, major ≥ 1. |
| `requestId` | string | 1–128 non-blank characters. The app uses a GUID. Responses echo the request's id. |
| `messageType` | string | Lowercase dotted identifier, `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$`. |
| `payload` | object \| null | Required field; may be `null`. Shape depends on `messageType`. |
| `timestamp` | string | ISO 8601 with a UTC offset (e.g. `2026-01-15T10:30:00Z`). Sender clock; informational. |

Unknown top-level fields are ignored by receivers (forward compatibility).

Example request:

```json
{
  "protocolVersion": "1.0",
  "requestId": "0f8fad5b-d9cb-469f-a165-70867728950e",
  "messageType": "worker.health",
  "payload": null,
  "timestamp": "2026-01-15T10:30:00Z"
}
```

## 3. Message types

Requests are sent by the app; the worker replies to each with exactly one response.

| Request | Purpose | Provisional payload |
| --- | --- | --- |
| `worker.initialize` | Handshake. Negotiates protocol, loads configuration, reports capabilities. Must be the first request. | `{ "modelDirectory": string, "engines": ["ocr", "translation", ...] }` |
| `worker.health` | Liveness and capability check. | `null` |
| `worker.shutdown` | Graceful shutdown; worker replies then exits. | `null` |
| `ocr.detect` | Recognise text in a frame. | `{ "width", "height", "stride", "pixelFormat": "bgra32", "pixels": base64, "languages"?: [...] }` |
| `translation.translate` | Translate text. | `{ "text", "sourceLanguage": string\|null, "targetLanguage" }` |
| `translation.cancel` | Cancel an in-flight request. | `{ "targetRequestId": string }` |
| `models.list` | List installed local models. | `null` |
| `models.status` | Status of a specific model or language pair. | `{ "modelId" }` or `{ "sourceLanguage", "targetLanguage" }` |

Large frames may later move to a shared-memory section referenced from the payload instead of
base64; that would be a minor-version addition.

## 4. Request–response correlation

- Each request has a unique `requestId`. The response carries the **same** `requestId`.
- Successful responses use `messageType` = request type + `.result`
  (e.g. `translation.translate.result`).
- Failures use `messageType` = `error` (see §6).
- Responses may arrive out of order; the app matches them by `requestId` only.
- A `requestId` must not be reused while the request is in flight. Duplicates are rejected with
  `error` code `duplicate_request_id`.

## 5. Cancellation

- The app sends `translation.cancel` with `targetRequestId`. (The same mechanism is expected to be
  generalised to `ocr.detect`.)
- The worker replies to the cancel request itself with `translation.cancel.result`
  `{ "cancelled": bool }` (`false` if the target had already completed or is unknown).
- If cancelled, the **target** request receives an `error` response with code `cancelled`. Every
  request therefore still receives exactly one response.
- On the .NET side, a `CancellationToken` triggers the cancel message and the awaiting task
  completes with `OperationCanceledException`.

## 6. Error responses

```json
{
  "protocolVersion": "1.0",
  "requestId": "<id of the failed request>",
  "messageType": "error",
  "payload": { "code": "model_not_installed", "message": "No model for ja→en.", "retryable": false },
  "timestamp": "2026-01-15T10:30:01Z"
}
```

Planned error codes: `invalid_message`, `unsupported_protocol_version`, `unknown_message_type`,
`not_initialized`, `duplicate_request_id`, `cancelled`, `timeout`, `model_not_installed`,
`engine_unavailable`, `payload_too_large`, `internal_error`.

- `message` is for logs/diagnostics. It **must not contain recognised screen text or translations**.
- If a message is so malformed that its `requestId` cannot be read, the worker logs it and
  replies with `requestId` `"unknown"`; the app treats repeated invalid messages as a fatal
  protocol failure and restarts the worker.

## 7. Worker startup

1. App sets `IWorkerProcessManager.Status = Starting`, creates the pipe, launches
   `GlyphLens.Worker.exe --pipe <name>` with no console window.
2. Worker connects within the **startup timeout (default 30 s)**; otherwise → `Error`.
3. App sends `worker.initialize`. The worker validates the protocol version and replies with
   `worker.initialize.result` `{ "workerVersion", "protocolVersion", "engines": { name: status } }`.
4. If required engines/models are missing → `Unavailable` (worker stays connected for
   `models.*` queries); otherwise → `Ready`.
5. Any request other than `worker.initialize` before initialization gets `error` `not_initialized`.

## 8. Worker shutdown

1. App sends `worker.shutdown`; worker stops accepting new requests, cancels in-flight work,
   replies `worker.shutdown.result`, closes the pipe and exits with code 0.
2. If the process has not exited within the **shutdown grace period (default 5 s)**, the app
   terminates it.
3. If the app exits or the pipe breaks, the worker exits on its own (it never outlives the app).
   The app also places the worker in a Windows Job Object with kill-on-close.

## 9. Timeouts

| Operation | Default (provisional) |
| --- | --- |
| Worker connect + `worker.initialize` | 30 s (model loading may be slow on first start) |
| `worker.health` | 2 s |
| `ocr.detect` | 10 s |
| `translation.translate` | 10 s |
| `worker.shutdown` grace | 5 s |

On timeout the app cancels the request (§5) and completes it with a timeout error. Repeated
health-check timeouts mark the worker `Error` and trigger a restart.

## 10. Process crashes

- The app monitors the worker process handle and the pipe. On unexpected exit or pipe break:
  all in-flight requests fail with a "worker unavailable" error, status becomes `Error`.
- Automatic restart with exponential backoff (e.g. 1 s, 2 s, 4 s … max 60 s); after 5 consecutive
  failures within 5 minutes, stop restarting and show the user a clear error.
- Crash diagnostics are logged locally without screen content.

## 11. Version compatibility

- Peers are **compatible when the major versions match.** Minor versions only add optional
  payload fields or new message types.
- Receivers ignore unknown fields. A receiver that gets an unknown `messageType` replies
  `error` `unknown_message_type` rather than failing.
- Incompatible major version → `error` `unsupported_protocol_version` during `worker.initialize`;
  the app reports the worker as `Unavailable` (likely a mismatched install).
- Breaking changes require a major version bump, an update to this document and an ADR.
- The authoritative versions are `ProtocolVersion.Current` (.NET) and `PROTOCOL_VERSION` (Python).

## 12. Security constraints

- Local pipe only, current-user ACL, remote clients rejected, single client, random name.
- The protocol has **no** message that executes code, loads arbitrary files outside the model
  directory, or opens network connections. New message types must preserve this.
- All input is validated (envelope rules above, size limit, payload schema) before dispatch.
- Neither side logs payload contents (screen text, pixels, translations).
