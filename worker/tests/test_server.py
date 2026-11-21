"""Tests for the request loop: correlation, malformed input, limits, disconnects and shutdown."""

from __future__ import annotations

import json
import struct
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path

from glyphlens_worker.protocols import envelope
from glyphlens_worker.runtime import framing, server
from glyphlens_worker.runtime.dispatcher import Dispatcher
from glyphlens_worker.runtime.server import serve
from glyphlens_worker.runtime.services import Services

from tests.streams import FakePipe, frame
from tests.support import make_package, make_services

TOKEN = "t" * 64

#: A model directory that never exists, so tests that do not install anything see an empty one.
NO_MODELS = Path(tempfile.gettempdir()) / "glyphlens-tests-no-such-models-directory"


def message(message_type: str, request_id: str = "r1", payload: object = None, version: str = "1.0") -> bytes:
    return frame(
        json.dumps(
            {
                "protocolVersion": version,
                "requestId": request_id,
                "messageType": message_type,
                "payload": payload,
                "timestamp": datetime(2026, 1, 1, tzinfo=timezone.utc).isoformat(),
            }
        ).encode("utf-8")
    )


INIT = message("worker.initialize", "init-1", {"clientProtocolVersion": "1.0"})


def run(incoming: bytes, services: Services | None = None, **pipe_args: object) -> tuple[int, list[dict], list[str]]:
    pipe = FakePipe(incoming, **pipe_args)  # type: ignore[arg-type]
    logs: list[str] = []
    code = serve(pipe, Dispatcher(TOKEN, services if services is not None else make_services(NO_MODELS)), logs.append)
    replies = [json.loads(raw) for raw in pipe.frames_written()]
    return code, replies, logs


class HandshakeAndShutdownTests(unittest.TestCase):
    def test_initialize_health_shutdown_exits_cleanly_with_correlated_responses(self) -> None:
        code, replies, _ = run(INIT + message("worker.health", "health-7") + message("worker.shutdown", "bye-9"))

        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual(
            [(r["messageType"], r["requestId"]) for r in replies],
            [("worker.initialize.result", "init-1"), ("worker.health.result", "health-7"), ("worker.shutdown.result", "bye-9")],
        )
        self.assertEqual(replies[0]["payload"]["sessionToken"], TOKEN)
        self.assertIn(replies[1]["payload"]["engines"]["ocr"], {"unavailable", "not_installed", "installed"})
        self.assertTrue(all(r["protocolVersion"] == "1.0" and r["timestamp"].endswith("Z") for r in replies))

    def test_nothing_is_read_after_shutdown(self) -> None:
        code, replies, _ = run(INIT + message("worker.shutdown", "bye") + message("worker.health", "late"))

        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual(len(replies), 2)

    def test_requests_before_initialize_get_an_error_and_the_loop_continues(self) -> None:
        code, replies, _ = run(message("worker.health", "early") + INIT + message("worker.shutdown", "bye"))

        self.assertEqual(replies[0]["messageType"], "error")
        self.assertEqual(replies[0]["requestId"], "early")
        self.assertEqual(replies[0]["payload"]["code"], "not_initialized")
        self.assertEqual(code, server.EXIT_OK)

    def test_unimplemented_engine_request_is_an_error_response(self) -> None:
        _, replies, _ = run(INIT + message("translation.cancel", "cancel-1", {"requestId": "AAAA"}) + message("worker.shutdown", "bye"))

        self.assertEqual(replies[1]["messageType"], "error")
        self.assertEqual(replies[1]["payload"]["code"], "engine_unavailable")

    def test_unknown_message_type_is_an_error_response(self) -> None:
        _, replies, _ = run(INIT + message("worker.reboot", "x") + message("worker.shutdown", "bye"))

        self.assertEqual(replies[1]["payload"]["code"], "unknown_message_type")


