"""Startup configuration for the worker.

The application passes the pipe name on the command line and the session token in the environment. Both are
validated here: the worker only ever connects to a pipe whose name matches the pattern the application generates, so
a name supplied by anything else (for example an attacker-controlled path) is refused rather than opened.
"""

from __future__ import annotations

import os
import re

#: Names the application generates: ``glyphlens-`` followed by 128 random bits in lowercase hex.
PIPE_NAME_PATTERN = re.compile(r"^glyphlens-[0-9a-f]{32}$")

#: Environment variable carrying the per-session token (kept off the command line, which other processes can read).
TOKEN_ENVIRONMENT_VARIABLE = "GLYPHLENS_WORKER_TOKEN"

MAX_TOKEN_LENGTH = 256


class SettingsError(ValueError):
    """The startup configuration is missing or invalid."""


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
