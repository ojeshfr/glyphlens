"""Tests for scanning the model directory and for the route finder."""

from __future__ import annotations

import builtins
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

from glyphlens_worker.engines.translation.routes import find_route, is_supported_pair, missing_package
from glyphlens_worker.languages import (
    ARGOS_LANGUAGES,
    canonical_language,
    detection_languages,
    package_name,
)
from glyphlens_worker.models.importer import install_package
from glyphlens_worker.models.manifest import MANIFEST_FILE_NAME
from glyphlens_worker.models.registry import display_directory_name, scan_models

from tests.support import fake_package, make_package


class ScanTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.models = self.temp / "models"

    def install(self, source: str, target: str, **kwargs: object) -> Path:
        archive = make_package(self.temp / f"{source}_{target}.argosmodel", source=source, target=target, **kwargs)
        install_package(archive, self.models)
        return self.models / f"{source}_{target}"

    def test_the_ocr_model_directory_is_not_a_translation_package_and_is_not_a_problem(self) -> None:
        self.install("fr", "en")
        (self.models / "ocr" / "PP-OCRv5_mobile_det_onnx").mkdir(parents=True)

        scan = scan_models(self.models)

        self.assertEqual([package.package_name for package in scan.packages], ["translate-fr_en"])
        self.assertEqual(scan.problems, ())

    def test_a_missing_directory_is_simply_empty(self) -> None:
        scan = scan_models(self.models)

        self.assertEqual((scan.packages, scan.problems, scan.readable), ((), (), True))

    def test_no_location_is_unreadable(self) -> None:
        scan = scan_models(None)

        self.assertFalse(scan.readable)
        self.assertEqual(scan.packages, ())

    def test_a_file_where_the_directory_should_be_is_unreadable(self) -> None:
        self.models.write_text("not a directory", encoding="utf-8")

        scan = scan_models(self.models)

        self.assertFalse(scan.readable)
        self.assertEqual(len(scan.problems), 1)

    def test_packages_are_listed_sorted_by_language_pair(self) -> None:
        self.install("fr", "en")
        self.install("de", "en")
        self.install("en", "fr")

        names = [package.package_name for package in scan_models(self.models).packages]

        self.assertEqual(names, ["translate-de_en", "translate-en_fr", "translate-fr_en"])

    def test_staging_and_backup_directories_are_not_packages_and_not_problems(self) -> None:
        self.install("fr", "en")
        (self.models / ".staging-abc").mkdir()
        (self.models / ".old-abc").mkdir()

        scan = scan_models(self.models)

        self.assertEqual(len(scan.packages), 1)
        self.assertEqual(scan.problems, ())

    def test_broken_directories_are_skipped_with_a_problem_and_never_fail_the_scan(self) -> None:
        good = self.install("fr", "en")
        self.install("de", "en")
        (self.models / "empty_dir").mkdir()
        (self.models / "stray.txt").write_text("hello", encoding="utf-8")
        (self.models / "de_en" / MANIFEST_FILE_NAME).write_text("{not json", encoding="utf-8")

        scan = scan_models(self.models)

        self.assertEqual([package.directory for package in scan.packages], [good])
        self.assertEqual(len(scan.problems), 3)
        self.assertTrue(all(len(problem) < 200 for problem in scan.problems))

    def test_a_directory_whose_name_does_not_match_its_manifest_is_ignored(self) -> None:
        self.install("fr", "en").rename(self.models / "xx_yy")

        scan = scan_models(self.models)

        self.assertEqual(scan.packages, ())
        self.assertIn("does not match", scan.problems[0])

    def test_a_truncated_or_missing_model_file_is_detected_without_hashing(self) -> None:
        self.install("fr", "en", model_bytes=b"weights" * 50)
        self.install("de", "en")
        (self.models / "fr_en" / "model" / "model.bin").write_bytes(b"short")
        (self.models / "de_en" / "sentencepiece.model").unlink()

        scan = scan_models(self.models)

        self.assertEqual(scan.packages, ())
        self.assertEqual(len(scan.problems), 2)
        self.assertTrue(any("model.bin" in problem for problem in scan.problems))
        self.assertTrue(any("sentencepiece.model" in problem for problem in scan.problems))

    def test_scanning_never_reads_the_model_file(self) -> None:
        self.install("fr", "en")
        opened: list[str] = []
        real_open = builtins.open

        def recording_open(file: object, *args: object, **kwargs: object):  # type: ignore[no-untyped-def]
            opened.append(str(file))
            return real_open(file, *args, **kwargs)  # type: ignore[arg-type]

        with mock.patch("builtins.open", recording_open):
            scan = scan_models(self.models)

        self.assertEqual(len(scan.packages), 1)
        self.assertTrue(opened)
        self.assertFalse(any(path.endswith("model.bin") for path in opened))

    def test_an_unreasonable_manifest_is_a_problem(self) -> None:
        directory = self.install("fr", "en")
        document = json.loads((directory / MANIFEST_FILE_NAME).read_text(encoding="utf-8"))
        for key, value in (("modelSha256", "xyz"), ("from", "../x"), ("packageVersion", ""), ("schema", 2), ("tokenizer", "bpe"), ("modelBytes", -1)):
            with self.subTest(key=key):
                (directory / MANIFEST_FILE_NAME).write_text(json.dumps({**document, key: value}), encoding="utf-8")

                self.assertEqual(scan_models(self.models).packages, ())

    def test_directory_names_in_problems_are_bounded_and_printable(self) -> None:
        self.assertEqual(display_directory_name("bad\x07name\n"), "bad?name?")
        self.assertEqual(len(display_directory_name("x" * 500)), 40)

        (self.models / ("y" * 100)).mkdir(parents=True)
        self.assertLess(len(scan_models(self.models).problems[0]), 120)

    def test_the_version_tag_is_the_documented_format(self) -> None:
        directory = self.install("fr", "en", version="1.9")
        package = scan_models(self.models).packages[0]

        manifest = json.loads((directory / MANIFEST_FILE_NAME).read_text(encoding="utf-8"))

        self.assertEqual(package.version_tag, f"argos:fr-en:1.9:{manifest['modelSha256'][:12]}")


