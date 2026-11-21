"""Truthful worker health.

Every field is a distinct fact. A running process says nothing about whether an engine works: an engine is only
``ready`` once it has loaded and done its job. The states of the OCR, language and translation engines come from
:class:`ComponentStates`, computed without importing any engine library (see ``runtime.services``).
"""

from __future__ import annotations

import os
import platform
from dataclasses import dataclass
from typing import Any

from glyphlens_worker import PROTOCOL_VERSION, __version__

@dataclass(frozen=True)
class ComponentStates:
    """The wire values (lower snake case) of the engine and model states. See docs/IPC-PROTOCOL.md."""

    language_detection: str
    translation: str
    models: str
    ocr: str = "unavailable"

    def engines(self) -> dict[str, str]:
        """The ``engines`` object of ``worker.initialize`` and ``worker.health`` results."""
        return {"ocr": self.ocr, "languageDetection": self.language_detection, "translation": self.translation}


def health_payload(*, ipc: str, protocol: str, states: ComponentStates) -> dict[str, Any]:
    """Build the ``worker.health`` result payload (camelCase keys, snake_case state values)."""
    return {
        "process": "ready",
        "ipc": ipc,
        "protocol": protocol,
        "engines": states.engines(),
        "models": states.models,
        "workerVersion": __version__,
        "protocolVersion": PROTOCOL_VERSION,
        "python": platform.python_version(),
        "pid": os.getpid(),
    }
