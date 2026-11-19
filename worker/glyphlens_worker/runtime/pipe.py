"""Named pipe client.

The application hosts the server end (so it controls the pipe's ACL and lifetime); the worker connects as
the single client. On Windows a named pipe is a file, so the standard library's ``open()`` is the whole
client: no pywin32 and no ctypes. This is deliberately the simplest thing that works, and it adds no
dependency. Reads are blocking, which is fine because the worker handles one request at a time; if the
application exits the pipe breaks and the blocked read returns end-of-stream, so the worker exits too.
"""

from __future__ import annotations

import errno
import time
from typing import BinaryIO

PIPE_PREFIX = "\\\\.\\pipe\\"

# Windows error code for "all pipe instances are busy". ``open()`` goes through the C runtime, which usually reports
# this as OSError(errno=EINVAL) with no ``winerror`` (measured), so both forms are recognised.
_ERROR_PIPE_BUSY = 231

# Transient while the server is between instances or serving another client: retry until the timeout.
_TRANSIENT_ERRNOS = frozenset({errno.ENOENT, errno.EINVAL, errno.EBUSY})


class PipeConnectError(Exception):
    """The pipe could not be opened within the timeout (or access was refused)."""


def _is_transient(exc: OSError) -> bool:
    return getattr(exc, "winerror", None) == _ERROR_PIPE_BUSY or exc.errno in _TRANSIENT_ERRNOS


def connect(pipe_name: str, timeout: float = 15.0, retry_interval: float = 0.05) -> BinaryIO:
    """Open the application's pipe for reading and writing, retrying while it is not yet available."""
    path = PIPE_PREFIX + pipe_name
    deadline = time.monotonic() + timeout
    last_error: OSError | None = None
    while True:
        try:
            return open(path, "r+b", buffering=0)  # noqa: SIM115 - the caller owns and closes the stream
        except PermissionError as exc:  # the ACL refused us: retrying will not help
            raise PipeConnectError("access to the pipe was refused") from exc
        except OSError as exc:
            if not _is_transient(exc):
                raise PipeConnectError(f"could not open the pipe (errno {exc.errno})") from exc
            last_error = exc

        if time.monotonic() >= deadline:
            raise PipeConnectError(f"timed out waiting for the pipe (errno {last_error.errno if last_error else '?'})")
        time.sleep(retry_interval)