class MalformedInputTests(unittest.TestCase):
    def test_invalid_json_gets_an_error_with_unknown_id_and_the_worker_keeps_serving(self) -> None:
        code, replies, _ = run(INIT + frame(b"{not json") + message("worker.health", "h") + message("worker.shutdown", "bye"))

        self.assertEqual(replies[1]["messageType"], "error")
        self.assertEqual(replies[1]["requestId"], "unknown")
        self.assertEqual(replies[1]["payload"]["code"], "invalid_message")
        self.assertEqual(replies[2]["messageType"], "worker.health.result")
        self.assertEqual(code, server.EXIT_OK)

    def test_invalid_utf8_is_an_invalid_message(self) -> None:
        _, replies, _ = run(frame(b"\xff\xfe\x00garbage") + INIT + message("worker.shutdown", "bye"))

        self.assertEqual(replies[0]["payload"]["code"], "invalid_message")

    def test_a_readable_request_id_is_echoed_even_when_validation_fails(self) -> None:
        bad = frame(json.dumps({"protocolVersion": "1.0", "requestId": "keep-me", "messageType": "BAD TYPE", "payload": None, "timestamp": "2026-01-01T00:00:00Z"}).encode())

        _, replies, _ = run(bad + INIT + message("worker.shutdown", "bye"))

        self.assertEqual(replies[0]["requestId"], "keep-me")
        self.assertEqual(replies[0]["payload"]["code"], "invalid_message")

    def test_an_unsupported_major_version_gets_its_own_error_code(self) -> None:
        _, replies, _ = run(message("worker.health", "v2", version="2.0") + INIT + message("worker.shutdown", "bye"))

        self.assertEqual(replies[0]["requestId"], "v2")
        self.assertEqual(replies[0]["payload"]["code"], "unsupported_protocol_version")

    def test_a_missing_request_id_is_rejected(self) -> None:
        no_id = frame(json.dumps({"protocolVersion": "1.0", "messageType": "worker.health", "payload": None, "timestamp": "2026-01-01T00:00:00Z"}).encode())

        _, replies, _ = run(no_id + INIT + message("worker.shutdown", "bye"))

        self.assertEqual(replies[0]["payload"]["code"], "invalid_message")
        self.assertEqual(replies[0]["requestId"], "unknown")

    def test_too_many_invalid_messages_in_a_row_is_fatal(self) -> None:
        code, replies, _ = run(frame(b"nope") * server.MAX_CONSECUTIVE_INVALID + INIT)

        self.assertEqual(code, server.EXIT_PROTOCOL_ERROR)
        self.assertEqual(len(replies), server.MAX_CONSECUTIVE_INVALID)  # the initialize after them was never served

    def test_a_valid_message_resets_the_invalid_counter(self) -> None:
        rounds = server.MAX_CONSECUTIVE_INVALID - 1
        incoming = frame(b"x") * rounds + INIT + frame(b"x") * rounds + message("worker.shutdown", "bye")

        code, _, _ = run(incoming)

        self.assertEqual(code, server.EXIT_OK)


class FramingViolationTests(unittest.TestCase):
    def test_oversized_frame_is_fatal_and_reported(self) -> None:
        code, replies, _ = run(INIT + struct.pack("<I", framing.MAX_FRAME_BYTES + 1) + b"junk")

        self.assertEqual(code, server.EXIT_PROTOCOL_ERROR)
        self.assertEqual(replies[-1]["payload"]["code"], "payload_too_large")

    def test_zero_length_frame_is_fatal(self) -> None:
        code, replies, _ = run(INIT + struct.pack("<I", 0))

        self.assertEqual(code, server.EXIT_PROTOCOL_ERROR)
        self.assertEqual(replies[-1]["messageType"], "error")

    def test_truncated_frame_is_fatal(self) -> None:
        code, _, _ = run(INIT + struct.pack("<I", 100) + b"short")

        self.assertEqual(code, server.EXIT_PROTOCOL_ERROR)


