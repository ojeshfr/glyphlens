"""Length-prefixed message framing.

A frame is a 4-byte unsigned little-endian length followed by that many bytes of UTF-8 JSON.
This mirrors ``GlyphLens.Infrastructure.IPC.WorkerFraming`` on the .NET side.

Framing only moves opaque bytes. It never assumes one read returns one frame: reads loop until the
header or payload is complete, and the announced length is validated *before* the payload buffer is
allocated, so a corrupt or hostile length cannot make the worker allocate more than the limit.
"""

from __future__ import annotations

import struct
from typing import BinaryIO

MAX_FRAME_BYTES = 16 * 1024 * 1024
HEADER_SIZE = 4

_HEADER = struct.Struct("<I")


class FramingError(Exception):
    """The byte stream violated the framing rules; it cannot be resynchronised."""


def read_exact(stream: BinaryIO, count: int) -> bytes:
    """Read up to ``count`` bytes, looping over partial reads.

    Returns fewer than ``count`` bytes only if the stream ended first (``b""`` if it ended immediately).
    """
    buffer = bytearray(count)
    view = memoryview(buffer)
    total = 0
    while total < count:
        read = stream.readinto(view[total:])
        if not read:  # 0 or None: end of stream
            break
        total += read
    return bytes(buffer[:total])


def read_frame(stream: BinaryIO, max_bytes: int = MAX_FRAME_BYTES) -> bytes | None:
    """Read one frame's payload.

    Returns ``None`` when the stream ended cleanly between frames.
    Raises :class:`FramingError` for a zero-length or oversized frame, or a stream that ends mid-frame.
    """
    header = read_exact(stream, HEADER_SIZE)
    if not header:
        return None
    if len(header) < HEADER_SIZE:
        raise FramingError("connection ended inside a frame header")

    (length,) = _HEADER.unpack(header)
    if length == 0:
        raise FramingError("zero-length frames are not allowed")
    if length > max_bytes:
        raise FramingError(f"frame length {length} exceeds the {max_bytes}-byte limit")

    payload = read_exact(stream, length)
    if len(payload) < length:
        raise FramingError("connection ended inside a frame payload")
    return payload


def write_frame(stream: BinaryIO, payload: bytes, max_bytes: int = MAX_FRAME_BYTES) -> None:
    """Write one frame as a single contiguous write and flush it."""
    if not payload:
        raise FramingError("zero-length frames are not allowed")
    if len(payload) > max_bytes:
        raise FramingError(f"payload length {len(payload)} exceeds the {max_bytes}-byte limit")

    data = _HEADER.pack(len(payload)) + payload
    view = memoryview(data)
    written = 0
    while written < len(data):  # raw streams may accept fewer bytes than offered
        count = stream.write(view[written:])
        if count is None:
            count = 0
        written += count
    stream.flush()
