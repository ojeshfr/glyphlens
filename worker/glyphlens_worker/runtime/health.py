"""Truthful worker health.

Every field is a distinct fact. A running process says nothing about whether an engine works: an engine is
only ``ready`` when it can do its job. In this build no engine exists, so they are ``not_implemented`` and no
models are installed, whatever the process state.
"""

from __future__ import annotations

import os
import platform
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__


def health_payload(*, ipc: str, protocol: str) -> dict[str, Any]:
    """Build the ``worker.health`` result payload (camelCase keys, snake_case state values)."""
    return {
        "process": "ready",
        "ipc": ipc,
        "protocol": protocol,
        "engines": {
            "ocr": "not_implemented",
            "languageDetection": "not_implemented",
            "translation": "not_implemented",
        },
        "models": "not_installed",
        "workerVersion": __version__,
        "protocolVersion": PROTOCOL_VERSION,
        "python": platform.python_version(),
        "pid": os.getpid(),
    }