class DisconnectTests(unittest.TestCase):
    def test_the_application_closing_the_pipe_without_shutdown_exits_with_pipe_lost(self) -> None:
        code, _, logs = run(INIT)

        self.assertEqual(code, server.EXIT_PIPE_LOST)
        self.assertTrue(any("closed the pipe" in line for line in logs))

    def test_a_write_failure_exits_with_pipe_lost(self) -> None:
        code, _, _ = run(INIT, fail_writes=True)

        self.assertEqual(code, server.EXIT_PIPE_LOST)

    def test_handler_bugs_become_internal_errors_not_crashes(self) -> None:
        pipe = FakePipe(INIT + message("worker.health", "h") + message("worker.shutdown", "bye"))
        dispatcher = Dispatcher(TOKEN, make_services(NO_MODELS))
        original = dispatcher.dispatch
        calls = {"n": 0}

        def flaky(request: envelope.Envelope):  # type: ignore[no-untyped-def]
            calls["n"] += 1
            if request.message_type == "worker.health":
                raise RuntimeError("boom with secret screen text")
            return original(request)

        dispatcher.dispatch = flaky  # type: ignore[method-assign]
        logs: list[str] = []

        code = serve(pipe, dispatcher, logs.append)
        replies = [json.loads(raw) for raw in pipe.frames_written()]

        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual(replies[1]["payload"]["code"], "internal_error")
        self.assertNotIn("secret", json.dumps(replies[1]))
        self.assertNotIn("secret", " ".join(logs))  # only the exception type is logged


