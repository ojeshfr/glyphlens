"""The installed packages, found by scanning the model directory.

Layout: one subdirectory ``<from>_<to>`` per package, holding ``glyphlens-model.json`` (written by the importer),
``metadata.json``, ``sentencepiece.model`` and ``model/`` (``model.bin``, ``config.json`` and the vocabulary).

Scanning is cheap enough to repeat for every request: it reads one small manifest per package and ``stat``s the model
files. It never hashes ``model.bin`` (the hash is in the manifest). A directory that does not hold a complete package
is skipped and reported as a problem; it never makes the scan fail.
"""

from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from glyphlens_worker.models.manifest import MANIFEST_FILE_NAME, MAX_MANIFEST_BYTES, Manifest, ManifestError

MODEL_SUBDIRECTORY = "model"
MODEL_BIN = "model.bin"
MODEL_CONFIG = "config.json"
METADATA_FILE = "metadata.json"
SENTENCEPIECE_MODEL = "sentencepiece.model"

#: Directories starting with this are staging or backup directories of the importer, not packages.
#: The OCR models live in ``<models>/ocr`` (see ``engines/ocr``); it is not a translation package.
RESERVED_OCR_DIRECTORY = "ocr"
HIDDEN_PREFIX = "."

MAX_DIRECTORY_NAME_DISPLAY = 40


@dataclass(frozen=True)
class InstalledPackage:
    """One installed package: its manifest and where it lives."""

    manifest: Manifest
    directory: Path

    @property
    def source(self) -> str:
        return self.manifest.source

    @property
    def target(self) -> str:
        return self.manifest.target

    @property
    def package_name(self) -> str:
        return self.manifest.package_name

    @property
    def model_directory(self) -> Path:
        return self.directory / MODEL_SUBDIRECTORY

    @property
    def version_tag(self) -> str:
        """``argos:<from>-<to>:<package_version>:<first 12 hex of the sha256 of model.bin>``."""
        m = self.manifest
        return f"argos:{m.source}-{m.target}:{m.package_version}:{m.model_sha256[:12]}"

    def to_wire(self) -> dict[str, Any]:
        m = self.manifest
        return {
            "from": m.source,
            "to": m.target,
            "fromName": m.source_name,
            "toName": m.target_name,
            "version": m.package_version,
            "packageName": m.package_name,
        }


@dataclass(frozen=True)
class RegistryScan:
    """The result of scanning the model directory."""

    packages: tuple[InstalledPackage, ...]
    problems: tuple[str, ...]
    #: ``False`` when the directory exists but could not be listed (a missing directory is readable: it is empty).
    readable: bool = True


def display_directory_name(name: str) -> str:
    """A directory name that is safe to put in a problem message (bounded, printable)."""
    printable = "".join(ch if ch.isprintable() else "?" for ch in name)
    return printable[:MAX_DIRECTORY_NAME_DISPLAY]


def scan_models(root: Path | None) -> RegistryScan:
    """List the valid packages below ``root``, sorted by (from, to), and explain every directory that was skipped."""
    if root is None:
        return RegistryScan((), (), readable=False)

    try:
        with os.scandir(root) as listing:
            entries = sorted(listing, key=lambda entry: entry.name)
    except FileNotFoundError:
        return RegistryScan((), ())
    except OSError:
        return RegistryScan((), ("The model directory could not be read.",), readable=False)

    packages: list[InstalledPackage] = []
    problems: list[str] = []
    for entry in entries:
        if entry.name.startswith(HIDDEN_PREFIX) or entry.name == RESERVED_OCR_DIRECTORY:
            continue
        try:
            is_directory = entry.is_dir(follow_symlinks=False)
        except OSError:
            is_directory = False
        if not is_directory:
            problems.append(f"Ignored '{display_directory_name(entry.name)}': it is not a package directory.")
            continue
        try:
            packages.append(_load_package(Path(entry.path), entry.name))
        except ManifestError as exc:
            problems.append(f"Ignored package '{display_directory_name(entry.name)}': {exc}")

    packages.sort(key=lambda package: (package.source, package.target))
    return RegistryScan(tuple(packages), tuple(problems))


def _load_package(directory: Path, directory_name: str) -> InstalledPackage:
    manifest = _read_manifest(directory)
    if directory_name != manifest.directory_name:
        raise ManifestError("the directory name does not match the package")

    model_bin = directory / MODEL_SUBDIRECTORY / MODEL_BIN
    required = (
        (directory / METADATA_FILE, None),
        (directory / SENTENCEPIECE_MODEL, None),
        (directory / MODEL_SUBDIRECTORY / MODEL_CONFIG, None),
        (model_bin, manifest.model_bytes),
    )
    for path, expected_size in required:
        try:
            size = path.stat().st_size
        except OSError as exc:
            raise ManifestError(f"'{path.name}' is missing; install the package again") from exc
        if expected_size is not None and size != expected_size:
            raise ManifestError(f"'{path.name}' does not match its manifest; install the package again")
    return InstalledPackage(manifest, directory)


def _read_manifest(directory: Path) -> Manifest:
    try:
        with open(directory / MANIFEST_FILE_NAME, "rb") as handle:
            data = handle.read(MAX_MANIFEST_BYTES + 1)
    except OSError as exc:
        raise ManifestError("it has no readable manifest") from exc
    return Manifest.from_json(data)
