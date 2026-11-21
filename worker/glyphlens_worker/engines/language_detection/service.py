"""The ``language.detect`` service.

It validates the request and identifies each text. Chinese, Japanese and Korean (and, without a statistical library, plainly
English text) are decided from the characters themselves (:mod:`.script`, no dependency): this is what the Chinese-first
release relies on. Everything else goes to the first usable detector library, loaded lazily and kept. The confidence is a
library's own probability or ``null`` (a rule has none); it is never invented, and there is no "uncertain" threshold: the
application decides what is confident enough and lets the user override the language.
"""

from __future__ import annotations

import importlib.metadata
import importlib.util
import math
from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

from glyphlens_worker.engines.language_detection.backends import DEFAULT_BACKENDS, BackendSpec, DetectorBackend
from glyphlens_worker.engines.language_detection.script import has_cjk, identify_script
from glyphlens_worker.engines.payloads import (
    MAX_DETECT_TEXTS,
    MAX_TEXT_LENGTH,
    invalid_request,
    is_valid_text,
    require_list,
    require_object,
)
from glyphlens_worker.errors import ServiceError
from glyphlens_worker.languages import LANGUAGE_CODE_PATTERN

MAX_ALTERNATIVES = 5
DEFAULT_ALTERNATIVES = 3

#: Fewer letters than this cannot be identified.
MIN_LETTERS = 3


@dataclass(frozen=True)
class _Loaded:
    backend: DetectorBackend | None  # the statistical library, when one is installed
    description: str  # "script", or "script+lingua 2.2.0"


class LanguageDetectionService:
    def __init__(self, backends: Sequence[BackendSpec] = DEFAULT_BACKENDS) -> None:
        self._specs = tuple(backends)
        self._loaded: _Loaded | None = None
        self._failed: set[str] = set()
        self._answered = False

    def state(self) -> str:
        """``installed`` (script rules always work; not asked yet) or ``ready`` (has answered). Imports nothing."""
        return "ready" if self._answered else "installed"

    def detect(self, payload: Any) -> dict[str, Any]:
        body = require_object(payload, "language detection")
        texts = require_list(body, "texts", MAX_DETECT_TEXTS)
        for index, text in enumerate(texts):
            if not is_valid_text(text) or len(text) > MAX_TEXT_LENGTH:
                raise invalid_request(f"texts[{index}] must be a valid string of at most {MAX_TEXT_LENGTH} characters.")
        max_alternatives = _read_max_alternatives(body.get("maxAlternatives"))

        loaded = self._load()
        results = [_identify(loaded.backend, text, max_alternatives) for text in texts]
        self._answered = True
        return {"detector": loaded.description, "results": results}

    # ---- loading -----------------------------------------------------------------------------------------------------

    def _candidates(self) -> list[BackendSpec]:
        return [spec for spec in self._specs if spec.name not in self._failed and spec.usable_here() and _is_installed(spec.module)]

    def _load(self) -> _Loaded:
        if self._loaded is not None:
            return self._loaded
        for spec in self._candidates():
            try:
                backend = spec.load()
            except Exception:  # noqa: BLE001 - a third-party load can fail in many ways; remember it, try the next library
                self._failed.add(spec.name)
                continue
            self._loaded = _Loaded(backend, f"script+{spec.name} {_version(spec.distribution)}")
            return self._loaded
        self._loaded = _Loaded(None, "script")
        return self._loaded


# ---- helpers ------------------------------------------------------------------------------------------------------------


def _is_installed(module: str) -> bool:
    try:
        return importlib.util.find_spec(module) is not None
    except (ImportError, ValueError):
        return False


def _version(distribution: str) -> str:
    try:
        return importlib.metadata.version(distribution)
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def _read_max_alternatives(value: Any) -> int:
    if value is None:
        return DEFAULT_ALTERNATIVES
    if isinstance(value, bool) or not isinstance(value, int) or not 0 <= value <= MAX_ALTERNATIVES:
        raise invalid_request(f"'maxAlternatives' must be an integer from 0 to {MAX_ALTERNATIVES}.")
    return value


def _probability(value: float | None) -> float | None:
    """The library's probability, as a number in [0, 1], or ``None``. Only rounding noise is corrected."""
    if value is None or math.isnan(value):
        return None
    return min(1.0, max(0.0, value))


def _undetermined(issue: str) -> dict[str, Any]:
    return {"language": None, "confidence": None, "alternatives": [], "issue": issue}


def _identify(backend: DetectorBackend | None, text: str, max_alternatives: int) -> dict[str, Any]:
    letters = sum(1 for character in text if character.isalpha())
    if letters == 0:
        return _undetermined("no_text")
    # One CJK character is a whole word ("好", "谢谢"), unlike one Latin letter.
    if letters < MIN_LETTERS and not has_cjk(text):
        return _undetermined("too_short")

    by_rule = identify_script(text, allow_english=backend is None)
    if by_rule is not None:
        return {"language": by_rule, "confidence": None, "alternatives": [], "issue": "none"}
    if backend is None:
        return _undetermined("unknown")

    candidates = backend.identify(text, max_alternatives + 1)
    if not candidates or not LANGUAGE_CODE_PATTERN.match(candidates[0][0]):
        return _undetermined("unknown")

    language, probability = candidates[0]
    alternatives = []
    for code, other in candidates[1 : 1 + max_alternatives]:
        confidence = _probability(other)
        # The protocol's alternatives carry a probability; a candidate without one cannot be reported.
        if confidence is not None and confidence > 0.0 and LANGUAGE_CODE_PATTERN.match(code):
            alternatives.append({"language": code, "confidence": confidence})
    return {"language": language, "confidence": _probability(probability), "alternatives": alternatives, "issue": "none"}
