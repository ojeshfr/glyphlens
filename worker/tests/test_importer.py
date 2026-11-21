"""Tests for the validating importer: hostile archives are refused and nothing is written outside the model directory."""

from __future__ import annotations

import hashlib
import json
import os
import struct
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest import mock

from glyphlens_worker.errors import ServiceError
from glyphlens_worker.models import importer
from glyphlens_worker.models.importer import check_archive_path, install_package
from glyphlens_worker.models.registry import scan_models

from tests.support import argos_entries, make_package, symlink_entry, write_archive

ROOT = "translate-fr_en-1_9"


class ImporterTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.models = self.temp / "work" / "models"  # does not exist yet: the importer creates it
        self.archive = self.temp / "downloads" / "translate-fr_en-1_9.argosmodel"

    def files_below(self, directory: Path) -> set[str]:
        return {path.relative_to(directory).as_posix() for path in directory.rglob("*") if path.is_file()}

    def assert_refused(self, code: str, archive: Path | None = None) -> ServiceError:
        with self.assertRaises(ServiceError) as raised:
            install_package(archive or self.archive, self.models)
        self.assertEqual(raised.exception.code, code, raised.exception.message)
        return raised.exception

    def assert_nothing_installed(self) -> None:
        """No package, no staging directory and nothing outside the model directory and the downloads folder."""
        if self.models.exists():
            self.assertEqual(sorted(path.name for path in self.models.iterdir()), [])
        self.assertEqual(sorted(path.name for path in self.temp.iterdir()), sorted(name for name in ("downloads", "work") if (self.temp / name).exists()))
        self.assertEqual(self.files_below(self.temp), {"downloads/" + self.archive.name} if self.archive.exists() else set())


