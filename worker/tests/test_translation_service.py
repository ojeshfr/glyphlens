"""Tests for the translation service over a fake runtime: routing, batching, per-item results, limits and states."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path
from typing import Any

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.translation.service import TranslationService
from glyphlens_worker.errors import ServiceError

from tests.support import FakeRuntime, FakeRuntimeLoader, make_package


def item(item_id: str, text: str, source: str) -> dict[str, str]:
    return {"id": item_id, "text": text, "sourceLanguage": source}


class ServiceTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.models = self.temp / "models"
        self.runtime = FakeRuntime()
        self.loader = FakeRuntimeLoader(self.runtime)
        self.service = TranslationService(ModelsLocation(self.models), self.loader)

    def install(self, source: str, target: str, **kwargs: object) -> dict[str, Any]:
        archive = make_package(self.temp / f"{source}_{target}.argosmodel", source=source, target=target, **kwargs)
        return self.service.install({"path": str(archive)})

    def translate(self, target: str, *items: dict[str, str]) -> list[dict[str, Any]]:
        return self.service.translate({"targetLanguage": target, "items": list(items)})["results"]

    def model_sha(self, directory: str) -> str:
        manifest = json.loads((self.models / directory / "glyphlens-model.json").read_text(encoding="utf-8"))
        return manifest["modelSha256"]

    def assert_service_error(self, code: str, call: Any, *args: Any) -> ServiceError:
        with self.assertRaises(ServiceError) as raised:
            call(*args)
        self.assertEqual(raised.exception.code, code, raised.exception.message)
        return raised.exception


class TranslateTests(ServiceTestCase):
    def test_a_translation_has_the_protocol_shape(self) -> None:
        self.install("fr", "en")

        response = self.service.translate({"targetLanguage": "en", "items": [item("r1", "Bonjour le monde", "fr")]})

        self.assertEqual(response["engine"], "ct2-9.9.9")
        self.assertEqual(
            response["results"],
            [
                {
                    "id": "r1",
                    "status": "ok",
                    "translatedText": "[fr>en] Bonjour le monde",
                    "sourceLanguage": "fr",
                    "targetLanguage": "en",
                    "modelVersion": f"ct2-9.9.9/seg4|argos:fr-en:1.9:{self.model_sha('fr_en')[:12]}",
                    "route": ["fr", "en"],
                }
            ],
        )
        self.assertEqual(json.loads(json.dumps(response)), response)  # plain JSON data

    def test_all_segments_of_a_request_go_through_one_batched_call_per_hop(self) -> None:
        self.install("fr", "en")

        results = self.translate(
            "en",
            item("a", "Un. Deux.", "fr"),
            item("b", "Trois\nQuatre", "fr"),
            item("c", "Cinq", "fr"),
        )

        self.assertEqual(self.runtime.translators["fr_en"].batches, [["Un.", "Deux.", "Trois", "Quatre", "Cinq"]])
        self.assertEqual(
            [result["translatedText"] for result in results],
            ["[fr>en] Un. [fr>en] Deux.", "[fr>en] Trois\n[fr>en] Quatre", "[fr>en] Cinq"],
        )

    def test_results_keep_the_order_and_ids_of_the_items(self) -> None:
        self.install("fr", "en")
        self.install("de", "en")

        results = self.translate("en", item("z", "Bonjour", "fr"), item("a", "Hallo", "de"), item("m", "Salut", "fr"))

        self.assertEqual([result["id"] for result in results], ["z", "a", "m"])
        self.assertEqual([result["route"] for result in results], [["fr", "en"], ["de", "en"], ["fr", "en"]])
        self.assertEqual(self.runtime.translators["fr_en"].batches, [["Bonjour", "Salut"]])  # items of one route share a batch
        self.assertEqual(self.runtime.translators["de_en"].batches, [["Hallo"]])

    def test_a_pivot_route_runs_both_hops_and_feeds_the_english_text_to_the_second(self) -> None:
        self.install("de", "en")
        self.install("en", "fr")

        results = self.translate("fr", item("p", "Hallo Welt\nGuten Tag", "de"))

        self.assertEqual(results[0]["translatedText"], "[en>fr] [de>en] Hallo Welt\n[en>fr] [de>en] Guten Tag")
        self.assertEqual(results[0]["route"], ["de", "en", "fr"])
        self.assertEqual(
            results[0]["modelVersion"],
            f"ct2-9.9.9/seg4|argos:de-en:1.9:{self.model_sha('de_en')[:12]}>argos:en-fr:1.9:{self.model_sha('en_fr')[:12]}",
        )
        self.assertEqual(self.runtime.translators["de_en"].batches, [["Hallo Welt", "Guten Tag"]])
        self.assertEqual(self.runtime.translators["en_fr"].batches, [["[de>en] Hallo Welt", "[de>en] Guten Tag"]])

    def test_a_three_hop_route_is_model_missing(self) -> None:
        self.install("ja", "ko")
        self.install("ko", "en")
        self.install("en", "fr")

        error = self.translate("fr", item("x", "text", "ja"))[0]["error"]

        self.assertEqual(error["code"], "model_missing")

    def test_the_item_failures_are_reported_per_item_with_the_documented_codes(self) -> None:
        self.install("fr", "en")

        results = self.translate(
            "en",
            item("ok", "Bonjour", "fr"),
            item("empty", "  \n\t ", "fr"),
            item("long", "x" * 4097, "fr"),
            item("same", "Hello", "en"),
            item("unsupported", "text", "xx"),
            item("missing", "text", "ja"),
            item("ok2", "Salut", "fr"),
        )

        self.assertEqual([result["status"] for result in results], ["ok", "error", "error", "error", "error", "error", "ok"])
        codes = {result["id"]: result["error"] for result in results if result["status"] == "error"}
        self.assertEqual(codes["empty"]["code"], "empty_text")
        self.assertEqual(codes["long"]["code"], "text_too_long")
        self.assertEqual(codes["same"]["code"], "same_language")
        self.assertEqual(codes["unsupported"]["code"], "unsupported_language_pair")
        self.assertEqual(codes["missing"], {"code": "model_missing", "message": "No installed model translates ja to en.", "requiredPackage": "translate-ja_en"})
        for code in ("empty", "long", "same", "unsupported"):
            self.assertNotIn("requiredPackage", codes[code])
        self.assertEqual(self.runtime.translators["fr_en"].batches, [["Bonjour", "Salut"]])

    def test_a_text_of_exactly_the_limit_is_accepted(self) -> None:
        self.install("fr", "en")

        result = self.translate("en", item("edge", "x" * 4096, "fr"))[0]

        self.assertEqual(result["status"], "ok")

    def test_the_limit_counts_code_points_not_utf16_units(self) -> None:
        self.install("fr", "en")

        result = self.translate("en", item("emoji", "\U0001F600" * 4096, "fr"))[0]  # 8192 UTF-16 units, 4096 code points

        self.assertEqual(result["status"], "ok")

    def test_a_failing_text_fails_alone_and_the_others_are_translated(self) -> None:
        self.runtime.fail_on = ("BOOM",)
        self.install("fr", "en")

        results = self.translate("en", item("a", "fine one", "fr"), item("b", "BOOM secret text", "fr"), item("c", "fine two", "fr"))

        self.assertEqual([result["status"] for result in results], ["ok", "error", "ok"])
        self.assertEqual(results[1]["error"], {"code": "translation_failed", "message": "The translation failed."})
        self.assertEqual(results[0]["translatedText"], "[fr>en] fine one")
        self.assertNotIn("BOOM", json.dumps(results[1]))

    def test_a_text_that_fails_in_the_first_hop_is_not_sent_to_the_second(self) -> None:
        self.runtime.fail_on = ("BOOM",)
        self.install("de", "en")
        self.install("en", "fr")

        results = self.translate("fr", item("a", "BOOM", "de"), item("b", "gut", "de"))

        self.assertEqual([result["status"] for result in results], ["error", "ok"])
        self.assertEqual(results[1]["translatedText"], "[en>fr] [de>en] gut")
        self.assertEqual(self.runtime.translators["en_fr"].batches, [["[de>en] gut"]])

    def test_a_model_that_cannot_be_loaded_fails_its_items_only(self) -> None:
        self.install("fr", "en")
        self.install("de", "en")
        self.runtime.fail_loading = ("de_en",)

        results = self.translate("en", item("a", "Bonjour", "fr"), item("b", "Hallo", "de"))

        self.assertEqual(results[0]["status"], "ok")
        self.assertEqual(results[1]["error"]["code"], "translation_failed")
        self.assertIn("could not be loaded", results[1]["error"]["message"])
        self.assertNotIn("secret", json.dumps(results))

    def test_the_second_request_reuses_the_loaded_translator(self) -> None:
        self.install("fr", "en")

        self.translate("en", item("a", "Un", "fr"))
        self.translate("en", item("b", "Deux", "fr"))

        self.assertEqual(self.runtime.loaded, ["fr_en"])

    def test_at_most_two_translators_stay_loaded_and_the_least_recently_used_is_dropped(self) -> None:
        for source in ("fr", "de", "it"):
            self.install(source, "en")

        for source in ("fr", "de", "fr", "it", "de"):
            self.translate("en", item("x", "text", source))

        # fr, de loaded; fr touched; it loads and evicts de (least recently used); de loads again and evicts fr.
        self.assertEqual(self.runtime.loaded, ["fr_en", "de_en", "it_en", "de_en"])

    def test_installing_a_new_version_releases_and_reloads_the_translator(self) -> None:
        self.install("fr", "en", model_bytes=b"first weights")
        first = self.translate("en", item("a", "Un", "fr"))[0]["modelVersion"]

        self.install("fr", "en", model_bytes=b"second weights")
        second = self.translate("en", item("a", "Un", "fr"))[0]["modelVersion"]

        self.assertEqual(self.runtime.loaded, ["fr_en", "fr_en"])
        self.assertNotEqual(first, second)

    def test_a_portuguese_request_uses_a_brazilian_package(self) -> None:
        self.install("pt_BR", "en")

        result = self.translate("en", item("a", "Olá mundo", "pt"))[0]

        self.assertEqual((result["sourceLanguage"], result["route"]), ("pt", ["pb", "en"]))
        self.assertIn("argos:pb-en:", result["modelVersion"])

    def test_bcp47_codes_are_mapped_to_argos_codes(self) -> None:
        self.install("pt_BR", "en")

        result = self.service.translate({"targetLanguage": "en-US", "items": [item("a", "Olá", "pt-BR")]})["results"][0]

        self.assertEqual((result["status"], result["sourceLanguage"], result["targetLanguage"]), ("ok", "pb", "en"))

    def test_nothing_installed_means_model_missing_for_every_item(self) -> None:
        results = self.translate("en", item("a", "Bonjour", "fr"), item("b", "Hallo", "de"))

        self.assertEqual([result["error"]["requiredPackage"] for result in results], ["translate-fr_en", "translate-de_en"])
        self.assertEqual(self.runtime.loaded, [])

    def test_translation_is_deterministic_and_does_not_touch_the_network(self) -> None:
        self.install("fr", "en")

        first = self.translate("en", item("a", "Bonjour. Salut", "fr"))
        second = self.translate("en", item("a", "Bonjour. Salut", "fr"))

        self.assertEqual(first, second)


class RequestValidationTests(ServiceTestCase):
    def invalid(self, payload: object) -> ServiceError:
        return self.assert_service_error("invalid_request", self.service.translate, payload)

    def test_the_payload_must_be_an_object_with_a_target_and_items(self) -> None:
        for payload in (None, [], "text", 5, {}, {"items": []}, {"targetLanguage": "en"}, {"targetLanguage": "en", "items": "x"}, {"targetLanguage": "en", "items": {}}):
            with self.subTest(payload=payload):
                self.invalid(payload)

    def test_the_target_language_must_be_a_language_code(self) -> None:
        for target in (None, 5, "", "EN", "english", "e", "en_US", "en-", "én"):
            with self.subTest(target=target):
                self.invalid({"targetLanguage": target, "items": []})

    def test_more_than_32_items_are_refused_and_32_are_accepted(self) -> None:
        self.install("fr", "en")
        too_many = [item(str(n), "texte", "fr") for n in range(33)]

        self.invalid({"targetLanguage": "en", "items": too_many})

        self.assertEqual(len(self.translate("en", *too_many[:32])), 32)

    def test_an_empty_item_list_is_an_empty_result(self) -> None:
        self.assertEqual(self.translate("en"), [])

    def test_items_must_be_well_formed(self) -> None:
        good = item("a", "texte", "fr")
        bad_items = [
            "text",
            None,
            [],
            {**good, "id": None},
            {**good, "id": ""},
            {**good, "id": 5},
            {**good, "id": "x" * 65},
            {**good, "text": None},
            {**good, "text": 5},
            {**good, "text": ["a"]},
            {**good, "sourceLanguage": None},
            {**good, "sourceLanguage": "FR"},
            {**good, "sourceLanguage": 5},
            {key: value for key, value in good.items() if key != "sourceLanguage"},
            {key: value for key, value in good.items() if key != "id"},
            {key: value for key, value in good.items() if key != "text"},
        ]
        for bad in bad_items:
            with self.subTest(item=bad):
                self.invalid({"targetLanguage": "en", "items": [good, bad]})

    def test_text_that_is_not_valid_unicode_is_refused(self) -> None:
        self.invalid({"targetLanguage": "en", "items": [item("a", "broken \ud800 text", "fr")]})

    def test_ids_of_64_characters_are_accepted_and_duplicates_are_refused(self) -> None:
        self.install("fr", "en")

        self.assertEqual(self.translate("en", item("x" * 64, "texte", "fr"))[0]["id"], "x" * 64)
        self.invalid({"targetLanguage": "en", "items": [item("same", "un", "fr"), item("same", "deux", "fr")]})

    def test_a_malformed_request_is_refused_before_anything_is_loaded(self) -> None:
        self.invalid({"targetLanguage": "en", "items": [item("a", "texte", "fr"), item("a", "autre", "fr")]})

        self.assertEqual(self.loader.load_calls, 0)

    def test_error_messages_name_fields_but_never_repeat_values(self) -> None:
        secret = "TOP-SECRET-SCREEN-TEXT"
        payloads = [
            {"targetLanguage": secret, "items": []},
            {"targetLanguage": "en", "items": [item("a", "x", secret)]},
            {"targetLanguage": "en", "items": [item(secret * 4, "x", "fr")]},
            {"targetLanguage": "en", "items": [item(secret, "x", "fr"), item(secret, secret, "fr")]},
            {"targetLanguage": "en", "items": [{"id": "a", "text": {"secret": secret}, "sourceLanguage": "fr"}]},
        ]
        for payload in payloads:
            with self.subTest(payload=str(payload)[:40]):
                self.assertNotIn(secret, self.invalid(payload).message)


class EngineUnavailableTests(ServiceTestCase):
    def test_a_missing_runtime_is_engine_unavailable_with_the_fix(self) -> None:
        self.loader.present = False

        error = self.assert_service_error("engine_unavailable", self.service.translate, {"targetLanguage": "en", "items": [item("a", "texte", "fr")]})

        self.assertIn("pip install", error.message)
        self.assertNotIn(str(self.temp), error.message)

    def test_a_runtime_that_fails_to_load_is_engine_unavailable_and_not_retried(self) -> None:
        self.loader.failure = "The translation runtime failed to load (ImportError). Reinstall it."
        payload = {"targetLanguage": "en", "items": [item("a", "texte", "fr")]}

        first = self.assert_service_error("engine_unavailable", self.service.translate, payload)
        self.assert_service_error("engine_unavailable", self.service.translate, payload)

        self.assertEqual(first.message, self.loader.failure)
        self.assertEqual(self.loader.load_calls, 1)

    def test_a_missing_runtime_is_looked_for_again_on_the_next_request(self) -> None:
        self.install("fr", "en")
        self.loader.present = False
        payload = {"targetLanguage": "en", "items": [item("a", "texte", "fr")]}
        self.assert_service_error("engine_unavailable", self.service.translate, payload)

        self.loader.present = True

        self.assertEqual(self.service.translate(payload)["results"][0]["status"], "ok")

    def test_an_unavailable_model_directory_is_engine_unavailable(self) -> None:
        service = TranslationService(ModelsLocation(None), self.loader)

        self.assert_service_error("engine_unavailable", service.translate, {"targetLanguage": "en", "items": [item("a", "texte", "fr")]})

    def test_a_request_is_validated_before_the_runtime_is_looked_at(self) -> None:
        self.loader.present = False

        self.assert_service_error("invalid_request", self.service.translate, {"targetLanguage": "en"})


class HealthTests(ServiceTestCase):
    def test_without_the_runtime_translation_is_unavailable(self) -> None:
        self.loader.present = False

        self.assertEqual(self.service.health().translation, "unavailable")

    def test_with_the_runtime_and_no_package_translation_is_not_installed(self) -> None:
        health = self.service.health()

        self.assertEqual((health.translation, health.models), ("not_installed", "not_installed"))

    def test_with_a_package_it_is_installed_until_something_loads_the_runtime(self) -> None:
        self.install("fr", "en")

        health = self.service.health()

        self.assertEqual((health.translation, health.models), ("installed", "installed"))
        self.assertEqual(self.loader.load_calls, 0)  # health never loads anything

    def test_listing_the_models_makes_the_engine_ready(self) -> None:
        self.install("fr", "en")

        self.service.list_models(None)

        self.assertEqual(self.service.health().translation, "ready")

    def test_a_translation_makes_the_engine_ready(self) -> None:
        self.install("fr", "en")

        self.translate("en", item("a", "texte", "fr"))

        self.assertEqual(self.service.health().translation, "ready")

    def test_a_loaded_runtime_without_packages_is_not_ready(self) -> None:
        self.service.list_models(None)

        self.assertEqual(self.service.health().translation, "not_installed")

    def test_a_runtime_that_failed_to_load_stays_unavailable(self) -> None:
        self.install("fr", "en")
        self.loader.failure = "failed"
        self.service.list_models(None)

        self.assertEqual(self.service.health().translation, "unavailable")

    def test_an_unreadable_model_directory_is_reported_as_models_unavailable(self) -> None:
        self.models.write_text("a file", encoding="utf-8")
        self.assertEqual(self.service.health().models, "unavailable")
        self.assertEqual(TranslationService(ModelsLocation(None), self.loader).health().models, "unavailable")

    def test_health_makes_no_import_and_no_directory(self) -> None:
        self.service.health()

        self.assertEqual(self.loader.load_calls, 0)
        self.assertFalse(self.models.exists())


class ListModelsTests(ServiceTestCase):
    def test_an_empty_directory_lists_no_packages(self) -> None:
        listing = self.service.list_models(None)

        self.assertEqual(
            listing,
            {"engine": "ct2-argos", "engineVersion": "9.9.9", "state": "not_installed", "storagePath": str(self.models), "packages": [], "problems": []},
        )

    def test_packages_are_listed_with_the_protocol_fields(self) -> None:
        self.install("de", "en", version="1.9")

        listing = self.service.list_models({})

        self.assertEqual(listing["state"], "installed")
        self.assertEqual(
            listing["packages"],
            [{"from": "de", "to": "en", "fromName": "DE", "toName": "EN", "version": "1.9", "packageName": "translate-de_en"}],
        )

    def test_a_missing_runtime_is_unavailable_with_the_reason_and_the_packages_still_listed(self) -> None:
        self.install("de", "en")
        self.loader.present = False

        listing = self.service.list_models(None)

        self.assertEqual(listing["state"], "unavailable")
        self.assertIsNone(listing["engineVersion"])
        self.assertEqual(len(listing["packages"]), 1)
        self.assertTrue(any("pip install" in problem for problem in listing["problems"]))

    def test_without_a_model_directory_the_state_is_unavailable_and_says_why(self) -> None:
        service = TranslationService(ModelsLocation(None, ("The default model directory is unavailable because LOCALAPPDATA is not set.",)), self.loader)

        listing = service.list_models(None)

        self.assertEqual((listing["state"], listing["storagePath"], listing["packages"]), ("unavailable", None, []))
        self.assertIn("LOCALAPPDATA", listing["problems"][0])

    def test_problems_of_the_settings_and_of_broken_packages_are_reported(self) -> None:
        self.install("de", "en")
        (self.models / "junk").mkdir()
        service = TranslationService(ModelsLocation(self.models, ("GLYPHLENS_MODELS_DIR was ignored.",)), self.loader)

        problems = service.list_models(None)["problems"]

        self.assertEqual(len(problems), 2)
        self.assertIn("GLYPHLENS_MODELS_DIR", problems[0])
        self.assertIn("junk", problems[1])

    def test_problems_are_bounded(self) -> None:
        for number in range(80):
            (self.models / f"broken{number}").mkdir(parents=True)

        problems = self.service.list_models(None)["problems"]

        self.assertEqual(len(problems), 50)
        self.assertTrue(all(len(problem) <= 300 for problem in problems))

    def test_a_package_the_engine_cannot_load_is_reported(self) -> None:
        self.install("fr", "en")
        self.runtime.fail_loading = ("fr_en",)
        self.translate("en", item("a", "texte", "fr"))

        problems = self.service.list_models(None)["problems"]

        self.assertEqual(len(problems), 1)
        self.assertIn("fr_en", problems[0])
        self.assertIn("OSError", problems[0])
        self.assertNotIn("secret", problems[0])

    def test_the_payload_must_be_null_or_an_object(self) -> None:
        for payload in ("x", 5, [], [1]):
            with self.subTest(payload=payload):
                self.assert_service_error("invalid_request", self.service.list_models, payload)


class RouteStatusTests(ServiceTestCase):
    def status(self, source: str, target: str) -> dict[str, Any]:
        return self.service.route_status({"source": source, "target": target})

    def test_an_installed_pair_is_available_with_its_model_version(self) -> None:
        self.install("de", "en")

        self.assertEqual(
            self.status("de", "en"),
            {
                "source": "de",
                "target": "en",
                "available": True,
                "route": ["de", "en"],
                "modelVersion": f"ct2-9.9.9/seg4|argos:de-en:1.9:{self.model_sha('de_en')[:12]}",
                "missingPackage": None,
                "issue": "none",
            },
        )

    def test_a_pivot_is_available_when_both_hops_are_installed(self) -> None:
        self.install("de", "en")
        self.install("en", "fr")

        status = self.status("de", "fr")

        self.assertEqual((status["available"], status["route"]), (True, ["de", "en", "fr"]))
        self.assertIn(">argos:en-fr:", status["modelVersion"])

    def test_a_missing_package_is_named(self) -> None:
        self.install("de", "en")

        status = self.status("ja", "en")

        self.assertEqual(
            status,
            {"source": "ja", "target": "en", "available": False, "route": [], "modelVersion": None, "missingPackage": "translate-ja_en", "issue": "model_missing"},
        )
        self.assertEqual(self.status("de", "fr")["missingPackage"], "translate-en_fr")

    def test_the_same_language_and_unknown_languages_are_unsupported(self) -> None:
        for source, target in (("en", "en"), ("de", "de"), ("xx", "en"), ("en", "xx")):
            with self.subTest(source=source, target=target):
                status = self.status(source, target)
                self.assertEqual((status["available"], status["issue"], status["missingPackage"]), (False, "unsupported_pair", None))

    def test_a_missing_runtime_is_engine_unavailable(self) -> None:
        self.install("de", "en")
        self.loader.present = False

        status = self.status("de", "en")

        self.assertEqual((status["available"], status["issue"], status["modelVersion"]), (False, "engine_unavailable", None))

    def test_an_unreadable_model_directory_is_engine_unavailable(self) -> None:
        service = TranslationService(ModelsLocation(None), self.loader)

        self.assertEqual(service.route_status({"source": "de", "target": "en"})["issue"], "engine_unavailable")

    def test_codes_are_mapped_to_argos_codes(self) -> None:
        self.install("pt_BR", "en")

        status = self.status("pt-BR", "en-GB")

        self.assertEqual((status["source"], status["target"], status["route"]), ("pb", "en", ["pb", "en"]))

    def test_the_request_must_be_valid(self) -> None:
        for payload in (None, [], {}, {"source": "de"}, {"target": "en"}, {"source": "DE", "target": "en"}, {"source": 1, "target": "en"}):
            with self.subTest(payload=payload):
                self.assert_service_error("invalid_request", self.service.route_status, payload)


class InstallTests(ServiceTestCase):
    def test_install_returns_the_package_and_whether_it_replaced_one(self) -> None:
        first = self.install("fr", "en")
        second = self.install("fr", "en", version="2.0")

        self.assertEqual(
            first,
            {"package": {"from": "fr", "to": "en", "fromName": "FR", "toName": "EN", "version": "1.9", "packageName": "translate-fr_en"}, "replaced": False},
        )
        self.assertEqual((second["replaced"], second["package"]["version"]), (True, "2.0"))

    def test_installing_does_not_need_or_load_the_runtime(self) -> None:
        self.loader.present = False

        self.install("fr", "en")

        self.assertEqual(self.loader.load_calls, 0)
        self.assertEqual(self.service.health().models, "installed")

    def test_the_path_must_be_a_valid_request(self) -> None:
        for payload in (None, [], {}, {"path": 5}, {"path": "relative.argosmodel"}, {"path": str(self.temp / "model.zip")}, {"path": "\\\\server\\share\\m.argosmodel"}):
            with self.subTest(payload=payload):
                self.assert_service_error("invalid_request", self.service.install, payload)
        self.assertFalse(self.models.exists())

    def test_a_missing_file_is_package_not_found(self) -> None:
        self.assert_service_error("package_not_found", self.service.install, {"path": str(self.temp / "nope.argosmodel")})

    def test_without_a_model_directory_nothing_can_be_installed(self) -> None:
        service = TranslationService(ModelsLocation(None), self.loader)
        archive = make_package(self.temp / "fr.argosmodel")

        self.assert_service_error("engine_unavailable", service.install, {"path": str(archive)})

    def test_importer_errors_are_passed_on_with_their_codes(self) -> None:
        bpe = make_package(self.temp / "es.argosmodel", source="es", target="en", tokenizer="bpe.model")
        garbage = self.temp / "garbage.argosmodel"
        garbage.write_bytes(b"not a zip" * 20)

        self.assert_service_error("unsupported_package", self.service.install, {"path": str(bpe)})
        self.assert_service_error("invalid_package", self.service.install, {"path": str(garbage)})
        self.assertEqual(self.service.health().models, "not_installed")


if __name__ == "__main__":
    unittest.main()
