"""A fail-closed, Python-level network guard.

GlyphLens' rule is that the worker never touches the network: no downloads, no telemetry, no lookups. The guard is
defence in depth for that rule. It replaces the standard library's ways of opening a connection or resolving a name
with functions that **record the attempt and raise** :class:`NetworkBlockedError` (recording matters: a library can
swallow the exception). The worker installs it at start-up, before any engine library is imported.

Limits, stated plainly:

* It only sees Python code. CTranslate2 and SentencePiece are native libraries, and native code that opens a socket
  itself is invisible to it. Verifying that needs a firewall rule or a packet capture.
* A library that kept a reference to an original function before the guard was installed still calls
  ``socket.socket.connect`` underneath, which is replaced too.
* Local sockets are refused as well (``connect`` is refused whatever the address; on Windows ``socket.socketpair`` connects).
  The worker does not need one: it talks to the application over a named pipe, opened as a file with ``open()``,
  which the guard does not touch.

Only the name of the blocked function is recorded, never an address or host name.
"""

from __future__ import annotations

import socket
import urllib.request
from collections.abc import Callable
from typing import Any, NoReturn

#: The most attempts kept; further ones are counted but not listed.
MAX_RECORDED_ATTEMPTS = 100

AttemptCallback = Callable[[str], None]


class NetworkBlockedError(ConnectionError):
    """A network operation was refused because the worker never uses the network."""


_originals: list[tuple[Any, str, Any]] = []
_attempts: list[str] = []
_attempt_count = 0
_on_attempt: AttemptCallback | None = None


def _targets() -> list[tuple[Any, str, str]]:
    """The functions to replace: (owner, attribute name, label recorded for an attempt)."""
    return [
        (socket.socket, "connect", "socket.connect"),
        (socket.socket, "connect_ex", "socket.connect_ex"),
        (socket.socket, "sendto", "socket.sendto"),
        (socket, "create_connection", "socket.create_connection"),
        (socket, "getaddrinfo", "socket.getaddrinfo"),
        (socket, "gethostbyname", "socket.gethostbyname"),
        (socket, "gethostbyname_ex", "socket.gethostbyname_ex"),
        (socket, "gethostbyaddr", "socket.gethostbyaddr"),
        (urllib.request, "urlopen", "urllib.request.urlopen"),
    ]


def _record(label: str) -> None:
    global _attempt_count
    _attempt_count += 1
    if len(_attempts) < MAX_RECORDED_ATTEMPTS:
        _attempts.append(label)
    callback = _on_attempt
    if callback is not None:
        callback(label)


def _denier(label: str) -> Callable[..., NoReturn]:
    def denied(*_args: object, **_kwargs: object) -> NoReturn:
        _record(label)
        raise NetworkBlockedError(f"network access is disabled in the GlyphLens worker ({label})")

    denied.__name__ = f"blocked_{label.replace('.', '_')}"
    return denied


def install(on_attempt: AttemptCallback | None = None) -> None:
    """Install the guard. Safe to call more than once; ``on_attempt`` receives the label of every blocked call."""
    global _on_attempt
    _on_attempt = on_attempt
    if _originals:
        return
    for owner, attribute, label in _targets():
        _originals.append((owner, attribute, getattr(owner, attribute)))
        setattr(owner, attribute, _denier(label))


def uninstall() -> None:
    """Restore the original functions and forget what was recorded (for tests; the worker never calls it)."""
    global _on_attempt, _attempt_count
    while _originals:
        owner, attribute, original = _originals.pop()
        setattr(owner, attribute, original)
    _attempts.clear()
    _attempt_count = 0
    _on_attempt = None


def is_installed() -> bool:
    return bool(_originals)


def blocked_attempts() -> tuple[str, ...]:
    """The labels of the blocked calls so far (at most ``MAX_RECORDED_ATTEMPTS``), oldest first."""
    return tuple(_attempts)


def blocked_attempt_count() -> int:
    return _attempt_count