class ValidInstallTests(ImporterTestCase):
    def test_installs_only_the_files_the_engine_needs_and_writes_a_manifest(self) -> None:
        model_bytes = b"fake CTranslate2 weights " * 40
        make_package(self.archive, model_bytes=model_bytes)

        result = install_package(self.archive, self.models)

        self.assertFalse(result.replaced)
        self.assertEqual(
            self.files_below(self.models),
            {
                "fr_en/glyphlens-model.json",
                "fr_en/metadata.json",
                "fr_en/sentencepiece.model",
                "fr_en/README.md",
                "fr_en/model/model.bin",
                "fr_en/model/config.json",
                "fr_en/model/shared_vocabulary.json",
            },
        )
        self.assertEqual((self.models / "fr_en" / "model" / "model.bin").read_bytes(), model_bytes)

    def test_the_manifest_records_both_hashes_sizes_and_the_package_data(self) -> None:
        model_bytes = b"weights" * 100
        make_package(self.archive, model_bytes=model_bytes, version="1.9")

        result = install_package(self.archive, self.models)

        manifest = json.loads((self.models / "fr_en" / "glyphlens-model.json").read_text(encoding="utf-8"))
        self.assertEqual(manifest["archiveSha256"], hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assertEqual(manifest["archiveBytes"], self.archive.stat().st_size)
        self.assertEqual(manifest["modelSha256"], hashlib.sha256(model_bytes).hexdigest())
        self.assertEqual(manifest["modelBytes"], len(model_bytes))
        self.assertEqual((manifest["from"], manifest["to"], manifest["packageVersion"]), ("fr", "en", "1.9"))
        self.assertEqual(manifest["tokenizer"], "sentencepiece")
        self.assertRegex(manifest["importedAt"], r"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$")
        self.assertEqual(result.package.manifest.model_sha256, manifest["modelSha256"])

    def test_the_result_describes_the_package_as_the_protocol_does(self) -> None:
        make_package(self.archive)

        wire = install_package(self.archive, self.models).package.to_wire()

        self.assertEqual(
            wire,
            {"from": "fr", "to": "en", "fromName": "FR", "toName": "EN", "version": "1.9", "packageName": "translate-fr_en"},
        )

    def test_the_installed_package_is_found_by_a_scan(self) -> None:
        make_package(self.archive)
        install_package(self.archive, self.models)

        scan = scan_models(self.models)

        self.assertEqual([package.package_name for package in scan.packages], ["translate-fr_en"])
        self.assertEqual(scan.problems, ())

    def test_installing_the_same_pair_again_replaces_it_and_changes_the_model_hash(self) -> None:
        make_package(self.archive, model_bytes=b"first weights")
        first = install_package(self.archive, self.models)
        make_package(self.archive, model_bytes=b"second weights", version="2.0")

        second = install_package(self.archive, self.models)

        self.assertTrue(second.replaced)
        self.assertNotEqual(first.package.manifest.model_sha256, second.package.manifest.model_sha256)
        self.assertEqual((self.models / "fr_en" / "model" / "model.bin").read_bytes(), b"second weights")
        self.assertEqual(sorted(path.name for path in self.models.iterdir()), ["fr_en"])  # no backup left behind

    def test_the_caller_can_release_the_old_package_before_it_is_replaced(self) -> None:
        make_package(self.archive)
        install_package(self.archive, self.models)
        calls: list[tuple[str, str]] = []

        install_package(self.archive, self.models, before_replace=lambda source, target: calls.append((source, target)))

        self.assertEqual(calls, [("fr", "en")])

    def test_language_codes_are_mapped_to_the_codes_argos_uses(self) -> None:
        make_package(self.archive, source="pt_BR", target="en")
        make_package(self.temp / "downloads" / "zh.argosmodel", source="zh-Hant", target="EN")

        brazilian = install_package(self.archive, self.models)
        traditional = install_package(self.temp / "downloads" / "zh.argosmodel", self.models)

        self.assertEqual((brazilian.package.source, brazilian.package.target), ("pb", "en"))
        self.assertEqual((traditional.package.source, traditional.package.target), ("zt", "en"))
        self.assertEqual(sorted(path.name for path in self.models.iterdir()), ["pb_en", "zt_en"])

    def test_display_names_are_cleaned_and_fall_back_to_the_language_name(self) -> None:
        make_package(self.archive, metadata={"from_name": "Fran\u00e7ais\x07\n" + "x" * 200, "to_name": 5})

        package = install_package(self.archive, self.models).package

        self.assertNotIn("\x07", package.manifest.source_name)
        self.assertNotIn("\n", package.manifest.source_name)
        self.assertLessEqual(len(package.manifest.source_name), 64)
        self.assertEqual(package.manifest.target_name, "English")

    def test_a_target_prefix_is_kept(self) -> None:
        make_package(self.archive, metadata={"target_prefix": "__fr__"})

        self.assertEqual(install_package(self.archive, self.models).package.manifest.target_prefix, "__fr__")

    def test_leftovers_of_a_crashed_import_are_removed(self) -> None:
        (self.models / ".staging-deadbeef" / "model").mkdir(parents=True)
        (self.models / ".old-deadbeef").mkdir()
        make_package(self.archive)

        install_package(self.archive, self.models)

        self.assertEqual(sorted(path.name for path in self.models.iterdir()), ["fr_en"])

    def test_the_model_directory_is_created_on_demand(self) -> None:
        make_package(self.archive)
        self.assertFalse(self.models.exists())

        install_package(self.archive, self.models)

        self.assertTrue(self.models.is_dir())


class PathTests(ImporterTestCase):
    def test_check_archive_path_accepts_an_absolute_argosmodel_path(self) -> None:
        self.assertEqual(check_archive_path(str(self.archive)), self.archive)
        self.assertEqual(check_archive_path(str(self.archive).upper().replace("ARGOSMODEL", "ArgosModel")).suffix, ".ArgosModel")

    def test_invalid_paths_are_invalid_requests(self) -> None:
        for value in (None, 5, "", [], "relative\\x.argosmodel", "x.argosmodel", "\\\\server\\share\\x.argosmodel", "//server/share/x.argosmodel",
                      "\\\\.\\C:\\x.argosmodel", str(self.temp / "x.zip"), str(self.temp / "x"), str(self.temp / "x.argosmodel") + "\x00", "a" * 5000):
            with self.subTest(value=value), self.assertRaises(ServiceError) as raised:
                check_archive_path(value)
            self.assertEqual(raised.exception.code, "invalid_request")

    def test_a_missing_file_is_package_not_found(self) -> None:
        self.assert_refused("package_not_found")

    def test_a_directory_is_not_a_package(self) -> None:
        directory = self.temp / "folder.argosmodel"
        directory.mkdir()

        self.assert_refused("invalid_package", directory)

    def test_a_symlink_is_refused(self) -> None:
        make_package(self.temp / "real.argosmodel")
        link = self.temp / "link.argosmodel"
        try:
            os.symlink(self.temp / "real.argosmodel", link)
        except (OSError, NotImplementedError):
            self.skipTest("creating symbolic links needs a privilege this account does not have")

        self.assert_refused("invalid_package", link)

    def test_an_archive_over_the_size_limit_is_refused_before_it_is_read(self) -> None:
        make_package(self.archive)

        with mock.patch.object(importer, "MAX_ARCHIVE_BYTES", 10):
            self.assert_refused("invalid_package")
        self.assertFalse(self.models.exists())

    def test_an_empty_or_non_zip_file_is_invalid(self) -> None:
        self.archive.parent.mkdir(parents=True)
        for content in (b"", b"this is not a zip file" * 100, b"PK\x05\x06" + b"\x00" * 18):
            with self.subTest(size=len(content)):
                self.archive.write_bytes(content)
                self.assert_refused("invalid_package")
        self.assertFalse(self.models.exists())


class HostileArchiveTests(ImporterTestCase):
    def archive_with(self, extra: dict[str, bytes]) -> None:
        entries = argos_entries()
        entries.update(extra)
        write_archive(self.archive, entries)

    def test_zip_slip_names_are_refused_and_nothing_escapes(self) -> None:
        names = (
            "../evil.txt",
            f"{ROOT}/../../evil.txt",
            f"{ROOT}/model/../../../evil.txt",
            "/absolute/evil.txt",
            "C:/evil.txt",
            "C:evil.txt",
            f"{ROOT}\\..\\evil.txt",
            f"{ROOT}/model/model.bin:hidden-stream",
            f"{ROOT}/./model/x",
            f"{ROOT}//model/x",
        )
        for name in names:
            with self.subTest(name=name):
                self.archive_with({name: b"payload"})

                error = self.assert_refused("invalid_package")

                self.assertNotIn("evil", error.message)
                self.assert_nothing_installed()

    def test_a_symlink_entry_is_refused(self) -> None:
        write_archive(self.archive, [*argos_entries().items(), (symlink_entry(f"{ROOT}/model/link"), b"../../../secret")])

        self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_a_symlink_entry_in_the_ignored_data_is_refused_too(self) -> None:
        write_archive(self.archive, [*argos_entries().items(), (symlink_entry(f"{ROOT}/stanza/link"), b"/etc/passwd")])

        self.assert_refused("invalid_package")

    def test_too_many_entries_are_refused(self) -> None:
        entries = argos_entries()
        entries.update({f"{ROOT}/stanza/file-{n}.pt": b"" for n in range(importer.MAX_ENTRIES + 1)})
        write_archive(self.archive, entries)

        self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_a_directory_that_claims_too_many_entries_is_refused_before_it_is_parsed(self) -> None:
        make_package(self.archive)
        data = bytearray(self.archive.read_bytes())
        end = data.rfind(b"PK\x05\x06")
        struct.pack_into("<HH", data, end + 8, 60000, 60000)  # entries on this disk, entries in total
        self.archive.write_bytes(data)

        with mock.patch.object(zipfile, "ZipFile", side_effect=AssertionError("the zip library must not parse it")):
            self.assert_refused("invalid_package")

    def test_an_oversized_central_directory_is_refused(self) -> None:
        make_package(self.archive)
        data = bytearray(self.archive.read_bytes())
        end = data.rfind(b"PK\x05\x06")
        struct.pack_into("<L", data, end + 12, importer.MAX_CENTRAL_DIRECTORY_BYTES + 1)
        self.archive.write_bytes(data)

        self.assert_refused("invalid_package")

    def test_zip64_archives_are_refused(self) -> None:
        make_package(self.archive)
        data = self.archive.read_bytes()
        end = data.rfind(b"PK\x05\x06")
        locator = b"PK\x06\x07" + b"\x00" * 16
        self.archive.write_bytes(data[:end] + locator + data[end:])

        self.assert_refused("invalid_package")

    def test_an_entry_that_expands_suspiciously_much_is_refused(self) -> None:
        self.archive_with({f"{ROOT}/model/zeros.bin": b"\x00" * (4 * 1024 * 1024)})  # deflates to a few KB

        self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_an_archive_that_declares_too_much_data_is_refused(self) -> None:
        make_package(self.archive)

        with mock.patch.object(importer, "MAX_TOTAL_BYTES", 10):
            self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_the_real_size_is_enforced_while_streaming_not_only_from_the_headers(self) -> None:
        make_package(self.archive)
        with zipfile.ZipFile(self.archive) as archive:
            layout = importer._inspect(archive)
            staging = self.temp / "staging"
            staging.mkdir()

            with mock.patch.object(importer, "MAX_TOTAL_BYTES", 5), self.assertRaises(ServiceError) as raised:
                importer._extract(archive, layout, staging)

        self.assertEqual(raised.exception.code, "invalid_package")

    def test_the_ratio_is_enforced_while_streaming_too(self) -> None:
        self.archive_with({f"{ROOT}/model/zeros.bin": b"\x00" * (4 * 1024 * 1024)})
        with zipfile.ZipFile(self.archive) as archive:
            with mock.patch.object(importer, "MAX_EXPANSION_RATIO", 10**9):  # let the header check pass
                layout = importer._inspect(archive)
            staging = self.temp / "staging"
            staging.mkdir()

            with self.assertRaises(ServiceError) as raised:
                importer._extract(archive, layout, staging)

        self.assertEqual(raised.exception.code, "invalid_package")

    def test_more_than_one_top_level_directory_is_refused(self) -> None:
        self.archive_with({"other-root/metadata.json": b"{}"})

        self.assert_refused("invalid_package")

    def test_a_file_next_to_the_top_level_directory_is_refused(self) -> None:
        self.archive_with({"readme.txt": b"hello"})

        self.assert_refused("invalid_package")

    def test_unexpected_entries_are_refused(self) -> None:
        for name in (f"{ROOT}/evil.exe", f"{ROOT}/glyphlens-model.json", f"{ROOT}/model/sub/dir.bin", f"{ROOT}/model/sub/", f"{ROOT}/README.md/x"):
            with self.subTest(name=name):
                self.archive_with({name: b"x"})
                self.assert_refused("invalid_package")

    def test_a_file_where_a_directory_belongs_is_refused(self) -> None:
        for name in (f"{ROOT}/stanza", f"{ROOT}/model"):
            with self.subTest(name=name):
                entries = {key: value for key, value in argos_entries().items() if not key.startswith(name + "/")}
                entries[name] = b"not a directory"
                write_archive(self.archive, entries)
                self.assert_refused("invalid_package")

    def test_windows_device_names_are_refused(self) -> None:
        for name in ("nul", "CON.txt", "com1", "lpt9.bin"):
            with self.subTest(name=name):
                self.archive_with({f"{ROOT}/model/{name}": b"x"})
                self.assert_refused("invalid_package")

    def test_duplicate_entries_are_refused_even_when_only_the_case_differs(self) -> None:
        write_archive(self.archive, [*argos_entries().items(), (f"{ROOT}/METADATA.JSON", b"{}")])

        self.assert_refused("invalid_package")

    def test_an_encrypted_entry_is_refused(self) -> None:
        name = f"{ROOT}/model/secret.bin"
        self.archive_with({name: b"x"})
        data = bytearray(self.archive.read_bytes())
        central_header = data.rfind(name.encode()) - 46  # the fixed part of a central directory header is 46 bytes
        data[central_header + 8] |= 0x1  # general purpose bit 0: the entry is encrypted
        self.archive.write_bytes(data)

        self.assert_refused("invalid_package")

    def test_a_damaged_entry_is_an_invalid_package_and_leaves_nothing_behind(self) -> None:
        self.archive.parent.mkdir(parents=True)
        with zipfile.ZipFile(self.archive, "w", zipfile.ZIP_STORED) as archive:
            for name, data in argos_entries(model_bytes=b"A" * 5000).items():
                archive.writestr(name, data)
        data = bytearray(self.archive.read_bytes())
        data[data.find(b"A" * 100) + 50] = ord("B")  # the stored weights no longer match their CRC-32
        self.archive.write_bytes(data)

        self.assert_refused("invalid_package")
        self.assert_nothing_installed()


class PackageContentTests(ImporterTestCase):
    def write_with_metadata(self, metadata: object | None, **overrides: object) -> None:
        entries = argos_entries(**overrides)  # type: ignore[arg-type]
        if metadata is None:
            del entries[f"{ROOT}/metadata.json"]
        else:
            entries[f"{ROOT}/metadata.json"] = metadata if isinstance(metadata, bytes) else json.dumps(metadata).encode("utf-8")
        write_archive(self.archive, entries)

    def test_metadata_is_required_and_must_be_a_json_object(self) -> None:
        for metadata in (None, b"{not json", b"\xff\xfe", b"[]", b'"text"', b"null", b" " * (importer.MAX_METADATA_BYTES + 1)):
            with self.subTest(metadata=metadata if metadata is None else metadata[:12]):
                self.write_with_metadata(metadata)
                self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_language_codes_must_be_valid(self) -> None:
        base = {"package_version": "1.9"}
        for extra in (
            {"from_code": "f", "to_code": "en"},
            {"from_code": "fr", "to_code": "english"},
            {"from_code": "fr", "to_code": ""},
            {"from_code": "fr", "to_code": 5},
            {"from_code": "../x", "to_code": "en"},
            {"from_code": "fr", "to_code": "en/../.."},
            {"from_code": "fr"},
            {"to_code": "en"},
            {"from_code": "en", "to_code": "EN"},
        ):
            with self.subTest(extra=extra):
                self.write_with_metadata({**base, **extra})
                self.assert_refused("invalid_package")
        self.assert_nothing_installed()

    def test_the_package_version_must_be_a_plain_string(self) -> None:
        for version in (None, 1.9, "", "1 9", "../1", "x" * 40, ["1"]):
            with self.subTest(version=version):
                self.write_with_metadata({"from_code": "fr", "to_code": "en", "package_version": version})
                self.assert_refused("invalid_package")

    def test_only_translation_packages_are_accepted(self) -> None:
        self.write_with_metadata({"from_code": "fr", "to_code": "en", "package_version": "1.9", "type": "sbd"})

        self.assert_refused("invalid_package")

    def test_the_ctranslate2_model_files_are_required(self) -> None:
        for missing in ("model/model.bin", "model/config.json"):
            with self.subTest(missing=missing):
                entries = argos_entries()
                del entries[f"{ROOT}/{missing}"]
                write_archive(self.archive, entries)
                self.assert_refused("invalid_package")

    def test_a_bpe_package_is_unsupported_not_invalid(self) -> None:
        write_archive(self.archive, argos_entries(tokenizer="bpe.model"))

        error = self.assert_refused("unsupported_package")

        self.assertIn("BPE", error.message)
        self.assert_nothing_installed()

    def test_a_package_without_any_tokenizer_is_invalid(self) -> None:
        entries = argos_entries()
        del entries[f"{ROOT}/sentencepiece.model"]
        write_archive(self.archive, entries)

        self.assert_refused("invalid_package")

    def test_an_empty_archive_is_invalid(self) -> None:
        write_archive(self.archive, {})

        self.assert_refused("invalid_package")


class FailureHandlingTests(ImporterTestCase):
    def test_staging_is_removed_when_the_install_fails_halfway(self) -> None:
        make_package(self.archive)

        with mock.patch.object(importer, "_sha256_of", side_effect=OSError("disk full")), self.assertRaises(ServiceError) as raised:
            install_package(self.archive, self.models)

        self.assertEqual(raised.exception.code, "install_failed")
        self.assertEqual(list(self.models.iterdir()), [])

    def test_staging_is_removed_when_extraction_fails_after_writing_some_files(self) -> None:
        make_package(self.archive)

        def partial_extract(archive: zipfile.ZipFile, layout: object, staging: Path) -> tuple[str, int]:
            (staging / "model").mkdir()
            (staging / "model" / "model.bin").write_bytes(b"half written")
            raise ServiceError("invalid_package", "simulated failure while streaming")

        with mock.patch.object(importer, "_extract", partial_extract), self.assertRaises(ServiceError):
            install_package(self.archive, self.models)

        self.assertEqual(list(self.models.iterdir()), [])

    def test_a_failed_replacement_keeps_the_previous_package(self) -> None:
        make_package(self.archive, model_bytes=b"old weights")
        install_package(self.archive, self.models)
        make_package(self.archive, model_bytes=b"new weights")
        real_replace = os.replace
        calls = {"count": 0}

        def flaky_replace(source: os.PathLike[str], destination: os.PathLike[str]) -> None:
            calls["count"] += 1
            if calls["count"] == 2:  # the move of the staging directory into place
                raise PermissionError("simulated: a file is in use")
            real_replace(source, destination)

        with mock.patch.object(importer.os, "replace", flaky_replace), self.assertRaises(ServiceError) as raised:
            install_package(self.archive, self.models)

        self.assertEqual(raised.exception.code, "install_failed")
        self.assertEqual((self.models / "fr_en" / "model" / "model.bin").read_bytes(), b"old weights")
        self.assertEqual(sorted(path.name for path in self.models.iterdir()), ["fr_en"])
        self.assertEqual([package.package_name for package in scan_models(self.models).packages], ["translate-fr_en"])

    def test_an_unwritable_model_directory_is_install_failed(self) -> None:
        make_package(self.archive)
        blocker = self.temp / "work"
        blocker.mkdir()
        (blocker / "models").write_text("a file where the directory should be", encoding="utf-8")

        self.assert_refused("install_failed")

    def test_error_messages_never_contain_paths_or_archive_names(self) -> None:
        marker = "SECRET-NAME-IN-ARCHIVE"
        write_archive(self.archive, [*argos_entries().items(), (f"{ROOT}/{marker}.exe", b"x")])

        error = self.assert_refused("invalid_package")

        self.assertNotIn(marker, error.message)
        self.assertNotIn(self.temp.name, error.message)


if __name__ == "__main__":
    unittest.main()
