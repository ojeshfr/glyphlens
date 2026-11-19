"""Command-line entry point for the GlyphLens worker.

Phase 0 provides diagnostics only. The worker does not yet serve IPC requests,
perform OCR, detect languages or translate text.
"""

from __future__ import annotations

import argparse
import json
import platform
import sys
from collections.abc import Sequence
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__

#: Engines the worker will host. All are reported honestly as not implemented in Phase 0.
ENGINES: tuple[str, ...] = ("ocr", "language_detection", "translation")


def health_report() -> dict[str, Any]:
    """Return a machine-readable description of the worker's current capabilities."""
    return {
        "worker": "glyphlens-worker",
        "version": __version__,
        "protocolVersion": PROTOCOL_VERSION,
        "python": platform.python_version(),
        # The process itself runs, but it cannot serve requests: there is no transport and no engine.
        "status": "unavailable",
        "transport": "not_implemented",
        "engines": {name: "not_implemented" for name in ENGINES},
    }


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="glyphlens-worker",
        description="GlyphLens local inference worker (Phase 0: diagnostics only).",
    )
    group = parser.add_mutually_exclusive_group(required=True)
    group.add_argument("--version", action="store_true", help="print the worker version and exit")
    group.add_argument("--health", action="store_true", help="print a JSON health report and exit")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    args = build_parser().parse_args(argv)

    if args.version:
        print(f"glyphlens-worker {__version__} (protocol {PROTOCOL_VERSION})")
        return 0

    if args.health:
        print(json.dumps(health_report(), indent=2, sort_keys=True))
        return 0

    return 2  # pragma: no cover - argparse enforces one option


if __name__ == "__main__":  # pragma: no cover
    sys.exit(main())
