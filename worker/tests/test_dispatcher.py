"""Tests for request dispatch and the truthful health report."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from glyphlens_worker import PROTOCOL_VERSION, __version__
from glyphlens_worker.engines.language_detection.service import LanguageDetectionService
from glyphlens_worker.engines.ocr.backend import PaddleOnnxRuntimeLoader
from glyphlens_worker.engines.translation.service import TranslationService
from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.runtime.dispatcher import Dispatcher, HandlerError
from glyphlens_worker.runtime.health import ComponentStates, health_payload
from glyphlens_worker.runtime.services import Services

from tests.support import (
    TOKEN,
    FakeRuntimeLoader,
    fake_backend_spec,
    make_package,
    make_services,
    request,
)


#: The default services use the real OCR loader, so the expected state follows what is installed here.
OCR = "not_installed" if PaddleOnnxRuntimeLoader().is_present() else "unavailable"


class DispatcherTestCase(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.models = self.temp / "models"
        self.loader = FakeRuntimeLoader()
        self.services = make_services(self.models, loader=self.loader)

    def dispatcher(self, services: Services | None = None) -> Dispatcher:
        return Dispatcher(TOKEN, services if services is not None else self.services)

    def initialized(self, services: Services | None = None) -> Dispatcher:
        dispatcher = self.dispatcher(services)
        dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))
        return dispatcher

    def install(self, dispatcher: Dispatcher, source: str, target: str) -> dict:
        archive = make_package(self.temp / f"{source}_{target}.argosmodel", source=source, target=target)
        return dispatcher.dispatch(request("models.install", {"path": str(archive)})).payload


class InitializeTests(DispatcherTestCase):
    def test_initialize_returns_versions_and_echoes_the_session_token(self) -> None:
        dispatcher = self.dispatcher()

        reply = dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertTrue(dispatcher.initialized)
        self.assertEqual(reply.payload["sessionToken"], TOKEN)
        self.assertEqual(reply.payload["workerVersion"], __version__)
        self.assertEqual(reply.payload["protocolVersion"], PROTOCOL_VERSION)
        self.assertFalse(reply.shutdown)

    def test_initialize_reports_honest_engine_states_and_none_is_ready(self) -> None:
        reply = self.dispatcher().dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertEqual(
            reply.payload["engines"],
            {"ocr": OCR, "languageDetection": "installed", "translation": "not_installed"},
        )
        self.assertNotIn("ready", reply.payload["engines"].values())

    def test_initialize_states_are_the_same_values_health_reports(self) -> None:
        dispatcher = self.dispatcher()
        initialize = dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"})).payload["engines"]

        health = dispatcher.dispatch(request("worker.health")).payload["engines"]

        self.assertEqual(initialize, health)

    def test_a_newer_minor_client_version_is_compatible(self) -> None:
        self.dispatcher().dispatch(request("worker.initialize", {"clientProtocolVersion": "1.7"}))

    def test_an_incompatible_major_version_is_refused(self) -> None:
        dispatcher = self.dispatcher()

        with self.assertRaises(HandlerError) as raised:
            dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "2.0"}))

        self.assertEqual(raised.exception.code, "unsupported_protocol_version")
        self.assertFalse(dispatcher.initialized)

    def test_invalid_initialize_payloads_are_refused(self) -> None:
        for payload in (None, {}, [], {"clientProtocolVersion": 1}, {"clientProtocolVersion": "banana"}):
            with self.subTest(payload=payload), self.assertRaises(HandlerError) as raised:
                self.dispatcher().dispatch(request("worker.initialize", payload))
            self.assertEqual(raised.exception.code, "invalid_message")

    def test_initialize_twice_is_harmless(self) -> None:
        dispatcher = self.initialized()

        reply = dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertEqual(reply.payload["sessionToken"], TOKEN)


class RequestOrderingTests(DispatcherTestCase):
    def test_requests_before_initialize_are_refused(self) -> None:
        for message_type in ("worker.health", "worker.shutdown", "ocr.detect", "models.list", "models.status", "models.install", "language.detect", "translation.translate"):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                self.dispatcher().dispatch(request(message_type, {"texts": ["secret"]}))
            self.assertEqual(raised.exception.code, "not_initialized")

    def test_unknown_message_types_are_refused_even_before_initialize(self) -> None:
        with self.assertRaises(HandlerError) as raised:
            self.dispatcher().dispatch(request("worker.reboot"))
        self.assertEqual(raised.exception.code, "unknown_message_type")

    def test_response_and_error_types_are_not_requests(self) -> None:
        for message_type in ("error", "worker.health.result", "models.list.result"):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                self.initialized().dispatch(request(message_type))
            self.assertEqual(raised.exception.code, "unknown_message_type")


class HealthAndShutdownTests(DispatcherTestCase):
    def test_health_after_initialize_is_the_truthful_report(self) -> None:
        payload = self.initialized().dispatch(request("worker.health")).payload

        self.assertEqual(payload["process"], "ready")
        self.assertEqual(payload["ipc"], "connected")
        self.assertEqual(payload["protocol"], "initialized")
        self.assertEqual(
            payload["engines"],
            {"ocr": OCR, "languageDetection": "installed", "translation": "not_installed"},
        )
        self.assertEqual(payload["models"], "not_installed")
        self.assertEqual(payload["workerVersion"], __version__)
        self.assertEqual(payload["protocolVersion"], PROTOCOL_VERSION)
        self.assertIsInstance(payload["pid"], int)

    def test_health_never_marks_an_engine_ready_just_because_the_worker_is_alive(self) -> None:
        payload = health_payload(ipc="connected", protocol="initialized", states=ComponentStates("installed", "installed", "installed", "installed"))

        self.assertEqual(payload["process"], "ready")
        self.assertNotIn("ready", payload["engines"].values())
        self.assertEqual(payload["engines"]["ocr"], "installed")

    def test_component_states_are_passed_through_as_wire_values(self) -> None:
        payload = health_payload(ipc="disconnected", protocol="not_initialized", states=ComponentStates("unavailable", "not_installed", "unavailable", "not_installed"))

        self.assertEqual(payload["engines"], {"ocr": "not_installed", "languageDetection": "unavailable", "translation": "not_installed"})
        self.assertEqual(payload["models"], "unavailable")

    def test_states_follow_what_the_engines_have_done(self) -> None:
        dispatcher = self.initialized()
        self.install(dispatcher, "de", "en")

        installed = dispatcher.dispatch(request("worker.health")).payload
        dispatcher.dispatch(request("models.list"))
        dispatcher.dispatch(request("language.detect", {"texts": ["Hallo Welt"]}))
        ready = dispatcher.dispatch(request("worker.health")).payload

        self.assertEqual((installed["engines"]["translation"], installed["engines"]["languageDetection"], installed["models"]), ("installed", "installed", "installed"))
        self.assertEqual((ready["engines"]["translation"], ready["engines"]["languageDetection"], ready["models"]), ("ready", "ready", "installed"))

    def test_missing_libraries_are_reported_unavailable(self) -> None:
        services = Services(
            LanguageDetectionService([fake_backend_spec(module="glyphlens_no_such_module")]),
            TranslationService(ModelsLocation(self.models), FakeRuntimeLoader(present=False)),
        )

        payload = self.initialized(services).dispatch(request("worker.health")).payload

        # Chinese, Japanese and Korean are identified by script rules, so detection never needs a library for them.
        self.assertEqual(payload["engines"], {"ocr": OCR, "languageDetection": "installed", "translation": "unavailable"})

    def test_health_imports_nothing_and_creates_no_directory(self) -> None:
        dispatcher = self.initialized()

        for _ in range(3):
            dispatcher.dispatch(request("worker.health"))

        self.assertEqual(self.loader.load_calls, 0)
        self.assertFalse(self.models.exists())

    def test_shutdown_acknowledges_and_requests_exit(self) -> None:
        reply = self.initialized().dispatch(request("worker.shutdown"))

        self.assertTrue(reply.shutdown)
        self.assertEqual(reply.payload, {"status": "stopping"})

    def test_cancel_stays_reserved_and_answers_with_an_error_not_a_fake_result(self) -> None:
        for message_type in ("translation.cancel",):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                self.initialized().dispatch(request(message_type, {"text": "secret"}))
            self.assertEqual(raised.exception.code, "engine_unavailable")
            self.assertNotIn("secret", raised.exception.message)


class TranslationMessageTests(DispatcherTestCase):
    def test_the_five_messages_are_answered_with_their_result_payloads(self) -> None:
        dispatcher = self.initialized()

        installed = self.install(dispatcher, "fr", "en")
        listing = dispatcher.dispatch(request("models.list")).payload
        status = dispatcher.dispatch(request("models.status", {"source": "fr", "target": "en"})).payload
        translation = dispatcher.dispatch(
            request("translation.translate", {"targetLanguage": "en", "items": [{"id": "0", "text": "Bonjour", "sourceLanguage": "fr"}]})
        ).payload
        detection = dispatcher.dispatch(request("language.detect", {"texts": ["Hallo Welt"], "maxAlternatives": 1})).payload

        self.assertEqual((installed["package"]["packageName"], installed["replaced"]), ("translate-fr_en", False))
        self.assertEqual((listing["state"], [p["packageName"] for p in listing["packages"]]), ("installed", ["translate-fr_en"]))
        self.assertEqual((status["available"], status["route"], status["issue"]), (True, ["fr", "en"], "none"))
        self.assertEqual(translation["results"][0]["translatedText"], "[fr>en] Bonjour")
        self.assertEqual(translation["results"][0]["modelVersion"], status["modelVersion"])
        self.assertEqual(detection["results"][0]["language"], "de")
        self.assertEqual(len(detection["results"][0]["alternatives"]), 1)

    def test_every_payload_is_plain_json(self) -> None:
        dispatcher = self.initialized()
        self.install(dispatcher, "fr", "en")
        requests = [
            ("models.list", None),
            ("models.status", {"source": "fr", "target": "en"}),
            ("translation.translate", {"targetLanguage": "en", "items": [{"id": "0", "text": "Bonjour", "sourceLanguage": "fr"}]}),
            ("language.detect", {"texts": ["Hallo Welt"]}),
        ]

        for message_type, payload in requests:
            with self.subTest(message_type=message_type):
                result = dispatcher.dispatch(request(message_type, payload)).payload
                self.assertEqual(json.loads(json.dumps(result)), result)

    def test_service_errors_become_handler_errors_with_the_same_code_and_message(self) -> None:
        dispatcher = self.initialized()
        cases = [
            ("language.detect", {"texts": "x"}, "invalid_request"),
            ("translation.translate", {"targetLanguage": "en"}, "invalid_request"),
            ("models.status", {"source": "de"}, "invalid_request"),
            ("models.list", "x", "invalid_request"),
            ("models.install", {"path": "relative.argosmodel"}, "invalid_request"),
            ("models.install", {"path": str(self.temp / "missing.argosmodel")}, "package_not_found"),
        ]

        for message_type, payload, code in cases:
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                dispatcher.dispatch(request(message_type, payload))
            self.assertEqual(raised.exception.code, code)
            self.assertTrue(raised.exception.message)

    def test_invalid_and_unsupported_packages_have_their_own_codes(self) -> None:
        dispatcher = self.initialized()
        bpe = make_package(self.temp / "es.argosmodel", source="es", target="en", tokenizer="bpe.model")
        junk = self.temp / "junk.argosmodel"
        junk.write_bytes(b"junk" * 50)

        for path, code in ((bpe, "unsupported_package"), (junk, "invalid_package")):
            with self.subTest(code=code), self.assertRaises(HandlerError) as raised:
                dispatcher.dispatch(request("models.install", {"path": str(path)}))
            self.assertEqual(raised.exception.code, code)

    def test_engine_unavailable_is_a_request_error_for_translation(self) -> None:
        services = Services(
            LanguageDetectionService([fake_backend_spec(module="glyphlens_no_such_module")]),
            TranslationService(ModelsLocation(self.models), FakeRuntimeLoader(present=False)),
        )
        dispatcher = self.initialized(services)
        requests = [
            ("translation.translate", {"targetLanguage": "en", "items": [{"id": "0", "text": "x", "sourceLanguage": "fr"}]}),
        ]

        for message_type, payload in requests:
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                dispatcher.dispatch(request(message_type, payload))
            self.assertEqual(raised.exception.code, "engine_unavailable")
            self.assertIn("pip install", raised.exception.message)

    def test_models_list_and_status_report_a_missing_runtime_in_their_payload_not_as_an_error(self) -> None:
        services = Services(LanguageDetectionService([fake_backend_spec()]), TranslationService(ModelsLocation(self.models), FakeRuntimeLoader(present=False)))
        dispatcher = self.initialized(services)

        listing = dispatcher.dispatch(request("models.list")).payload
        status = dispatcher.dispatch(request("models.status", {"source": "de", "target": "en"})).payload

        self.assertEqual(listing["state"], "unavailable")
        self.assertEqual((status["available"], status["issue"]), (False, "engine_unavailable"))

    def test_no_payload_text_appears_in_any_error_message(self) -> None:
        dispatcher = self.initialized()
        secret = "TOP-SECRET-SCREEN-TEXT"
        attempts = [
            ("language.detect", {"texts": [secret * 500]}),
            ("language.detect", {"texts": [secret], "maxAlternatives": secret}),
            ("translation.translate", {"targetLanguage": secret, "items": []}),
            ("translation.translate", {"targetLanguage": "en", "items": [{"id": secret, "text": secret, "sourceLanguage": secret}]}),
            ("translation.translate", {"targetLanguage": "en", "items": [{"id": "a", "text": {"x": secret}, "sourceLanguage": "fr"}]}),
            ("models.status", {"source": secret, "target": "en"}),
            ("models.install", {"path": secret}),
            ("models.install", {"path": str(self.temp / f"{secret}.argosmodel")}),
        ]

        for message_type, payload in attempts:
            with self.subTest(message_type=message_type, payload=str(payload)[:30]), self.assertRaises(HandlerError) as raised:
                dispatcher.dispatch(request(message_type, payload))
            self.assertNotIn(secret, raised.exception.message)


if __name__ == "__main__":
    unittest.main()
