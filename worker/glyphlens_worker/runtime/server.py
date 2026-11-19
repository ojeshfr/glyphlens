"""The worker's request loop over an established pipe.

Behaviour (see docs/IPC-PROTOCOL.md):

* One request at a time; each gets exactly one response with the same ``requestId``.
* A well-framed message that fails validation gets an ``error`` response and the loop continues; too many
  in a row (``MAX_CONSECUTIVE_INVALID``) is fatal.
* A framing violation (zero/oversized length, truncated frame) cannot be recovered from: best-effort error,
  then exit with ``EXIT_PROTOCOL_ERROR``.
* End of stream without ``worker.shutdown`` means the application is gone: exit with ``EXIT_PIPE_LOST``.
* Logging records request id, type, outcome and duration only. Payloads are never logged.
"""

from __future__ import annotations

import sys
import time
from collections.abc import Callable
from typing import BinaryIO

from glyphlens_worker.protocols.envelope import (
    ProtocolError,
    UnsupportedVersionError,
    decode,
    encode,
    encode_error,
    extract_request_id,
)
from glyphlens_worker.runtime.dispatcher import Dispatcher, HandlerError
from glyphlens_worker.runtime.framing import FramingError, read_frame, write_frame

EXIT_OK = 0
EXIT_USAGE = 2
EXIT_CONNECT_FAILED = 3
EXIT_PROTOCOL_ERROR = 4
EXIT_PIPE_LOST = 5
EXIT_NO_TOKEN = 6

MAX_CONSECUTIVE_INVALID = 5

LogFunction = Callable[[str], None]


def stderr_log(line: str) -> None:
    """Write one diagnostic line to stderr, which the application captures (sanitised) for its diagnostics."""
    try:
        print(f"glyphlens-worker: {line}", file=sys.stderr, flush=True)
    except (OSError, ValueError):
        pass  # a closed stderr must never take the worker down


def serve(stream: BinaryIO, dispatcher: Dispatcher, log: LogFunction = stderr_log) -> int:
    """Serve requests until shutdown, disconnect or a fatal protocol error. Returns the process exit code."""
    consecutive_invalid = 0

    while True:
        try:
            raw = read_frame(stream)
        except FramingError as exc:
            log(f"fatal framing error: {exc}")
            _send_best_effort(stream, encode_error("unknown", "payload_too_large" if "exceeds" in str(exc) else "invalid_message", str(exc)))
            return EXIT_PROTOCOL_ERROR
        except (OSError, ValueError):
            log("pipe lost while reading")
            return EXIT_PIPE_LOST

        if raw is None:
            log("application closed the pipe without worker.shutdown")
            return EXIT_PIPE_LOST

        started = time.perf_counter()
        try:
            request = decode(raw)
        except ProtocolError as exc:
            consecutive_invalid += 1
            code = "unsupported_protocol_version" if isinstance(exc, UnsupportedVersionError) else "invalid_message"
            request_id = extract_request_id(raw)
            log(f"req={request_id} status=error:{code} invalid message ({consecutive_invalid}/{MAX_CONSECUTIVE_INVALID})")
            if not _send(stream, encode_error(request_id, code, str(exc))):
                return EXIT_PIPE_LOST
            if consecutive_invalid >= MAX_CONSECUTIVE_INVALID:
                log("too many invalid messages in a row")
                return EXIT_PROTOCOL_ERROR
            continue

        consecutive_invalid = 0
        shutdown = False
        try:
            reply = dispatcher.dispatch(request)
            response = encode(request.message_type + ".result", request.request_id, reply.payload)
            status = "ok"
            shutdown = reply.shutdown
        except HandlerError as exc:
            response = encode_error(request.request_id, exc.code, exc.message)
            status = f"error:{exc.code}"
        except Exception as exc:  # noqa: BLE001 - a handler bug must not kill the worker; report the type only
            response = encode_error(request.request_id, "internal_error", "internal error")
            status = f"error:internal_error({type(exc).__name__})"

        if not _send(stream, response):
            return EXIT_PIPE_LOST

        elapsed_ms = (time.perf_counter() - started) * 1000.0
        log(f"req={request.request_id} type={request.message_type} status={status} ms={elapsed_ms:.1f}")
        if shutdown:
            return EXIT_OK


def _send(stream: BinaryIO, payload: bytes) -> bool:
    try:
        write_frame(stream, payload)
        return True
    except (OSError, ValueError, FramingError):
        return False


def _send_best_effort(stream: BinaryIO, payload: bytes) -> None:
    _send(stream, payload)
