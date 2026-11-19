"""Command-line entry point for the GlyphLens worker.

* ``--pipe NAME``  connect to the application's named pipe and serve requests (the normal mode).
* ``--version``    print the worker version and exit.
* ``--health``     print a JSON health report for a worker that is not connected, and exit.

The worker implements the transport and the ``worker.initialize`` / ``worker.health`` / ``worker.shutdown``
requests. It does not perform OCR, language detection or translation, and says so in its health report.
It never makes network requests and never installs or downloads anything.

Exit codes: 0 clean shutdown, 2 usage error, 3 could not connect, 4 protocol violation, 5 pipe lost
(application gone), 6 session token missing.
"""

from __future__ import annotations

import argparse
import json
import sys
from collections.abc import Sequence
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__
from glyphlens_worker.config.settings import SettingsError, take_session_token, validate_pipe_name
from glyphlens_worker.runtime.dispatcher import Dispatcher
from glyphlens_worker.runtime.health import health_payload
from glyphlens_worker.runtime.pipe import PipeConnectError, connect
from glyphlens_worker.runtime.server import (
    EXIT_CONNECT_FAILED,
    EXIT_NO_TOKEN,
    EXIT_USAGE,
    serve,
    stderr_log,
)

CONNECT_TIMEOUT_SECONDS = 15.0


def health_report() -> dict[str, Any]:
    """Health of a worker that is running but not connected to an application."""
    return health_payload(ipc="disconnected", protocol="not_initialized")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="glyphlens-worker",
        description="GlyphLens local inference worker (transport only: no OCR or translation yet).",
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--version", action="store_true", help="print the worker version and exit")
    group.add_argument("--health", action="store_true", help="print a JSON health report and exit")
    group.add_argument("--pipe", metavar="NAME", help="connect to this GlyphLens session pipe and serve requests")
    return parser


def run_pipe_session(pipe_name: str) -> int:
    try:
        validate_pipe_name(pipe_name)
        token = take_session_token()
    except SettingsError as exc:
        stderr_log(f"configuration error: {exc}")
        return EXIT_NO_TOKEN if "TOKEN" in str(exc) else EXIT_USAGE

    try:
        stream = connect(pipe_name, timeout=CONNECT_TIMEOUT_SECONDS)
    except PipeConnectError as exc:
        stderr_log(f"could not connect: {exc}")
        return EXIT_CONNECT_FAILED

    with stream:
        stderr_log(f"connected (worker {__version__}, protocol {PROTOCOL_VERSION})")
        return serve(stream, Dispatcher(token))


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if args.version:
        print(f"glyphlens-worker {__version__} (protocol {PROTOCOL_VERSION})")
        return 0

    if args.health:
        print(json.dumps(health_report(), indent=2, sort_keys=True))
        return 0

    return run_pipe_session(args.pipe)


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
