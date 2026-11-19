"""Tests for length-prefixed framing: partial I/O, limits, truncation."""

from __future__ import annotations

import struct
import unittest

from glyphlens_worker.runtime import framing
from glyphlens_worker.runtime.framing import FramingError, read_frame, write_frame

from tests.streams import ChunkedReader, ChunkedWriter, frame


class ReadFrameTests(unittest.TestCase):
    def test_reads_a_frame(self) -> None:
        self.assertEqual(read_frame(ChunkedReader(frame(b'{"a":1}'), chunk=4096)), b'{"a":1}')

    def test_reads_a_frame_delivered_one_byte_at_a_time(self) -> None:
        payload = b'{"hello":"wor\xc3\xb6ld"}'
        stream = ChunkedReader(frame(payload), chunk=1)

        self.assertEqual(read_frame(stream), payload)
        self.assertEqual(stream.read_calls, 4 + len(payload))  # every byte needed its own read: partial reads are handled

    def test_reads_consecutive_frames_without_confusing_boundaries(self) -> None:
        stream = ChunkedReader(frame(b"one") + frame(b"two") + frame(b"three"), chunk=5)

        self.assertEqual([read_frame(stream), read_frame(stream), read_frame(stream)], [b"one", b"two", b"three"])
        self.assertIsNone(read_frame(stream))

    def test_clean_end_of_stream_between_frames_returns_none(self) -> None:
        self.assertIsNone(read_frame(ChunkedReader(b"")))

    def test_zero_length_frame_is_rejected(self) -> None:
        with self.assertRaisesRegex(FramingError, "zero-length"):
            read_frame(ChunkedReader(struct.pack("<I", 0)))

    def test_oversized_length_is_rejected_before_reading_or_allocating_the_payload(self) -> None:
        stream = ChunkedReader(struct.pack("<I", framing.MAX_FRAME_BYTES + 1) + b"x" * 64, chunk=4096)

        with self.assertRaisesRegex(FramingError, "exceeds"):
            read_frame(stream)

        self.assertEqual(stream.bytes_served, 4)  # only the header was consumed; no payload read

    def test_the_largest_possible_length_field_is_rejected_without_allocating_4_gib(self) -> None:
        stream = ChunkedReader(struct.pack("<I", 0xFFFFFFFF))

        with self.assertRaises(FramingError):
            read_frame(stream)

    def test_length_exactly_at_the_limit_is_accepted(self) -> None:
        payload = b"x" * 1024
        self.assertEqual(read_frame(ChunkedReader(frame(payload), chunk=4096), max_bytes=1024), payload)
        with self.assertRaises(FramingError):
            read_frame(ChunkedReader(frame(payload + b"y"), chunk=4096), max_bytes=1024)

    def test_header_truncated_mid_way_is_rejected(self) -> None:
        with self.assertRaisesRegex(FramingError, "header"):
            read_frame(ChunkedReader(b"\x05\x00"))

    def test_payload_truncated_mid_way_is_rejected(self) -> None:
        with self.assertRaisesRegex(FramingError, "payload"):
            read_frame(ChunkedReader(struct.pack("<I", 10) + b"abc"))

    def test_length_is_little_endian(self) -> None:
        self.assertEqual(read_frame(ChunkedReader(b"\x03\x00\x00\x00abc", chunk=4096)), b"abc")
        with self.assertRaises(FramingError):  # big-endian 3 would read as 0x03000000 = 50 MiB
            read_frame(ChunkedReader(b"\x00\x00\x00\x03abc", chunk=4096))


class WriteFrameTests(unittest.TestCase):
    def test_writes_length_prefix_then_payload(self) -> None:
        writer = ChunkedWriter(chunk=4096)

        write_frame(writer, b"abc")

        self.assertEqual(bytes(writer.data), b"\x03\x00\x00\x00abc")
        self.assertEqual(writer.flushed, 1)

    def test_handles_a_stream_that_accepts_only_a_few_bytes_per_write(self) -> None:
        writer = ChunkedWriter(chunk=3)

        write_frame(writer, b"0123456789")

        self.assertEqual(bytes(writer.data), struct.pack("<I", 10) + b"0123456789")

    def test_rejects_empty_and_oversized_payloads(self) -> None:
        with self.assertRaises(FramingError):
            write_frame(ChunkedWriter(), b"")
        with self.assertRaises(FramingError):
            write_frame(ChunkedWriter(), b"x" * 11, max_bytes=10)

    def test_round_trip_through_write_and_read(self) -> None:
        writer = ChunkedWriter(chunk=7)
        write_frame(writer, "unicode: é中".encode("utf-8"))

        self.assertEqual(read_frame(ChunkedReader(bytes(writer.data), chunk=2)), "unicode: é中".encode("utf-8"))


if __name__ == "__main__":
    unittest.main()
