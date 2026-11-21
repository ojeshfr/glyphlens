"""Startup configuration for the worker.

The application passes the pipe name on the command line and the session token in the environment. Both are
validated here: the worker only ever connects to a pipe whose name matches the pattern the application generates, so
a name supplied by anything else (for example an attacker-controlled path) is refused rather than opened.

The model directory is configured here too: ``GLYPHLENS_MODELS_DIR`` (an absolute local path) or ``%LOCALAPPDATA%\\GlyphLens\\models``.
"""

from __future__ import annotations

import os
import re
from collections.abc import Mapping
from dataclasses import dataclass
from pathlib import Path

#: Names the application generates: ``glyphlens-`` followed by 128 random bits in lowercase hex.
PIPE_NAME_PATTERN = re.compile(r"^glyphlens-[0-9a-f]{32}$")

#: Environment variable carrying the per-session token (kept off the command line, which other processes can read).
TOKEN_ENVIRONMENT_VARIABLE = "GLYPHLENS_WORKER_TOKEN"

MAX_TOKEN_LENGTH = 256

#: Optional override of the model directory (an absolute local path; the tests use it).
MODELS_DIR_ENVIRONMENT_VARIABLE = "GLYPHLENS_MODELS_DIR"

#: Where models live below ``%LOCALAPPDATA%`` when there is no override.
DEFAULT_MODELS_SUBDIRECTORY = Path("GlyphLens") / "models"


class SettingsError(ValueError):
    """The startup configuration is missing or invalid."""


@dataclass(frozen=True)
class ModelsLocation:
    """Where installed translation models are stored.

    ``path`` is ``None`` when no usable location could be determined. The directory is not created here: it is
    created on the first installation. ``problems`` explains an ignored override or a missing default (no values).
    """

    path: Path | None
    problems: tuple[str, ...] = ()


def _is_local_absolute(value: str) -> bool:
    return "\x00" not in value and not value.startswith(("\\\\", "//")) and Path(value).is_absolute()


def resolve_models_location(environ: Mapping[str, str] | None = None) -> ModelsLocation:
    """Find the model directory: ``GLYPHLENS_MODELS_DIR`` when it is an absolute local path, else ``%LOCALAPPDATA%\\GlyphLens\\models``.

    Nothing outside this directory is ever read or written, and Argos' own directories are never touched.
    """
    env = os.environ if environ is None else environ
    problems: list[str] = []

    override = env.get(MODELS_DIR_ENVIRONMENT_VARIABLE)
    if override:
        if _is_local_absolute(override):
            return ModelsLocation(Path(override))
        problems.append(f"{MODELS_DIR_ENVIRONMENT_VARIABLE} is not an absolute local path and was ignored.")

    local_app_data = env.get("LOCALAPPDATA")
    if local_app_data and _is_local_absolute(local_app_data):
        return ModelsLocation(Path(local_app_data) / DEFAULT_MODELS_SUBDIRECTORY, tuple(problems))

    problems.append("The default model directory is unavailable because LOCALAPPDATA is not set.")
    return ModelsLocation(None, tuple(problems))


def validate_pipe_name(name: str) -> str:
    if not isinstance(name, str) or not PIPE_NAME_PATTERN.match(name):
        raise SettingsError("the pipe name is not a GlyphLens session pipe name")
    return name


def take_session_token(environ: dict[str, str] | None = None) -> str:
    """Read the session token and remove it from the environment so it is not inherited by anything we start."""
    env = os.environ if environ is None else environ
    token = env.pop(TOKEN_ENVIRONMENT_VARIABLE, None)
    if not token or len(token) > MAX_TOKEN_LENGTH:
        raise SettingsError(f"{TOKEN_ENVIRONMENT_VARIABLE} is missing or invalid")
    return token
