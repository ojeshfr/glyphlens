"""The validating importer behind ``models.install``.

An ``.argosmodel`` file is a zip archive chosen by the user, so it is treated as hostile input. Argos' own installer is
an unvalidated ``extractall``; this one checks the archive before it writes anything, extracts only the files the engine
needs, and installs the result atomically:

* the path is absolute and local (no UNC or device path), ends in ``.argosmodel`` and is a regular file (not a link)
  of at most 1 GiB; the archive is opened once and the same handle is used for every read;
* the central directory is bounded before the zip library parses it, and ZIP64 archives are refused;
* every entry name is checked (no ``..``, absolute or drive-letter names, backslashes, reserved device names, symlinks,
  duplicates), there is exactly one top-level directory, and the entries below it follow an allow-list;
* entry count, declared size and expansion ratio are checked up front and the real sizes again while streaming,
  because headers can lie;
* ``metadata.json`` must name valid language codes and a package version; ``model/model.bin``,
  ``model/config.json`` and ``sentencepiece.model`` must exist (a package with only ``bpe.model`` is refused as unsupported);
* only the needed files are extracted (never ``stanza/``, ``minisbd/`` or ``spacy/``: unused, and a network hazard in
  Argos), into ``.staging-<uuid>`` inside the model directory, then moved into place.

Messages never contain archive content or paths. There is no signature to verify (Argos publishes none), so importing
a package is a trust decision for the user; the manifest records the archive's SHA-256.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import stat
import struct
import uuid
import zipfile
import zlib
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import BinaryIO

from glyphlens_worker.errors import ServiceError
from glyphlens_worker.languages import canonical_language
from glyphlens_worker.models.manifest import (
    MANIFEST_FILE_NAME,
    MAX_TARGET_PREFIX_LENGTH,
    PACKAGE_VERSION_PATTERN,
    Manifest,
    clean_name,
    default_language_name,
)
from glyphlens_worker.models.registry import (
    METADATA_FILE,
    MODEL_BIN,
    MODEL_CONFIG,
    MODEL_SUBDIRECTORY,
    SENTENCEPIECE_MODEL,
    InstalledPackage,
)

PACKAGE_EXTENSION = ".argosmodel"
MAX_PATH_LENGTH = 4096
MAX_ARCHIVE_BYTES = 1024 * 1024 * 1024
MAX_ENTRIES = 2000
MAX_TOTAL_BYTES = 2 * 1024 * 1024 * 1024
MAX_METADATA_BYTES = 64 * 1024
MAX_CENTRAL_DIRECTORY_BYTES = 4 * 1024 * 1024
MAX_EXPANSION_RATIO = 400
RATIO_CHECK_MIN_BYTES = 1_000_000
CHUNK_BYTES = 1024 * 1024

STAGING_PREFIX = ".staging-"
BACKUP_PREFIX = ".old-"

BPE_MODEL = "bpe.model"
LICENSE_FILES = frozenset({"README.md", "LICENSE", "LICENSE.md", "LICENSE.txt"})
_FILES_BELOW_ROOT = frozenset({METADATA_FILE, SENTENCEPIECE_MODEL, BPE_MODEL}) | LICENSE_FILES
#: Directories below the root. Only ``model`` is extracted; the others are Argos' sentence-splitter data.
_MODEL_DIRECTORY = MODEL_SUBDIRECTORY
_IGNORED_DIRECTORIES = frozenset({"stanza", "minisbd", "spacy"})

_ROOT_PATTERN = re.compile(r"^[A-Za-z0-9._-]{1,64}$")
_MODEL_FILE_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")
_CODE_PATTERN = re.compile(r"^[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})?$")
_FORBIDDEN_NAME_CHARACTERS = frozenset('<>:"|?*\\')
_RESERVED_DEVICE_NAMES = frozenset({"con", "prn", "aux", "nul", *(f"com{n}" for n in range(1, 10)), *(f"lpt{n}" for n in range(1, 10))})

_END_OF_CENTRAL_DIRECTORY = struct.Struct("<4s4H2LH")
_END_SIGNATURE = b"PK\x05\x06"
_ZIP64_LOCATOR_SIGNATURE = b"PK\x06\x07"
_ZIP64_LOCATOR_SIZE = 20
_MAX_COMMENT_BYTES = 0xFFFF

_ENCRYPTED_FLAG = 0x1


@dataclass(frozen=True)
class InstallResult:
    package: InstalledPackage
    replaced: bool


@dataclass(frozen=True)
class _Entry:
    info: zipfile.ZipInfo
    parts: tuple[str, ...]  # path below the top-level directory


@dataclass(frozen=True)
class _Layout:
    files: dict[tuple[str, ...], _Entry]  # files that exist and are kept or needed, by path below the root


def _invalid(message: str) -> ServiceError:
    return ServiceError("invalid_package", message)


def check_archive_path(value: object) -> Path:
    """Validate the ``path`` of a ``models.install`` request (``invalid_request`` on any violation)."""
    if not isinstance(value, str) or not value or len(value) > MAX_PATH_LENGTH or "\x00" in value:
        raise ServiceError("invalid_request", "'path' must be a non-empty string.")
    if value.startswith(("\\\\", "//")):
        raise ServiceError("invalid_request", "Network and device paths are not supported; copy the package to a local drive.")
    path = Path(value)
    if not path.is_absolute():
        raise ServiceError("invalid_request", "'path' must be an absolute path.")
    if path.suffix.lower() != PACKAGE_EXTENSION:
        raise ServiceError("invalid_request", f"The package file must have the {PACKAGE_EXTENSION} extension.")
    return path


def install_package(
    archive_path: Path,
    models_root: Path,
    before_replace: Callable[[str, str], None] | None = None,
) -> InstallResult:
    """Validate ``archive_path`` and install it below ``models_root``.

    ``before_replace(source, target)`` is called once the package is validated and extracted, just before an existing
    package of the same pair is swapped out, so the caller can release files it has loaded.
    Raises :class:`ServiceError` with ``package_not_found``, ``invalid_package``, ``unsupported_package`` or ``install_failed``.
    """
    try:
        handle = _open_regular_file(archive_path)
    except OSError as exc:
        raise ServiceError("package_not_found", "The package file was not found or cannot be read.") from exc

    with handle:
        try:
            return _install_from_handle(handle, models_root, before_replace)
        except OSError as exc:
            raise ServiceError("install_failed", "The package could not be installed (a file operation failed).") from exc
        except (zipfile.BadZipFile, zlib.error, EOFError, NotImplementedError, RuntimeError, struct.error) as exc:
            raise _invalid("The archive is damaged or uses an unsupported zip feature.") from exc


def _open_regular_file(path: Path) -> BinaryIO:
    """Open ``path`` for reading after checking it is a regular file (not a link or a directory) within the size limit."""
    status = os.lstat(path)
    if not stat.S_ISREG(status.st_mode):
        raise _invalid("The package path is not a regular file.")
    handle = open(path, "rb")  # noqa: SIM115 - the caller owns the handle and closes it
    try:
        opened = os.fstat(handle.fileno())
        if not stat.S_ISREG(opened.st_mode):
            raise _invalid("The package path is not a regular file.")
        if opened.st_size > MAX_ARCHIVE_BYTES:
            raise _invalid("The package file is larger than 1 GiB.")
        if opened.st_size == 0:
            raise _invalid("The package file is empty.")
    except BaseException:
        handle.close()
        raise
    return handle


def _install_from_handle(
    handle: BinaryIO,
    models_root: Path,
    before_replace: Callable[[str, str], None] | None,
) -> InstallResult:
    archive_bytes = os.fstat(handle.fileno()).st_size
    _check_central_directory(handle, archive_bytes)

    with zipfile.ZipFile(handle) as archive:
        layout = _inspect(archive)
        metadata = _read_metadata(archive, layout)
        source, target = _read_languages(metadata)
        package_version = _read_version(metadata)
        target_prefix = _read_target_prefix(metadata)
        _require_model_files(layout)

        models_root.mkdir(parents=True, exist_ok=True)
        _remove_leftovers(models_root)
        staging = models_root / f"{STAGING_PREFIX}{uuid.uuid4().hex}"
        try:
            staging.mkdir()
            model_sha256, model_bytes = _extract(archive, layout, staging)
            manifest = Manifest(
                source=source,
                target=target,
                source_name=clean_name(metadata.get("from_name"), default_language_name(source)),
                target_name=clean_name(metadata.get("to_name"), default_language_name(target)),
                package_version=package_version,
                target_prefix=target_prefix,
                archive_sha256=_sha256_of(handle),
                archive_bytes=archive_bytes,
                model_sha256=model_sha256,
                model_bytes=model_bytes,
                imported_at=datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z"),
            )
            (staging / MANIFEST_FILE_NAME).write_bytes(manifest.to_json())

            final = models_root / manifest.directory_name
            if before_replace is not None:
                before_replace(manifest.source, manifest.target)
            replaced = _swap_into_place(staging, final)
        finally:
            shutil.rmtree(staging, ignore_errors=True)  # a no-op after a successful move

    return InstallResult(InstalledPackage(manifest, final), replaced)


# ---------------------------------------------------------------------------------------------------------------------
# Inspection (nothing is written)


def _check_central_directory(handle: BinaryIO, archive_bytes: int) -> None:
    """Bound the entry count and central directory size before the zip library reads (and allocates) every entry."""
    tail_size = min(archive_bytes, _END_OF_CENTRAL_DIRECTORY.size + _MAX_COMMENT_BYTES)
    handle.seek(archive_bytes - tail_size)
    tail = handle.read(tail_size)
    start = tail.rfind(_END_SIGNATURE)
    if start < 0 or len(tail) - start < _END_OF_CENTRAL_DIRECTORY.size:
        raise _invalid("The file is not a zip archive.")
    locator = start - _ZIP64_LOCATOR_SIZE
    if locator >= 0 and tail[locator : locator + len(_ZIP64_LOCATOR_SIGNATURE)] == _ZIP64_LOCATOR_SIGNATURE:
        raise _invalid("ZIP64 archives are not supported.")

    _, _, _, entries_on_disk, entries_total, directory_bytes, _, _ = _END_OF_CENTRAL_DIRECTORY.unpack_from(tail, start)
    if entries_total == 0 or entries_total != entries_on_disk:
        raise _invalid("The archive has no entries or spans several disks.")
    if entries_total > MAX_ENTRIES:
        raise _invalid(f"The archive has more than {MAX_ENTRIES} entries.")
    if directory_bytes > MAX_CENTRAL_DIRECTORY_BYTES:
        raise _invalid("The archive's directory is too large.")


def _inspect(archive: zipfile.ZipFile) -> _Layout:
    infos = archive.infolist()
    if len(infos) > MAX_ENTRIES:
        raise _invalid(f"The archive has more than {MAX_ENTRIES} entries.")

    root: str | None = None
    seen: set[str] = set()
    declared_total = 0
    files: dict[tuple[str, ...], _Entry] = {}

    for info in infos:
        parts = _entry_parts(info)
        _check_entry_kind(info)

        if root is None:
            root = parts[0]
        elif parts[0] != root:
            raise _invalid("The archive must contain exactly one top-level directory.")
        if not _ROOT_PATTERN.match(root):
            raise _invalid("The top-level directory name is not allowed.")

        key = "/".join(parts).lower()  # Windows file systems are case-insensitive
        if key in seen:
            raise _invalid("The archive contains the same entry twice.")
        seen.add(key)

        below_root = parts[1:]
        if not below_root:
            if not info.is_dir():
                raise _invalid("The archive has a file where the top-level directory should be.")
            continue
        if not _is_allowed(info, below_root):
            raise _invalid("The archive contains an entry that a translation package does not have.")
        if info.is_dir():
            continue

        declared_total += info.file_size
        if declared_total > MAX_TOTAL_BYTES:
            raise _invalid("The archive expands to more than 2 GiB.")
        if info.file_size > RATIO_CHECK_MIN_BYTES and info.file_size > MAX_EXPANSION_RATIO * max(info.compress_size, 1):
            raise _invalid("An entry expands suspiciously much.")
        files[below_root] = _Entry(info, below_root)

    return _Layout(files)


def _entry_parts(info: zipfile.ZipInfo) -> tuple[str, ...]:
    name = info.filename.removesuffix("/")
    if (
        not name
        or len(name) > 255
        or name.startswith("/")
        or any(ch in _FORBIDDEN_NAME_CHARACTERS or ord(ch) < 32 for ch in name)
    ):
        raise _invalid("The archive contains an entry with an unsafe name.")
    parts = tuple(name.split("/"))
    if any(part in ("", ".", "..") or part.endswith((".", " ")) for part in parts):
        raise _invalid("The archive contains an entry with an unsafe name.")
    return parts


def _check_entry_kind(info: zipfile.ZipInfo) -> None:
    if info.flag_bits & _ENCRYPTED_FLAG:
        raise _invalid("The archive is encrypted.")
    if stat.S_IFMT(info.external_attr >> 16) not in (0, stat.S_IFREG, stat.S_IFDIR):
        raise _invalid("The archive contains a link or special file.")


def _is_allowed(info: zipfile.ZipInfo, below_root: tuple[str, ...]) -> bool:
    first = below_root[0]
    if first in _FILES_BELOW_ROOT:
        return len(below_root) == 1 and not info.is_dir()
    if first in _IGNORED_DIRECTORIES:
        return len(below_root) > 1 or info.is_dir()
    if first == _MODEL_DIRECTORY:
        if len(below_root) == 1:
            return info.is_dir()
        name = below_root[1]
        return (
            len(below_root) == 2
            and not info.is_dir()
            and _MODEL_FILE_PATTERN.match(name) is not None
            and name.split(".")[0].lower() not in _RESERVED_DEVICE_NAMES
        )
    return False


def _read_metadata(archive: zipfile.ZipFile, layout: _Layout) -> dict[str, object]:
    entry = layout.files.get((METADATA_FILE,))
    if entry is None:
        raise _invalid("The package has no metadata.json.")
    if entry.info.file_size > MAX_METADATA_BYTES:
        raise _invalid("metadata.json is too large.")
    with archive.open(entry.info) as stream:
        data = stream.read(MAX_METADATA_BYTES + 1)
    if len(data) > MAX_METADATA_BYTES:
        raise _invalid("metadata.json is too large.")
    try:
        metadata = json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, ValueError, RecursionError) as exc:
        raise _invalid("metadata.json is not valid JSON.") from exc
    if not isinstance(metadata, dict):
        raise _invalid("metadata.json is not a JSON object.")
    if metadata.get("type", "translate") != "translate":
        raise _invalid("The package is not a translation package.")
    return metadata


def _read_languages(metadata: dict[str, object]) -> tuple[str, str]:
    codes: list[str] = []
    for key in ("from_code", "to_code"):
        value = metadata.get(key)
        if not isinstance(value, str) or not _CODE_PATTERN.match(value):
            raise _invalid(f"metadata.json has no valid {key}.")
        codes.append(canonical_language(value.lower().replace("_", "-")))
    source, target = codes
    if source == target:
        raise _invalid("The package translates a language into itself.")
    return source, target


def _read_version(metadata: dict[str, object]) -> str:
    value = metadata.get("package_version")
    if not isinstance(value, str) or not PACKAGE_VERSION_PATTERN.match(value):
        raise _invalid("metadata.json has no valid package_version.")
    return value


def _read_target_prefix(metadata: dict[str, object]) -> str:
    value = metadata.get("target_prefix", "")
    if not isinstance(value, str) or len(value) > MAX_TARGET_PREFIX_LENGTH or any(not ch.isprintable() for ch in value):
        raise _invalid("metadata.json has an invalid target_prefix.")
    return value


def _require_model_files(layout: _Layout) -> None:
    if (MODEL_SUBDIRECTORY, MODEL_BIN) not in layout.files or (MODEL_SUBDIRECTORY, MODEL_CONFIG) not in layout.files:
        raise _invalid("The package has no CTranslate2 model (model/model.bin and model/config.json).")
    if (SENTENCEPIECE_MODEL,) in layout.files:
        return
    if (BPE_MODEL,) in layout.files:
        raise ServiceError(
            "unsupported_package",
            "This package uses a BPE tokenizer (bpe.model), which this version of GlyphLens cannot run yet.",
        )
    raise _invalid("The package has no sentencepiece.model.")


# ---------------------------------------------------------------------------------------------------------------------
# Writing


def _wanted(below_root: tuple[str, ...]) -> bool:
    """Whether a file is kept: the model, its tokenizer and metadata, and the licence text (attribution is required)."""
    first = below_root[0]
    return first == _MODEL_DIRECTORY or first in LICENSE_FILES or below_root in ((METADATA_FILE,), (SENTENCEPIECE_MODEL,))


def _extract(archive: zipfile.ZipFile, layout: _Layout, staging: Path) -> tuple[str, int]:
    """Extract the wanted files into ``staging``, enforcing the size limits on the bytes actually produced.

    Returns the SHA-256 and size of ``model/model.bin``.
    """
    written_total = 0
    model_digest = hashlib.sha256()
    model_bytes = 0

    for below_root, entry in layout.files.items():
        if not _wanted(below_root):
            continue
        destination = staging.joinpath(*below_root)
        destination.parent.mkdir(exist_ok=True)
        is_model = below_root == (MODEL_SUBDIRECTORY, MODEL_BIN)
        written = 0
        ratio_limit = MAX_EXPANSION_RATIO * max(entry.info.compress_size, 1)
        with archive.open(entry.info) as source, open(destination, "xb") as target:
            while chunk := source.read(CHUNK_BYTES):
                written += len(chunk)
                written_total += len(chunk)
                if written_total > MAX_TOTAL_BYTES:
                    raise _invalid("The archive expands to more than 2 GiB.")
                if written > RATIO_CHECK_MIN_BYTES and written > ratio_limit:
                    raise _invalid("An entry expands suspiciously much.")
                target.write(chunk)
                if is_model:
                    model_digest.update(chunk)
        if is_model:
            model_bytes = written

    return model_digest.hexdigest(), model_bytes


def _sha256_of(handle: BinaryIO) -> str:
    digest = hashlib.sha256()
    handle.seek(0)
    while chunk := handle.read(CHUNK_BYTES):
        digest.update(chunk)
    return digest.hexdigest()


def _swap_into_place(staging: Path, final: Path) -> bool:
    """Move ``staging`` to ``final``, replacing an existing package. Returns whether one was replaced.

    The old package is renamed away first and restored if the move fails, so a failure never leaves the pair without a package.
    """
    backup: Path | None = None
    if os.path.lexists(final):
        backup = final.with_name(f"{BACKUP_PREFIX}{uuid.uuid4().hex}")
        os.replace(final, backup)
    try:
        os.replace(staging, final)
    except OSError:
        if backup is not None:
            os.replace(backup, final)
        raise
    if backup is not None:
        shutil.rmtree(backup, ignore_errors=True)
    return backup is not None


def _remove_leftovers(models_root: Path) -> None:
    """Delete staging and backup directories a crashed import left behind (best effort)."""
    try:
        for entry in os.scandir(models_root):
            if entry.name.startswith((STAGING_PREFIX, BACKUP_PREFIX)):
                shutil.rmtree(entry.path, ignore_errors=True)
    except OSError:
        pass