class RouteTests(unittest.TestCase):
    def route(self, source: str, target: str, *installed: tuple[str, str]) -> list[str] | None:
        found = find_route(source, target, [fake_package(*pair) for pair in installed])
        return None if found is None else found.languages

    def test_a_direct_package_is_a_one_hop_route(self) -> None:
        self.assertEqual(self.route("de", "en", ("de", "en")), ["de", "en"])

    def test_a_direct_package_beats_a_pivot(self) -> None:
        self.assertEqual(self.route("de", "fr", ("de", "en"), ("en", "fr"), ("de", "fr")), ["de", "fr"])

    def test_two_packages_make_a_pivot_route(self) -> None:
        route = find_route("de", "fr", [fake_package("de", "en"), fake_package("en", "fr")])

        assert route is not None
        self.assertEqual(route.languages, ["de", "en", "fr"])
        self.assertEqual([package.package_name for package in route.packages], ["translate-de_en", "translate-en_fr"])

    def test_the_route_is_none_when_a_hop_is_missing(self) -> None:
        self.assertIsNone(self.route("de", "fr", ("de", "en")))
        self.assertIsNone(self.route("de", "fr", ("en", "fr")))
        self.assertIsNone(self.route("ja", "en", ("de", "en")))

    def test_a_route_of_three_hops_is_not_offered(self) -> None:
        installed = [("ja", "ko"), ("ko", "en"), ("en", "fr")]

        self.assertIsNone(self.route("ja", "fr", *installed))

    def test_the_pivot_does_not_have_to_be_english(self) -> None:
        self.assertEqual(self.route("ja", "ko", ("ja", "zh"), ("zh", "ko")), ["ja", "zh", "ko"])

    def test_english_is_the_preferred_pivot(self) -> None:
        installed = [("de", "it"), ("it", "fr"), ("de", "en"), ("en", "fr")]

        self.assertEqual(self.route("de", "fr", *installed), ["de", "en", "fr"])

    def test_a_package_is_directional(self) -> None:
        self.assertIsNone(self.route("en", "de", ("de", "en")))

    def test_a_portuguese_request_can_use_a_brazilian_package_and_the_reverse(self) -> None:
        self.assertEqual(self.route("pt", "en", ("pb", "en")), ["pb", "en"])
        self.assertEqual(self.route("pb", "en", ("pt", "en")), ["pt", "en"])
        self.assertEqual(self.route("en", "pt", ("en", "pb")), ["en", "pb"])

    def test_chinese_variants_stand_in_for_each_other(self) -> None:
        self.assertEqual(self.route("zh", "en", ("zt", "en")), ["zt", "en"])
        self.assertEqual(self.route("zt", "en", ("zh", "en")), ["zh", "en"])

    def test_an_exact_package_is_preferred_over_a_variant(self) -> None:
        self.assertEqual(self.route("pt", "en", ("pb", "en"), ("pt", "en")), ["pt", "en"])

    def test_a_variant_is_a_pivot_end_too(self) -> None:
        self.assertEqual(self.route("pt", "fr", ("pb", "en"), ("en", "fr")), ["pb", "en", "fr"])

    def test_no_installed_packages_means_no_route(self) -> None:
        self.assertIsNone(self.route("fr", "en"))


