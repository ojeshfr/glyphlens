"""Tests for the request loop: correlation, malformed input, limits, disconnects and shutdown."""

from __future__ import annotations

import json
import struct
import unittest
from datetime import datetime, timezone

from glyphlens_worker.protocols import envelope
from glyphlens_worker.runtime import framing, server
from glyphlens_worker.runtime.dispatcher import Dispatcher
from glyphlens_worker.runtime.server import serve

from tests.streams import FakePipe, frame

TOKEN = "t" * 64


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


def run(incoming: bytes, **pipe_args: object) -> tuple[int, list[dict], list[str]]:
    pipe = FakePipe(incoming, **pipe_args)  # type: ignore[arg-type]
    logs: list[str] = []
    code = serve(pipe, Dispatcher(TOKEN), logs.append)
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
        self.assertEqual(replies[1]["payload"]["engines"]["ocr"], "not_implemented")
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
        _, replies, _ = run(INIT + message("ocr.detect", "ocr-1", {"pixels": "AAAA"}) + message("worker.shutdown", "bye"))

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
        dispatcher = Dispatcher(TOKEN)
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
    def test_logs_contain_ids_types_status_and_timing_but_never_payload_content(self) -> None:
        secret = "TOP-SECRET-SCREEN-TEXT"
        _, _, logs = run(INIT + message("translation.translate", "t-1", {"text": secret}) + message("worker.shutdown", "bye"))

        joined = "\n".join(logs)
        self.assertNotIn(secret, joined)
        self.assertIn("req=t-1", joined)
        self.assertIn("type=translation.translate", joined)
        self.assertIn("status=error:engine_unavailable", joined)
        self.assertRegex(joined, r"ms=\d")

    def test_error_responses_never_echo_the_payload(self) -> None:
        secret = "TOP-SECRET-SCREEN-TEXT"
        _, replies, _ = run(INIT + message("translation.translate", "t-1", {"text": secret}) + message("worker.shutdown", "bye"))

        self.assertNotIn(secret, json.dumps(replies))


if __name__ == "__main__":
    unittest.main()
