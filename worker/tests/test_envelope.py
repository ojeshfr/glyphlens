"""Tests for IPC protocol envelope validation."""

from __future__ import annotations

import json
import unittest
from typing import Any

from glyphlens_worker.protocols import envelope
from glyphlens_worker.protocols.envelope import ProtocolError


def make_message(**overrides: Any) -> dict[str, Any]:
    message: dict[str, Any] = {
        "protocolVersion": "1.0",
        "requestId": "7f9c2a52-1d1e-4a3b-9c55-0b8e2f6c1a10",
        "messageType": "worker.health",
        "payload": {},
        "timestamp": "2026-01-15T10:30:00+00:00",
    }
    message.update(overrides)
    return message


class VersionTests(unittest.TestCase):
    def test_parse_valid_versions(self) -> None:
        self.assertEqual(envelope.parse_version("1.0"), (1, 0))
        self.assertEqual(envelope.parse_version("12.34"), (12, 34))

    def test_parse_rejects_malformed_versions(self) -> None:
        for value in ("", "1", "1.0.0", "01.0", "1.01", "0.1", "-1.0", " 1.0", "a.b", 1.0, None):
            with self.subTest(value=value), self.assertRaises(ProtocolError):
                envelope.parse_version(value)

    def test_compatibility_requires_same_major(self) -> None:
        self.assertTrue(envelope.is_compatible("1.7", current="1.0"))
        self.assertFalse(envelope.is_compatible("2.0", current="1.0"))


class ValidateTests(unittest.TestCase):
    def test_valid_message_returns_envelope(self) -> None:
        result = envelope.validate(make_message(payload={"x": 1}))
        self.assertEqual(result.message_type, "worker.health")
        self.assertEqual(result.payload, {"x": 1})
        self.assertIsNotNone(result.timestamp.tzinfo)

    def test_null_payload_is_allowed(self) -> None:
        self.assertIsNone(envelope.validate(make_message(payload=None)).payload)

    def test_utc_z_suffix_is_accepted(self) -> None:
        result = envelope.validate(make_message(timestamp="2026-01-15T10:30:00Z"))
        self.assertEqual(result.timestamp.utcoffset().total_seconds(), 0)

    def test_rejects_non_object(self) -> None:
        with self.assertRaises(ProtocolError):
            envelope.validate(["not", "an", "object"])

    def test_rejects_missing_fields(self) -> None:
        for field in ("protocolVersion", "requestId", "messageType", "payload", "timestamp"):
            message = make_message()
            del message[field]
            with self.subTest(field=field), self.assertRaisesRegex(ProtocolError, field):
                envelope.validate(message)

    def test_rejects_incompatible_major_version(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "unsupported"):
            envelope.validate(make_message(protocolVersion="2.0"))

    def test_accepts_newer_minor_version(self) -> None:
        self.assertEqual(envelope.validate(make_message(protocolVersion="1.9")).protocol_version, "1.9")

    def test_rejects_invalid_request_ids(self) -> None:
        for request_id in ("", "   ", "x" * (envelope.MAX_REQUEST_ID_LENGTH + 1), 42):
            with self.subTest(request_id=request_id), self.assertRaises(ProtocolError):
                envelope.validate(make_message(requestId=request_id))

    def test_rejects_invalid_message_types(self) -> None:
        for message_type in ("", "Worker.Health", "worker..health", ".worker", "worker health", 7):
            with self.subTest(message_type=message_type), self.assertRaises(ProtocolError):
                envelope.validate(make_message(messageType=message_type))

    def test_accepts_result_and_error_message_types(self) -> None:
        for message_type in ("worker.health.result", "error"):
            with self.subTest(message_type=message_type):
                envelope.validate(make_message(messageType=message_type))

    def test_rejects_naive_or_invalid_timestamps(self) -> None:
        for timestamp in ("2026-01-15T10:30:00", "yesterday", 1700000000):
            with self.subTest(timestamp=timestamp), self.assertRaises(ProtocolError):
                envelope.validate(make_message(timestamp=timestamp))

    def test_request_types_match_documented_protocol(self) -> None:
        self.assertEqual(
            envelope.REQUEST_TYPES,
            {
                "worker.initialize", "worker.health", "worker.shutdown", "ocr.detect",
                "translation.translate", "translation.cancel", "models.list", "models.status",
                "models.install", "language.detect",
            },
        )


class DecodeTests(unittest.TestCase):
    def test_decode_round_trip(self) -> None:
        data = json.dumps(make_message()).encode("utf-8")
        self.assertEqual(envelope.decode(data).request_id, make_message()["requestId"])

    def test_decode_rejects_invalid_json_and_encoding(self) -> None:
        for data in (b"{not json", b"\xff\xfe\x00"):
            with self.subTest(data=data), self.assertRaises(ProtocolError):
                envelope.decode(data)

    def test_decode_rejects_oversized_message(self) -> None:
        with self.assertRaisesRegex(ProtocolError, "limit"):
            envelope.decode(b" " * (envelope.MAX_MESSAGE_BYTES + 1))


if __name__ == "__main__":
    unittest.main()