class MissingPackageTests(unittest.TestCase):
    def missing(self, source: str, target: str, *installed: tuple[str, str]) -> str:
        return missing_package(source, target, [fake_package(*pair) for pair in installed])

    def test_a_pair_with_english_names_that_pair(self) -> None:
        self.assertEqual(self.missing("ja", "en"), "translate-ja_en")
        self.assertEqual(self.missing("en", "fr"), "translate-en_fr")

    def test_other_pairs_name_the_first_missing_hop_through_english(self) -> None:
        self.assertEqual(self.missing("de", "fr"), "translate-de_en")
        self.assertEqual(self.missing("de", "fr", ("de", "en")), "translate-en_fr")

    def test_the_conventional_name(self) -> None:
        self.assertEqual(package_name("de", "en"), "translate-de_en")


class SupportedPairTests(unittest.TestCase):
    def test_known_languages_are_supported_but_not_into_themselves(self) -> None:
        self.assertTrue(is_supported_pair("de", "en"))
        self.assertTrue(is_supported_pair("pb", "en"))
        self.assertFalse(is_supported_pair("de", "de"))

    def test_unknown_languages_are_unsupported_unless_a_package_for_them_is_installed(self) -> None:
        self.assertFalse(is_supported_pair("xx", "en"))
        self.assertFalse(is_supported_pair("en", "xx"))
        self.assertTrue(is_supported_pair("xx", "en", [fake_package("xx", "en")]))


class LanguageTableTests(unittest.TestCase):
    def test_the_catalogue_is_the_49_argos_source_languages_plus_english(self) -> None:
        self.assertEqual(len(ARGOS_LANGUAGES), 50)
        self.assertEqual(ARGOS_LANGUAGES["en"], "English")
        self.assertTrue({"pb", "zt", "zh", "pt", "es", "ja", "ar", "ru"} <= set(ARGOS_LANGUAGES))

    def test_detection_covers_every_argos_language_with_variants_folded(self) -> None:
        codes = detection_languages()

        self.assertEqual(len(codes), 48)
        self.assertNotIn("pb", codes)
        self.assertNotIn("zt", codes)
        self.assertTrue({"pt", "zh", "en", "es", "fr"} <= set(codes))
        self.assertEqual(list(codes), sorted(codes))

    def test_wire_codes_map_to_argos_codes(self) -> None:
        cases = {"de": "de", "DE": "de", "pt-BR": "pb", "pt-br": "pb", "pt-PT": "pt", "zh-Hant": "zt", "zh-TW": "zt", "zh-Hans": "zh",
                 "zh-CN": "zh", "de-AT": "de", "en-US": "en", "nb-NO": "nb", "he-IL": "he", "fil": "fil"}
        for wire, argos in cases.items():
            with self.subTest(wire=wire):
                self.assertEqual(canonical_language(wire), argos)


if __name__ == "__main__":
    unittest.main()