class LoggingPrivacyTests(unittest.TestCase):
    SECRET = "TOP-SECRET-SCREEN-TEXT"

    def test_logs_contain_ids_types_status_and_timing_but_never_payload_content(self) -> None:
        _, _, logs = run(INIT + message("translation.cancel", "o-1", {"text": self.SECRET}) + message("worker.shutdown", "bye"))

        joined = "\n".join(logs)
        self.assertNotIn(self.SECRET, joined)
        self.assertIn("req=o-1", joined)
        self.assertIn("type=translation.cancel", joined)
        self.assertIn("status=error:engine_unavailable", joined)
        self.assertRegex(joined, r"ms=\d")

    def test_error_responses_never_echo_the_payload(self) -> None:
        _, replies, _ = run(INIT + message("translation.cancel", "o-1", {"text": self.SECRET}) + message("worker.shutdown", "bye"))

        self.assertNotIn(self.SECRET, json.dumps(replies))

    def test_invalid_translation_requests_log_only_the_error_code(self) -> None:
        incoming = (
            INIT
            + message("translation.translate", "t-1", {"targetLanguage": self.SECRET, "items": []})
            + message("language.detect", "d-1", {"texts": [self.SECRET * 500]})
            + message("worker.shutdown", "bye")
        )

        _, replies, logs = run(incoming)

        joined = "\n".join(logs)
        self.assertNotIn(self.SECRET, joined)
        self.assertNotIn(self.SECRET, json.dumps(replies))
        self.assertIn("status=error:invalid_request", joined)

    def test_successful_translation_and_detection_never_log_text_or_translation(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            archive = make_package(Path(temp) / "fr.argosmodel")
            incoming = (
                INIT
                + message("models.install", "i-1", {"path": str(archive)})
                + message("translation.translate", "t-1", {"targetLanguage": "en", "items": [{"id": "0", "text": self.SECRET, "sourceLanguage": "fr"}]})
                + message("language.detect", "d-1", {"texts": [self.SECRET]})
                + message("worker.shutdown", "bye")
            )

            _, replies, logs = run(incoming, make_services(Path(temp) / "models"))

        self.assertEqual([reply["messageType"] for reply in replies[1:4]], ["models.install.result", "translation.translate.result", "language.detect.result"])
        self.assertIn(self.SECRET, json.dumps(replies[2], ensure_ascii=False))  # the result carries the translation...
        joined = "\n".join(logs)
        self.assertNotIn(self.SECRET, joined)  # ...but the log never does
        self.assertIn("type=translation.translate status=ok", joined)
        self.assertIn("type=language.detect status=ok", joined)


class TranslationWireTests(unittest.TestCase):
    """The translation messages through the real request loop and the real JSON encoding."""

    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.archive = make_package(self.temp / "fr.argosmodel")
        self.services = make_services(self.temp / "models")

    def exchange(self, *messages: bytes) -> tuple[int, list[dict]]:
        code, replies, _ = run(INIT + b"".join(messages) + message("worker.shutdown", "bye"), self.services)
        return code, replies[1:-1]

    def test_install_list_status_translate_and_detect_over_the_wire(self) -> None:
        text = "Où est la gare la plus proche ? J'ai perdu mon billet."  # two sentences

        code, replies = self.exchange(
            message("models.install", "m1", {"path": str(self.archive)}),
            message("models.list", "m2"),
            message("models.status", "m3", {"source": "fr", "target": "en"}),
            message("translation.translate", "m4", {"targetLanguage": "en", "items": [{"id": "a", "text": text, "sourceLanguage": "fr"}]}),
            message("language.detect", "m5", {"texts": [text], "maxAlternatives": 2}),
        )

        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual(
            [(reply["messageType"], reply["requestId"]) for reply in replies],
            [("models.install.result", "m1"), ("models.list.result", "m2"), ("models.status.result", "m3"), ("translation.translate.result", "m4"), ("language.detect.result", "m5")],
        )
        install, listing, status, translation, detection = (reply["payload"] for reply in replies)
        self.assertEqual(set(install), {"package", "replaced"})
        self.assertEqual(set(install["package"]), {"from", "to", "fromName", "toName", "version", "packageName"})
        self.assertEqual(set(listing), {"engine", "engineVersion", "state", "storagePath", "packages", "problems"})
        self.assertEqual(set(status), {"source", "target", "available", "route", "modelVersion", "missingPackage", "issue"})
        self.assertEqual(set(translation), {"engine", "results"})
        self.assertEqual(
            set(translation["results"][0]),
            {"id", "status", "translatedText", "sourceLanguage", "targetLanguage", "modelVersion", "route"},
        )
        self.assertEqual(
            translation["results"][0]["translatedText"],
            "[fr>en] Où est la gare la plus proche ? [fr>en] J'ai perdu mon billet.",  # non-ASCII text survives the encoding
        )
        self.assertEqual(set(detection), {"detector", "results"})
        self.assertEqual(set(detection["results"][0]), {"language", "confidence", "alternatives", "issue"})

    def test_failures_are_error_responses_with_the_documented_shape_and_the_loop_continues(self) -> None:
        code, replies = self.exchange(
            message("language.detect", "e1", {"texts": 5}),
            message("models.install", "e2", {"path": str(self.temp / "missing.argosmodel")}),
            message("models.status", "e3", {"source": "x"}),
            message("worker.health", "after"),
        )

        self.assertEqual(code, server.EXIT_OK)
        self.assertEqual([(reply["messageType"], reply["payload"]["code"]) for reply in replies[:3]], [("error", "invalid_request"), ("error", "package_not_found"), ("error", "invalid_request")])
        for reply in replies[:3]:
            self.assertEqual(set(reply["payload"]), {"code", "message", "retryable"})
            self.assertFalse(reply["payload"]["retryable"])
        self.assertEqual(replies[3]["messageType"], "worker.health.result")

    def test_a_per_item_failure_is_a_normal_result_not_an_error_message(self) -> None:
        _, replies = self.exchange(
            message("translation.translate", "t", {"targetLanguage": "en", "items": [{"id": "0", "text": "texte", "sourceLanguage": "ja"}]}),
        )

        result = replies[0]
        self.assertEqual(result["messageType"], "translation.translate.result")
        self.assertEqual(
            result["payload"]["results"][0],
            {"id": "0", "status": "error", "error": {"code": "model_missing", "message": "No installed model translates ja to en.", "requiredPackage": "translate-ja_en"}},
        )


if __name__ == "__main__":
    unittest.main()
