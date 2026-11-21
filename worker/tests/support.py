"""Shared test helpers: protocol messages, synthetic ``.argosmodel`` archives and fake engine backends."""

from __future__ import annotations

import json
import stat
import zipfile
from collections.abc import Iterable, Sequence
from datetime import datetime, timezone
from pathlib import Path

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.language_detection.backends import BackendSpec
from glyphlens_worker.engines.language_detection.service import LanguageDetectionService
from glyphlens_worker.engines.translation.backend import RuntimeUnavailableError
from glyphlens_worker.engines.translation.service import TranslationService
from glyphlens_worker.models.manifest import Manifest
from glyphlens_worker.models.registry import InstalledPackage
from glyphlens_worker.protocols.envelope import Envelope
from glyphlens_worker.runtime.services import Services

TOKEN = "a" * 64

# ---------------------------------------------------------------------------------------------------------------------
# Messages


def request(message_type: str, payload: object = None, request_id: str = "req-1") -> Envelope:
    return Envelope("1.0", request_id, message_type, payload, datetime(2026, 1, 1, tzinfo=timezone.utc))


# ---------------------------------------------------------------------------------------------------------------------
# Synthetic packages


def argos_entries(
    *,
    root: str = "translate-fr_en-1_9",
    source: str = "fr",
    target: str = "en",
    version: str = "1.9",
    tokenizer: str = "sentencepiece.model",
    model_bytes: bytes = b"fake CTranslate2 weights",
    metadata: dict[str, object] | None = None,
) -> dict[str, bytes]:
    """The entries of a small, valid package: the real layout with tiny fake files (and Stanza data that must be skipped)."""
    document: dict[str, object] = {
        "package_version": version,
        "argos_version": "1.9.0",
        "from_code": source,
        "from_name": source.upper(),
        "to_code": target,
        "to_name": target.upper(),
    }
    document.update(metadata or {})
    return {
        f"{root}/": b"",
        f"{root}/README.md": b"Fake package. CC-BY 4.0.\n",
        f"{root}/metadata.json": json.dumps(document).encode("utf-8"),
        f"{root}/{tokenizer}": b"fake tokenizer",
        f"{root}/model/": b"",
        f"{root}/model/model.bin": model_bytes,
        f"{root}/model/config.json": b"{}",
        f"{root}/model/shared_vocabulary.json": b"[]",
        f"{root}/stanza/": b"",
        f"{root}/stanza/resources.json": b"{}",
        f"{root}/stanza/{source}/tokenize/gsd.pt": b"fake stanza data",
    }


def write_archive(path: Path, entries: dict[str, bytes] | Iterable[tuple[str | zipfile.ZipInfo, bytes]]) -> Path:
    """Write a zip with exactly the given entries (names ending in ``/`` are directories)."""
    items = entries.items() if isinstance(entries, dict) else entries
    path.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, data in items:
            archive.writestr(name, data)
    return path


def symlink_entry(name: str) -> zipfile.ZipInfo:
    info = zipfile.ZipInfo(name)
    info.create_system = 3
    info.external_attr = (stat.S_IFLNK | 0o777) << 16
    return info


def make_package(path: Path, **kwargs: object) -> Path:
    """Write a valid synthetic package to ``path`` (``source``/``target``/``version`` and so on as in :func:`argos_entries`)."""
    return write_archive(path, argos_entries(**kwargs))  # type: ignore[arg-type]


def fake_package(source: str, target: str, version: str = "1.9", model_sha256: str | None = None) -> InstalledPackage:
    """An installed package that exists only in memory (for the route finder)."""
    digest = model_sha256 or (f"{source}{target}".encode().hex() * 32)[:64]
    manifest = Manifest(
        source=source,
        target=target,
        source_name=source,
        target_name=target,
        package_version=version,
        target_prefix="",
        archive_sha256="0" * 64,
        archive_bytes=1,
        model_sha256=digest,
        model_bytes=1,
        imported_at="2026-01-01T00:00:00Z",
    )
    return InstalledPackage(manifest, Path(f"{source}_{target}"))


