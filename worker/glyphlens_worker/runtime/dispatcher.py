"""Request dispatch for the worker.

Implemented: ``worker.initialize``, ``worker.health``, ``worker.shutdown``.
Known but not implemented in this build (``ocr.detect``, ``translation.*``, ``models.*``): answered with the
``engine_unavailable`` error, never with an invented result. Anything else: ``unknown_message_type``.
"""

from __future__ import annotations

import platform
from dataclasses import dataclass
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__
from glyphlens_worker.protocols.envelope import REQUEST_TYPES, Envelope, is_compatible
from glyphlens_worker.runtime.health import health_payload


class HandlerError(Exception):
    """A request failed; becomes an ``error`` response with this code."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


@dataclass(frozen=True)
class Reply:
    payload: dict[str, Any] | None
    shutdown: bool = False


class Dispatcher:
    """Routes decoded requests to handlers and enforces the initialize-first rule."""

    def __init__(self, session_token: str) -> None:
        self._token = session_token
        self.initialized = False

    def dispatch(self, request: Envelope) -> Reply:
        message_type = request.message_type

        if message_type == "worker.initialize":
            return self._initialize(request.payload)

        if message_type not in REQUEST_TYPES:
            raise HandlerError("unknown_message_type", f"unknown message type '{message_type}'")

        if not self.initialized:
            raise HandlerError("not_initialized", "worker.initialize must be the first request")

        if message_type == "worker.health":
            return Reply(health_payload(ipc="connected", protocol="initialized"))

        if message_type == "worker.shutdown":
            return Reply({"status": "stopping"}, shutdown=True)

        raise HandlerError("engine_unavailable", f"'{message_type}' is not implemented in this build")

    def _initialize(self, payload: Any) -> Reply:
        if not isinstance(payload, dict) or not isinstance(payload.get("clientProtocolVersion"), str):
            raise HandlerError("invalid_message", "worker.initialize requires clientProtocolVersion")

        client_version = payload["clientProtocolVersion"]
        try:
            compatible = is_compatible(client_version)
        except ValueError as exc:
            raise HandlerError("invalid_message", "clientProtocolVersion is not valid") from exc
        if not compatible:
            raise HandlerError(
                "unsupported_protocol_version",
                f"this worker speaks protocol {PROTOCOL_VERSION}, not {client_version}",
            )

        self.initialized = True
        return Reply(
            {
                "workerVersion": __version__,
                "protocolVersion": PROTOCOL_VERSION,
                "python": platform.python_version(),
                # Echoing the per-session token proves to the application that this client is the process it launched.
                "sessionToken": self._token,
                "engines": {
                    "ocr": "not_implemented",
                    "languageDetection": "not_implemented",
                    "translation": "not_implemented",
                },
            }
        )
