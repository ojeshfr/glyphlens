"""IPC message envelope validation (protocol 1.x).

Mirrors ``GlyphLens.Infrastructure.IPC.WorkerMessageSerializer`` on the .NET side.
See ``docs/IPC-PROTOCOL.md``. This module validates message shape only; it does not
implement any transport.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION

MAX_MESSAGE_BYTES = 16 * 1024 * 1024
MAX_REQUEST_ID_LENGTH = 128

REQUEST_TYPES: frozenset[str] = frozenset(
    {
        "worker.initialize",
        "worker.health",
        "worker.shutdown",
        "ocr.detect",
        "translation.translate",
        "translation.cancel",
        "models.list",
        "models.status",
        "models.install",
        "language.detect",
    }
)

_VERSION_PATTERN = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
_MESSAGE_TYPE_PATTERN = re.compile(r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$")
_REQUIRED_FIELDS = ("protocolVersion", "requestId", "messageType", "payload", "timestamp")


class ProtocolError(ValueError):
    """Raised when a message does not conform to the GlyphLens worker protocol."""


class UnsupportedVersionError(ProtocolError):
    """The message's major protocol version is not the one this worker speaks."""


@dataclass(frozen=True)
class Envelope:
    protocol_version: str
    request_id: str
    message_type: str
    payload: Any
    timestamp: datetime


def parse_version(value: object) -> tuple[int, int]:
    """Parse a ``MAJOR.MINOR`` protocol version string."""
    if not isinstance(value, str) or not _VERSION_PATTERN.match(value):
        raise ProtocolError(f"invalid protocolVersion {value!r}")
    major, minor = (int(part) for part in value.split("."))
    if major == 0:
        raise ProtocolError(f"invalid protocolVersion {value!r}")
    return major, minor


def is_compatible(version: str, current: str = PROTOCOL_VERSION) -> bool:
    """Peers are compatible when their major versions match."""
    return parse_version(version)[0] == parse_version(current)[0]


def validate(message: object) -> Envelope:
    """Validate a decoded JSON object and return it as an :class:`Envelope`."""
    if not isinstance(message, dict):
        raise ProtocolError("message must be a JSON object")

    missing = [name for name in _REQUIRED_FIELDS if name not in message]
    if missing:
        raise ProtocolError(f"missing required field(s): {', '.join(missing)}")

    version = message["protocolVersion"]
    if not is_compatible(version):
        raise UnsupportedVersionError(
            f"unsupported protocolVersion {version!r}; this worker speaks {PROTOCOL_VERSION}"
        )

    request_id = message["requestId"]
    if (
        not isinstance(request_id, str)
        or not request_id.strip()
        or len(request_id) > MAX_REQUEST_ID_LENGTH
    ):
        raise ProtocolError(f"requestId must be 1-{MAX_REQUEST_ID_LENGTH} non-blank characters")

    message_type = message["messageType"]
    if not isinstance(message_type, str) or not _MESSAGE_TYPE_PATTERN.match(message_type):
        raise ProtocolError(f"invalid messageType {message_type!r}")

    raw_timestamp = message["timestamp"]
    if not isinstance(raw_timestamp, str):
        raise ProtocolError("timestamp must be an ISO 8601 string")
    try:
        timestamp = datetime.fromisoformat(raw_timestamp)
    except ValueError as exc:
        raise ProtocolError(f"invalid timestamp {raw_timestamp!r}") from exc
    if timestamp.tzinfo is None:
        raise ProtocolError("timestamp must include a UTC offset")

    return Envelope(
        protocol_version=version,
        request_id=request_id,
        message_type=message_type,
        payload=message["payload"],
        timestamp=timestamp,
    )


def _timestamp() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def encode(message_type: str, request_id: str, payload: Any) -> bytes:
    """Encode one envelope as compact UTF-8 JSON (camelCase field names, as the .NET side expects)."""
    message = {
        "protocolVersion": PROTOCOL_VERSION,
        "requestId": request_id,
        "messageType": message_type,
        "payload": payload,
        "timestamp": _timestamp(),
    }
    return json.dumps(message, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def encode_error(request_id: str, code: str, message: str, retryable: bool = False) -> bytes:
    """Encode an ``error`` response. ``message`` must never contain message content."""
    return encode("error", request_id, {"code": code, "message": message, "retryable": retryable})


def extract_request_id(data: bytes) -> str:
    """Best-effort ``requestId`` of a message that failed validation, so the error can still be correlated."""
    try:
        message = json.loads(data.decode("utf-8"))
        request_id = message.get("requestId") if isinstance(message, dict) else None
    except (UnicodeDecodeError, json.JSONDecodeError, AttributeError):
        return "unknown"
    if isinstance(request_id, str) and request_id.strip() and len(request_id) <= MAX_REQUEST_ID_LENGTH:
        return request_id
    return "unknown"


def decode(data: bytes) -> Envelope:
    """Decode and validate one UTF-8 JSON message."""
    if len(data) > MAX_MESSAGE_BYTES:
        raise ProtocolError(f"message exceeds the {MAX_MESSAGE_BYTES}-byte limit")
    try:
        message = json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise ProtocolError("message is not valid UTF-8 JSON") from exc
    return validate(message)
