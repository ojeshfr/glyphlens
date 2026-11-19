"""Tests for request dispatch and the truthful health report."""

from __future__ import annotations

import unittest

from glyphlens_worker import PROTOCOL_VERSION, __version__
from glyphlens_worker.protocols.envelope import Envelope
from glyphlens_worker.runtime.dispatcher import Dispatcher, HandlerError
from glyphlens_worker.runtime.health import health_payload

from datetime import datetime, timezone

TOKEN = "a" * 64


def request(message_type: str, payload: object = None) -> Envelope:
    return Envelope("1.0", "req-1", message_type, payload, datetime(2026, 1, 1, tzinfo=timezone.utc))


def initialized() -> Dispatcher:
    dispatcher = Dispatcher(TOKEN)
    dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))
    return dispatcher


class InitializeTests(unittest.TestCase):
    def test_initialize_returns_versions_and_echoes_the_session_token(self) -> None:
        dispatcher = Dispatcher(TOKEN)

        reply = dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertTrue(dispatcher.initialized)
        self.assertEqual(reply.payload["sessionToken"], TOKEN)
        self.assertEqual(reply.payload["workerVersion"], __version__)
        self.assertEqual(reply.payload["protocolVersion"], PROTOCOL_VERSION)
        self.assertFalse(reply.shutdown)

    def test_initialize_reports_no_engine_as_ready(self) -> None:
        reply = Dispatcher(TOKEN).dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertEqual(set(reply.payload["engines"].values()), {"not_implemented"})

    def test_a_newer_minor_client_version_is_compatible(self) -> None:
        Dispatcher(TOKEN).dispatch(request("worker.initialize", {"clientProtocolVersion": "1.7"}))

    def test_an_incompatible_major_version_is_refused(self) -> None:
        dispatcher = Dispatcher(TOKEN)

        with self.assertRaises(HandlerError) as raised:
            dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "2.0"}))

        self.assertEqual(raised.exception.code, "unsupported_protocol_version")
        self.assertFalse(dispatcher.initialized)

    def test_invalid_initialize_payloads_are_refused(self) -> None:
        for payload in (None, {}, [], {"clientProtocolVersion": 1}, {"clientProtocolVersion": "banana"}):
            with self.subTest(payload=payload), self.assertRaises(HandlerError) as raised:
                Dispatcher(TOKEN).dispatch(request("worker.initialize", payload))
            self.assertEqual(raised.exception.code, "invalid_message")

    def test_initialize_twice_is_harmless(self) -> None:
        dispatcher = initialized()

        reply = dispatcher.dispatch(request("worker.initialize", {"clientProtocolVersion": "1.0"}))

        self.assertEqual(reply.payload["sessionToken"], TOKEN)


class RequestOrderingTests(unittest.TestCase):
    def test_requests_before_initialize_are_refused(self) -> None:
        for message_type in ("worker.health", "worker.shutdown", "ocr.detect", "models.list"):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                Dispatcher(TOKEN).dispatch(request(message_type))
            self.assertEqual(raised.exception.code, "not_initialized")

    def test_unknown_message_types_are_refused_even_before_initialize(self) -> None:
        with self.assertRaises(HandlerError) as raised:
            Dispatcher(TOKEN).dispatch(request("worker.reboot"))
        self.assertEqual(raised.exception.code, "unknown_message_type")

    def test_response_and_error_types_are_not_requests(self) -> None:
        for message_type in ("error", "worker.health.result"):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                initialized().dispatch(request(message_type))
            self.assertEqual(raised.exception.code, "unknown_message_type")


class HealthAndShutdownTests(unittest.TestCase):
    def test_health_after_initialize_is_the_truthful_report(self) -> None:
        payload = initialized().dispatch(request("worker.health")).payload

        self.assertEqual(payload["process"], "ready")
        self.assertEqual(payload["ipc"], "connected")
        self.assertEqual(payload["protocol"], "initialized")
        self.assertEqual(
            payload["engines"],
            {"ocr": "not_implemented", "languageDetection": "not_implemented", "translation": "not_implemented"},
        )
        self.assertEqual(payload["models"], "not_installed")
        self.assertEqual(payload["workerVersion"], __version__)
        self.assertEqual(payload["protocolVersion"], PROTOCOL_VERSION)
        self.assertIsInstance(payload["pid"], int)

    def test_health_never_marks_an_engine_ready_just_because_the_worker_is_alive(self) -> None:
        payload = health_payload(ipc="connected", protocol="initialized")

        self.assertEqual(payload["process"], "ready")
        self.assertNotIn("ready", payload["engines"].values())
        self.assertNotEqual(payload["models"], "installed")

    def test_shutdown_acknowledges_and_requests_exit(self) -> None:
        reply = initialized().dispatch(request("worker.shutdown"))

        self.assertTrue(reply.shutdown)
        self.assertEqual(reply.payload, {"status": "stopping"})

    def test_unimplemented_engines_answer_with_an_error_not_a_fake_result(self) -> None:
        for message_type in ("ocr.detect", "translation.translate", "translation.cancel", "models.list", "models.status"):
            with self.subTest(message_type=message_type), self.assertRaises(HandlerError) as raised:
                initialized().dispatch(request(message_type, {"text": "secret"}))
            self.assertEqual(raised.exception.code, "engine_unavailable")
            self.assertNotIn("secret", raised.exception.message)


if __name__ == "__main__":
    unittest.main()
