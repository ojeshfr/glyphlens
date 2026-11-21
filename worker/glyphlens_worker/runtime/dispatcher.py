"""Request dispatch for the worker.

Implemented: ``worker.initialize``, ``worker.health``, ``worker.shutdown``, ``ocr.detect``, ``language.detect``,
``translation.translate`` and ``models.list`` / ``models.status`` / ``models.install``.
Known but not implemented in this build (``translation.cancel``): answered with the
``engine_unavailable`` error, never with an invented result. Anything else: ``unknown_message_type``.
"""

from __future__ import annotations

import platform
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__
from glyphlens_worker.errors import ServiceError
from glyphlens_worker.protocols.envelope import REQUEST_TYPES, Envelope, is_compatible
from glyphlens_worker.runtime.health import health_payload
from glyphlens_worker.runtime.services import Services, create_services


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

    def __init__(self, session_token: str, services: Services | None = None) -> None:
        self._token = session_token
        self._services = services if services is not None else create_services()
        self.initialized = False
        # Handlers of the translation messages: payload in, result payload out, ServiceError for a failure.
        self._service_handlers: dict[str, Callable[[Any], dict[str, Any]]] = {
            "ocr.detect": self._services.ocr.detect,
            "language.detect": self._services.detection.detect,
            "translation.translate": self._services.translation.translate,
            "models.list": self._services.translation.list_models,
            "models.status": self._services.translation.route_status,
            "models.install": self._services.translation.install,
        }

    def dispatch(self, request: Envelope) -> Reply:
        message_type = request.message_type

        if message_type == "worker.initialize":
            return self._initialize(request.payload)

        if message_type not in REQUEST_TYPES:
            raise HandlerError("unknown_message_type", f"unknown message type '{message_type}'")

        if not self.initialized:
            raise HandlerError("not_initialized", "worker.initialize must be the first request")

        if message_type == "worker.health":
            return Reply(health_payload(ipc="connected", protocol="initialized", states=self._services.component_states()))

        if message_type == "worker.shutdown":
            return Reply({"status": "stopping"}, shutdown=True)

        handler = self._service_handlers.get(message_type)
        if handler is None:
            raise HandlerError("engine_unavailable", f"'{message_type}' is not implemented in this build")
        try:
            return Reply(handler(request.payload))
        except ServiceError as exc:
            raise HandlerError(exc.code, exc.message) from exc

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
                "engines": self._services.component_states().engines(),
            }
        )
