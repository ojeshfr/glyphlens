"""The language identification libraries the worker can use, behind one small interface.

* **Lingua** (``lingua-language-detector`` 2.2.0, Python 3.12+): the most accurate on short text and the only one with
  a sound probability. The detector is built over *every* Argos language (see
  :func:`glyphlens_worker.languages.detection_languages`), never only over the installed models: restricted to what is
  installed, a Spanish line would be named French with high confidence. Lingua cannot recognise Galician and Kyrgyz.
* **py3langid** (0.4.0): the Python 3.11 fallback (Lingua is far heavier there), restricted to the same languages with
  normalised probabilities.

The libraries are imported only when the first request needs one.
"""

from __future__ import annotations

import sys
from collections.abc import Callable
from dataclasses import dataclass
from typing import Any, Protocol

from glyphlens_worker.languages import detection_languages

LINGUA_REQUIREMENT = "lingua-language-detector==2.2.0"
PY3LANGID_REQUIREMENT = "py3langid==0.4.0"


class DetectorBackend(Protocol):
    def identify(self, text: str, limit: int) -> list[tuple[str, float | None]]:
        """Up to ``limit`` candidate languages, best first, as (code, probability). The probability is ``None`` when the library reports none."""


@dataclass(frozen=True)
class BackendSpec:
    """A detector library: how to recognise that it is installed and how to load it."""

    name: str
    distribution: str  # the pip distribution name (for the version)
    module: str  # the import name (for the presence check, which must not import it)
    min_python: tuple[int, int]
    requirement: str  # what to install, for the message
    load: Callable[[], DetectorBackend]

    def usable_here(self) -> bool:
        return sys.version_info >= self.min_python


class LinguaBackend:
    def __init__(self, detector: Any) -> None:
        self._detector = detector

    @classmethod
    def load(cls) -> LinguaBackend:
        from lingua import IsoCode639_1, Language, LanguageDetectorBuilder

        languages = []
        for code in detection_languages():
            iso_code = getattr(IsoCode639_1, code.upper(), None)
            if iso_code is not None:  # Lingua does not know every Argos language
                languages.append(Language.from_iso_code_639_1(iso_code))
        return cls(LanguageDetectorBuilder.from_languages(*languages).build())

    def identify(self, text: str, limit: int) -> list[tuple[str, float | None]]:
        values = self._detector.compute_language_confidence_values(text)
        return [(value.language.iso_code_639_1.name.lower(), value.value) for value in values[:limit]]


class Py3LangidBackend:
    def __init__(self, identifier: Any) -> None:
        self._identifier = identifier

    @classmethod
    def load(cls) -> Py3LangidBackend:
        from py3langid import langid

        identifier = langid.LanguageIdentifier.from_modelpath(langid.MODEL_DIR / langid.MODEL_FILE, norm_probs=True)
        known = set(identifier.nb_classes)
        identifier.set_languages([code for code in detection_languages() if code in known])
        return cls(identifier)

    def identify(self, text: str, limit: int) -> list[tuple[str, float | None]]:
        return [(language, float(probability)) for language, probability in self._identifier.rank(text)[:limit]]


#: In order of preference.
DEFAULT_BACKENDS: tuple[BackendSpec, ...] = (
    BackendSpec("lingua", "lingua-language-detector", "lingua", (3, 12), f"{LINGUA_REQUIREMENT} (Python 3.12 or newer)", LinguaBackend.load),
    BackendSpec("py3langid", "py3langid", "py3langid", (3, 11), PY3LANGID_REQUIREMENT, Py3LangidBackend.load),
)
