"""Parsing and validating the payloads of ``translation.translate``, ``models.status`` and ``models.list``."""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from glyphlens_worker.engines.payloads import (
    MAX_ITEM_ID_LENGTH,
    MAX_TRANSLATE_ITEMS,
    invalid_request,
    is_valid_text,
    require_language_code,
    require_list,
    require_object,
)
from glyphlens_worker.languages import canonical_language


@dataclass(frozen=True)
class TranslateItem:
    id: str
    text: str
    source: str  # Argos code


@dataclass(frozen=True)
class TranslateRequest:
    target: str  # Argos code
    items: tuple[TranslateItem, ...]


def parse_translate_request(payload: Any) -> TranslateRequest:
    """Validate a ``translation.translate`` payload. Item texts are not length-checked here: that is a per-item error."""
    body = require_object(payload, "translation")
    target = canonical_language(require_language_code(body.get("targetLanguage"), "targetLanguage"))

    items: list[TranslateItem] = []
    seen: set[str] = set()
    for index, raw in enumerate(require_list(body, "items", MAX_TRANSLATE_ITEMS)):
        if not isinstance(raw, dict):
            raise invalid_request(f"items[{index}] must be an object.")
        item_id = raw.get("id")
        if not isinstance(item_id, str) or not 1 <= len(item_id) <= MAX_ITEM_ID_LENGTH:
            raise invalid_request(f"items[{index}].id must be a string of 1 to {MAX_ITEM_ID_LENGTH} characters.")
        if item_id in seen:
            raise invalid_request(f"items[{index}].id is used twice; ids must be unique.")
        seen.add(item_id)
        text = raw.get("text")
        if not is_valid_text(text):
            raise invalid_request(f"items[{index}].text must be a valid string.")
        source = canonical_language(require_language_code(raw.get("sourceLanguage"), f"items[{index}].sourceLanguage"))
        items.append(TranslateItem(item_id, text, source))
    return TranslateRequest(target, tuple(items))


def parse_route_request(payload: Any) -> tuple[str, str]:
    """Validate a ``models.status`` payload and return the (source, target) Argos codes."""
    body = require_object(payload, "route status")
    source = canonical_language(require_language_code(body.get("source"), "source"))
    target = canonical_language(require_language_code(body.get("target"), "target"))
    return source, target


def require_empty_payload(payload: Any) -> None:
    """``models.list`` takes no parameters: the payload is ``null`` or an object (extra fields are ignored)."""
    if payload is not None and not isinstance(payload, dict):
        raise invalid_request("The payload must be null or an object.")
