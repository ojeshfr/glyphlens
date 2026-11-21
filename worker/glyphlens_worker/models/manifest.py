"""The manifest GlyphLens writes next to every installed package (``glyphlens-model.json``).

It records what the importer validated, including the SHA-256 of the archive and of ``model.bin``. The hashes are
computed once, while importing, so listing models never reads an 80 MB file. The model hash feeds the model version
string that keys the application's translation cache, so re-importing a different file changes it.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from typing import Any

from glyphlens_worker.languages import ARGOS_LANGUAGES, package_name

MANIFEST_FILE_NAME = "glyphlens-model.json"
MANIFEST_SCHEMA = 1
MAX_MANIFEST_BYTES = 64 * 1024

#: Codes of an installed package: two or three lowercase letters (the importer maps ``zh-Hant`` to ``zt``, ``pt-BR`` to ``pb``).
PACKAGE_CODE_PATTERN = re.compile(r"^[a-z]{2,3}$")
PACKAGE_VERSION_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._+-]{0,31}$")
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")

MAX_NAME_LENGTH = 64
MAX_TARGET_PREFIX_LENGTH = 32

SENTENCEPIECE_TOKENIZER = "sentencepiece"


class ManifestError(ValueError):
    """The manifest is missing a field or holds an invalid value (the message names the field, never a value)."""


def clean_name(value: object, fallback: str) -> str:
    """A display name from package metadata: bounded, without control characters, or ``fallback`` when unusable."""
    if not isinstance(value, str):
        return fallback
    cleaned = "".join(" " if ch.isspace() or not ch.isprintable() else ch for ch in value).strip()[:MAX_NAME_LENGTH].strip()
    return cleaned or fallback


def default_language_name(code: str) -> str:
    return ARGOS_LANGUAGES.get(code, code)


@dataclass(frozen=True)
class Manifest:
    source: str
    target: str
    source_name: str
    target_name: str
    package_version: str
    target_prefix: str
    archive_sha256: str
    archive_bytes: int
    model_sha256: str
    model_bytes: int
    imported_at: str

    @property
    def package_name(self) -> str:
        return package_name(self.source, self.target)

    @property
    def directory_name(self) -> str:
        return f"{self.source}_{self.target}"

    def to_json(self) -> bytes:
        document = {
            "schema": MANIFEST_SCHEMA,
            "from": self.source,
            "to": self.target,
            "fromName": self.source_name,
            "toName": self.target_name,
            "packageVersion": self.package_version,
            "tokenizer": SENTENCEPIECE_TOKENIZER,
            "targetPrefix": self.target_prefix,
            "archiveSha256": self.archive_sha256,
            "archiveBytes": self.archive_bytes,
            "modelSha256": self.model_sha256,
            "modelBytes": self.model_bytes,
            "importedAt": self.imported_at,
        }
        return (json.dumps(document, indent=2, ensure_ascii=False) + "\n").encode("utf-8")

    @classmethod
    def from_json(cls, data: bytes) -> Manifest:
        """Parse and validate a manifest. Raises :class:`ManifestError`."""
        if len(data) > MAX_MANIFEST_BYTES:
            raise ManifestError("the manifest is too large")
        try:
            document = json.loads(data.decode("utf-8"))
        except (UnicodeDecodeError, ValueError, RecursionError) as exc:
            raise ManifestError("the manifest is not valid JSON") from exc
        if not isinstance(document, dict):
            raise ManifestError("the manifest is not a JSON object")
        if document.get("schema") != MANIFEST_SCHEMA or document.get("tokenizer") != SENTENCEPIECE_TOKENIZER:
            raise ManifestError("the manifest has an unsupported schema or tokenizer")

        source = _code(document, "from")
        target = _code(document, "to")
        if source == target:
            raise ManifestError("the manifest translates a language into itself")
        return cls(
            source=source,
            target=target,
            source_name=clean_name(document.get("fromName"), default_language_name(source)),
            target_name=clean_name(document.get("toName"), default_language_name(target)),
            package_version=_matching(document, "packageVersion", PACKAGE_VERSION_PATTERN),
            target_prefix=_target_prefix(document),
            archive_sha256=_matching(document, "archiveSha256", SHA256_PATTERN),
            archive_bytes=_size(document, "archiveBytes"),
            model_sha256=_matching(document, "modelSha256", SHA256_PATTERN),
            model_bytes=_size(document, "modelBytes"),
            imported_at=_text(document, "importedAt", 40),
        )


def _text(document: dict[str, Any], key: str, max_length: int) -> str:
    value = document.get(key)
    if not isinstance(value, str) or not value or len(value) > max_length:
        raise ManifestError(f"'{key}' is missing or invalid")
    return value


def _matching(document: dict[str, Any], key: str, pattern: re.Pattern[str]) -> str:
    value = document.get(key)
    if not isinstance(value, str) or not pattern.match(value):
        raise ManifestError(f"'{key}' is missing or invalid")
    return value


def _code(document: dict[str, Any], key: str) -> str:
    return _matching(document, key, PACKAGE_CODE_PATTERN)


def _size(document: dict[str, Any], key: str) -> int:
    value = document.get(key)
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ManifestError(f"'{key}' is missing or invalid")
    return value


def _target_prefix(document: dict[str, Any]) -> str:
    value = document.get("targetPrefix", "")
    if not isinstance(value, str) or len(value) > MAX_TARGET_PREFIX_LENGTH or any(not ch.isprintable() for ch in value):
        raise ManifestError("'targetPrefix' is invalid")
    return value
