"""A minimal named-pipe *server* for tests, built on ``_winapi`` (the application is the server in production).

It lets a test (or a measurement script) talk to a real worker process, or to the worker's own pipe client, over a real
Windows named pipe with the same framing as the application: a 4-byte little-endian length and a UTF-8 JSON body.
Windows only; ``AVAILABLE`` says whether the harness can be used.
"""

from __future__ import annotations

import json
import struct
import sys
import time
from datetime import datetime, timezone
from typing import Any

try:
    import _winapi
except ImportError:  # not Windows
    _winapi = None  # type: ignore[assignment]

AVAILABLE = sys.platform == "win32" and _winapi is not None

_BUFFER_BYTES = 1 << 20
_WAIT_OBJECT_0 = 0
_PIPE_MODE_BYTE_WAIT = _winapi.PIPE_WAIT if _winapi is not None else 0  # PIPE_TYPE_BYTE | PIPE_READMODE_BYTE is 0
_HEADER = struct.Struct("<I")


class PipeTimeout(TimeoutError):
    """The peer did not answer in time."""


class PipeServer:
    """One server instance of a byte-mode duplex pipe (``maxInstances = 1``)."""

    def __init__(self, name: str) -> None:
        self.name = name
        self._handle = _winapi.CreateNamedPipe(
            "\\\\.\\pipe\\" + name,
            _winapi.PIPE_ACCESS_DUPLEX | _winapi.FILE_FLAG_OVERLAPPED,
            _PIPE_MODE_BYTE_WAIT,
            1,
            _BUFFER_BYTES,
            _BUFFER_BYTES,
            0,
            _winapi.NULL,
        )
        self._pending_connect = _winapi.ConnectNamedPipe(self._handle, overlapped=True)

    def wait_for_client(self, timeout: float = 15.0) -> None:
        self._wait(self._pending_connect, timeout)
        self._pending_connect.GetOverlappedResult(True)

    def close(self) -> None:
        try:
            _winapi.CloseHandle(self._handle)
        except OSError:
            pass

    def __enter__(self) -> PipeServer:
        return self

    def __exit__(self, *_exc: object) -> None:
        self.close()

    # ---- framed messages ---------------------------------------------------------------------------------------------

    def send_message(self, message_type: str, payload: Any = None, request_id: str = "r1") -> None:
        body = json.dumps(
            {
                "protocolVersion": "1.0",
                "requestId": request_id,
                "messageType": message_type,
                "payload": payload,
                "timestamp": datetime.now(timezone.utc).isoformat(),
            },
            ensure_ascii=False,
        ).encode("utf-8")
        self.send_frame(body)

    def receive_message(self, timeout: float = 60.0) -> dict[str, Any]:
        return json.loads(self.receive_frame(timeout))

    def call(self, message_type: str, payload: Any = None, request_id: str = "r1", timeout: float = 60.0) -> dict[str, Any]:
        """Send one request and return the response message (``result`` or ``error``)."""
        self.send_message(message_type, payload, request_id)
        response = self.receive_message(timeout)
        assert response["requestId"] == request_id, "the worker answered a different request"
        return response

    def send_frame(self, body: bytes) -> None:
        data = _HEADER.pack(len(body)) + body
        overlapped, error = _winapi.WriteFile(self._handle, data, overlapped=True)
        try:
            if error == _winapi.ERROR_IO_PENDING:
                self._wait(overlapped, 30.0)
        finally:
            overlapped.GetOverlappedResult(True)

    def receive_frame(self, timeout: float = 60.0) -> bytes:
        deadline = time.monotonic() + timeout
        (length,) = _HEADER.unpack(self._read_exact(_HEADER.size, deadline))
        return self._read_exact(length, deadline)

    # ---- plumbing ----------------------------------------------------------------------------------------------------

    def _read_exact(self, count: int, deadline: float) -> bytes:
        data = b""
        while len(data) < count:
            overlapped, error = _winapi.ReadFile(self._handle, count - len(data), overlapped=True)
            try:
                if error == _winapi.ERROR_IO_PENDING:
                    self._wait(overlapped, deadline - time.monotonic())
            finally:
                _, error = overlapped.GetOverlappedResult(True)
            chunk = overlapped.getbuffer()
            if error not in (0, _winapi.ERROR_MORE_DATA) or not chunk:
                raise EOFError("the pipe was closed by the worker")
            data += chunk
        return data

    @staticmethod
    def _wait(overlapped: Any, timeout: float) -> None:
        milliseconds = max(0, int(timeout * 1000))
        if _winapi.WaitForSingleObject(overlapped.event, milliseconds) != _WAIT_OBJECT_0:
            overlapped.cancel()
            raise PipeTimeout("timed out waiting for the pipe")
