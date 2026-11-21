"""Shared limits and request-shape checks for the language and translation messages (docs/IPC-PROTOCOL.md).

Every violation is an ``invalid_request`` :class:`ServiceError` whose message names the field, never its value: values
are private screen content.
"""

from __future__ import annotations

from typing import Any, TypeGuard

from glyphlens_worker.errors import ServiceError
from glyphlens_worker.languages import LANGUAGE_CODE_PATTERN

#: Characters per text (Python code points) for both ``language.detect`` and ``translation.translate``.
MAX_TEXT_LENGTH = 4096
MAX_DETECT_TEXTS = 64
MAX_TRANSLATE_ITEMS = 32
MAX_ITEM_ID_LENGTH = 64


def invalid_request(message: str) -> ServiceError:
    return ServiceError("invalid_request", message)


def is_valid_text(value: object) -> TypeGuard[str]:
    """Whether ``value`` is a string the engines can process: a lone surrogate (invalid Unicode) cannot be encoded and would fail a whole request."""
    if not isinstance(value, str):
        return False
    try:
        value.encode("utf-8")
    except UnicodeEncodeError:
        return False
    return True


def require_object(payload: Any, what: str) -> dict[str, Any]:
    if not isinstance(payload, dict):
        raise invalid_request(f"The {what} payload must be an object.")
    return payload


def require_language_code(value: Any, field: str) -> str:
    """A language code as the wire allows it (``de``, ``pt-BR``)."""
    if not isinstance(value, str) or not LANGUAGE_CODE_PATTERN.match(value):
        raise invalid_request(f"'{field}' must be a language code such as 'en' or 'pt-BR'.")
    return value


def require_list(payload: dict[str, Any], key: str, maximum: int) -> list[Any]:
    value = payload.get(key)
    if not isinstance(value, list):
        raise invalid_request(f"'{key}' must be an array.")
    if len(value) > maximum:
        raise invalid_request(f"'{key}' has more than {maximum} entries.")
    return value
