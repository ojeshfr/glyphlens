"""Test doubles for binary streams (no real pipe needed)."""

from __future__ import annotations

import struct


class ChunkedReader:
    """A readable stream that returns at most ``chunk`` bytes per ``readinto`` call (simulating partial pipe reads)."""

    def __init__(self, data: bytes, chunk: int = 1) -> None:
        self._data = data
        self._chunk = chunk
        self._pos = 0
        self.read_calls = 0
        self.bytes_served = 0

    def readinto(self, buffer: memoryview) -> int:
        self.read_calls += 1
        remaining = len(self._data) - self._pos
        count = min(len(buffer), self._chunk, remaining)
        buffer[:count] = self._data[self._pos : self._pos + count]
        self._pos += count
        self.bytes_served += count
        return count


class ChunkedWriter:
    """A writable stream that accepts at most ``chunk`` bytes per ``write`` call (simulating partial pipe writes)."""

    def __init__(self, chunk: int = 3) -> None:
        self._chunk = chunk
        self.data = bytearray()
        self.flushed = 0

    def write(self, buffer: memoryview) -> int:
        count = min(len(buffer), self._chunk)
        self.data += bytes(buffer[:count])
        return count

    def flush(self) -> None:
        self.flushed += 1


class FakePipe:
    """A duplex stream: reads come from queued input, writes are collected. Optionally fails writes."""

    def __init__(self, incoming: bytes = b"", fail_writes: bool = False, chunk: int = 4096) -> None:
        self._reader = ChunkedReader(incoming, chunk)
        self.written = bytearray()
        self.fail_writes = fail_writes

    def readinto(self, buffer: memoryview) -> int:
        return self._reader.readinto(buffer)

    def write(self, buffer: memoryview) -> int:
        if self.fail_writes:
            raise BrokenPipeError("simulated broken pipe")
        self.written += bytes(buffer)
        return len(buffer)

    def flush(self) -> None:
        pass

    def frames_written(self) -> list[bytes]:
        """Split everything written so far back into frames."""
        out: list[bytes] = []
        data = bytes(self.written)
        pos = 0
        while pos < len(data):
            (length,) = struct.unpack_from("<I", data, pos)
            out.append(data[pos + 4 : pos + 4 + length])
            pos += 4 + length
        return out


def frame(payload: bytes) -> bytes:
    return struct.pack("<I", len(payload)) + payload
