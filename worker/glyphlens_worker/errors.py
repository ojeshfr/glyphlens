"""Errors raised by the engine services and turned into protocol ``error`` responses by the dispatcher."""

from __future__ import annotations


class ServiceError(Exception):
    """A request cannot be served.

    ``code`` is one of the protocol's error codes (for example ``invalid_request`` or ``engine_unavailable``).
    ``message`` is sent to the application as it is, so it never contains payload content (screen text, file
    contents) and never contains a file system path.
    """

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message