# ---------------------------------------------------------------------------------------------------------------------
# Fake translation runtime


class FakeSegmentTranslator:
    """Translates ``segment`` to ``[<source>><target>] segment`` and records every batch it is given."""

    def __init__(self, label: str, fail_on: Sequence[str] = ()) -> None:
        self.label = label
        self.fail_on = tuple(fail_on)
        self.batches: list[list[str]] = []

    def count_tokens(self, text: str) -> int:
        return len(text.split())

    def translate(self, segments: Sequence[str]) -> list[str]:
        self.batches.append(list(segments))
        for segment in segments:
            if any(marker in segment for marker in self.fail_on):
                raise RuntimeError("simulated native failure")
        return [f"[{self.label}] {segment}" for segment in segments]


class FakeRuntime:
    version = "9.9.9"

    def __init__(self, fail_loading: Sequence[str] = (), fail_on: Sequence[str] = ()) -> None:
        self.fail_loading = tuple(fail_loading)
        self.fail_on = tuple(fail_on)
        self.loaded: list[str] = []  # directory names, in load order
        self.translators: dict[str, FakeSegmentTranslator] = {}

    def load(self, package_directory: Path, target_prefix: str) -> FakeSegmentTranslator:
        name = package_directory.name
        if name in self.fail_loading:
            raise OSError("simulated model load failure with a secret path")
        self.loaded.append(name)
        translator = FakeSegmentTranslator(name.replace("_", ">"), self.fail_on)
        self.translators[name] = translator
        return translator


class FakeRuntimeLoader:
    def __init__(self, runtime: FakeRuntime | None = None, present: bool = True, failure: str | None = None) -> None:
        self.runtime = runtime if runtime is not None else FakeRuntime()
        self.present = present
        self.failure = failure
        self.load_calls = 0

    def is_present(self) -> bool:
        return self.present

    def load(self) -> FakeRuntime:
        self.load_calls += 1
        if not self.present:
            raise RuntimeUnavailableError("The translation runtime is not installed. Install it with: pip install fake")
        if self.failure is not None:
            raise RuntimeUnavailableError(self.failure)
        return self.runtime


# ---------------------------------------------------------------------------------------------------------------------
# Fake language detector


class FakeDetectorBackend:
    """Returns the candidates it was given for every text and records how it was called."""

    def __init__(self, candidates: list[tuple[str, float | None]] | None = None) -> None:
        self.candidates = candidates if candidates is not None else [("de", 0.9), ("nl", 0.07), ("sv", 0.02), ("da", 0.01)]
        self.calls: list[tuple[str, int]] = []

    def identify(self, text: str, limit: int) -> list[tuple[str, float | None]]:
        self.calls.append((text, limit))
        return self.candidates[:limit]


def fake_backend_spec(backend: FakeDetectorBackend | None = None, *, name: str = "fake", module: str = "json") -> BackendSpec:
    """A detector spec whose library is always 'installed' (the stdlib ``json``) and that loads ``backend``."""
    chosen = backend if backend is not None else FakeDetectorBackend()
    return BackendSpec(name, "no-such-distribution", module, (3, 0), "pip install fake-detector", lambda: chosen)


# ---------------------------------------------------------------------------------------------------------------------
# Services


def make_services(
    models_dir: Path,
    *,
    loader: FakeRuntimeLoader | None = None,
    detector_specs: Sequence[BackendSpec] | None = None,
) -> Services:
    """Services over fakes and a temporary model directory (never the real one)."""
    specs = detector_specs if detector_specs is not None else [fake_backend_spec()]
    return Services(
        LanguageDetectionService(specs),
        TranslationService(ModelsLocation(models_dir), loader if loader is not None else FakeRuntimeLoader()),
    )
