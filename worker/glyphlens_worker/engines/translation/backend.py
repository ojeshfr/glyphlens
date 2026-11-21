"""The translation runtime: CTranslate2 and SentencePiece, driven directly (``argostranslate`` is never imported).

Everything the service needs from the native libraries sits behind two small interfaces, so the routing, batching and
error handling can be tested with fakes:

* :class:`TranslationRuntime` - the loaded libraries: their version, and a way to load one package;
* :class:`SegmentTranslator` - one loaded package: count tokens, translate a batch of already split segments.

The libraries are imported lazily, the first time a request needs them, never when the worker starts or reports health.
"""

from __future__ import annotations

import importlib.util
import re
from collections.abc import Sequence
from pathlib import Path
from typing import Any, Protocol

#: Exactly what to install when the runtime is missing (also stated in the README).
RUNTIME_REQUIREMENTS = "ctranslate2==4.9.0 sentencepiece==0.2.3"

_RUNTIME_MODULES = ("ctranslate2", "sentencepiece")

#: Decoding parameters of Argos' ``apply_packaged_translation``.
BEAM_SIZE = 4
LENGTH_PENALTY = 0.2
MAX_BATCH_SIZE = 32

#: SentencePiece's word-boundary marker, U+2581 LOWER ONE EIGHTH BLOCK. ``decode_pieces`` converts it; a stray one is replaced.
SENTENCEPIECE_SPACE = chr(0x2581)


class RuntimeUnavailableError(Exception):
    """The translation runtime is missing or failed to load. The message is safe to send to the application."""


class SegmentTranslator(Protocol):
    """One loaded translation package."""

    def count_tokens(self, text: str) -> int:
        """The number of model tokens ``text`` becomes."""

    def translate(self, segments: Sequence[str]) -> list[str]:
        """Translate ``segments`` in one batched call; one translation per segment, in order."""


class TranslationRuntime(Protocol):
    """The loaded translation libraries."""

    @property
    def version(self) -> str:
        """The CTranslate2 version, for the engine tag (``ct2-<version>``)."""

    def load(self, package_directory: Path, target_prefix: str) -> SegmentTranslator:
        """Load the package in ``package_directory`` (``model/`` and ``sentencepiece.model``)."""


class RuntimeLoader(Protocol):
    def is_present(self) -> bool:
        """Cheap check that the libraries are installed. Must not import them."""

    def load(self) -> TranslationRuntime:
        """Import the libraries. Raises :class:`RuntimeUnavailableError`."""


class Ct2Translator:
    """A CTranslate2 translator plus the SentencePiece model of one package."""

    def __init__(self, translator: Any, processor: Any, target_prefix: str) -> None:
        self._translator = translator
        self._processor = processor
        self._target_prefix = target_prefix

    def count_tokens(self, text: str) -> int:
        return len(self._processor.encode(text, out_type=str))

    def translate(self, segments: Sequence[str]) -> list[str]:
        if not segments:
            return []
        tokens = [self._processor.encode(segment, out_type=str) for segment in segments]
        prefix = self._target_prefix
        results = self._translator.translate_batch(
            tokens,
            target_prefix=[[prefix]] * len(tokens) if prefix else None,
            beam_size=BEAM_SIZE,
            num_hypotheses=1,
            length_penalty=LENGTH_PENALTY,
            max_batch_size=MAX_BATCH_SIZE,
            batch_type="tokens",
            replace_unknowns=True,
        )
        return [self._decode(result.hypotheses[0]) for result in results]

    def _decode(self, pieces: list[str]) -> str:
        text: str = self._processor.decode_pieces(pieces).replace(SENTENCEPIECE_SPACE, " ").strip()
        prefix = self._target_prefix
        if prefix and text.startswith(prefix):
            text = text[len(prefix) :].lstrip()
        return _tidy_contractions(text)


class Ct2Runtime:
    def __init__(self, ctranslate2: Any, sentencepiece: Any) -> None:
        self._ctranslate2 = ctranslate2
        self._sentencepiece = sentencepiece

    @property
    def version(self) -> str:
        return str(self._ctranslate2.__version__)

    def load(self, package_directory: Path, target_prefix: str) -> Ct2Translator:
        processor = self._sentencepiece.SentencePieceProcessor(model_file=str(package_directory / "sentencepiece.model"))
        translator = self._ctranslate2.Translator(
            str(package_directory / "model"),
            device="cpu",  # never rely on the default: a CUDA device may be present
            compute_type="auto",
            inter_threads=1,
            intra_threads=0,
        )
        return Ct2Translator(translator, processor, target_prefix)


class Ct2RuntimeLoader:
    """Finds and imports ``ctranslate2`` and ``sentencepiece``."""

    def is_present(self) -> bool:
        return not self._missing()

    def load(self) -> Ct2Runtime:
        missing = self._missing()
        if missing:
            raise RuntimeUnavailableError(
                f"The translation runtime is not installed (missing: {', '.join(missing)}). "
                f"Install it with: pip install {RUNTIME_REQUIREMENTS}"
            )
        try:
            import ctranslate2
            import sentencepiece
        except Exception as exc:  # noqa: BLE001 - a native library can fail to load in many ways; report the type only
            raise RuntimeUnavailableError(
                f"The translation runtime failed to load ({type(exc).__name__}). "
                f"Reinstall it with: pip install --force-reinstall {RUNTIME_REQUIREMENTS}"
            ) from exc
        return Ct2Runtime(ctranslate2, sentencepiece)

    @staticmethod
    def _missing() -> list[str]:
        return [name for name in _RUNTIME_MODULES if _find_spec(name) is None]


def _find_spec(name: str) -> object | None:
    try:
        return importlib.util.find_spec(name)
    except (ImportError, ValueError):
        return None


# Some models (the Chinese ones) emit Moses-style tokens, so a contraction comes out as "Tomorrow ' s". Rejoin only the
# unambiguous English contraction endings; everything else in the model's output is left exactly as produced.
_CONTRACTION = re.compile(r"(\w) ?' ?(s|t|re|ve|ll|d|m)(?=\W|$)")


def _tidy_contractions(text: str) -> str:
    return _CONTRACTION.sub(r"\1'\2", text) if " '" in text or "' " in text else text
