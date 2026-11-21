"""Tests for startup configuration, pipe connection retry logic and exit codes."""

from __future__ import annotations

import contextlib
import errno
import io
import socket
import unittest
from unittest import mock

from glyphlens_worker import main as worker_main
from glyphlens_worker.config import settings
from glyphlens_worker.runtime import network_guard, pipe, server

VALID_PIPE = "glyphlens-" + "0123456789abcdef" * 2


class PipeNameTests(unittest.TestCase):
    def test_accepts_only_names_the_application_generates(self) -> None:
        self.assertEqual(settings.validate_pipe_name(VALID_PIPE), VALID_PIPE)

    def test_rejects_arbitrary_or_malicious_names(self) -> None:
        for name in (
            "",
            "glyphlens-xyz",
            "glyphlens-" + "G" * 32,
            "glyphlens-" + "a" * 31,
            "glyphlens-" + "a" * 33,
            "..\\evil",
            "\\\\.\\pipe\\" + VALID_PIPE,
            "\\\\server\\pipe\\" + VALID_PIPE,
            VALID_PIPE + "\\..\\other",
            "other-" + "a" * 32,
        ):
            with self.subTest(name=name), self.assertRaises(settings.SettingsError):
                settings.validate_pipe_name(name)


class TokenTests(unittest.TestCase):
    def test_token_is_taken_from_the_environment_and_removed(self) -> None:
        env = {settings.TOKEN_ENVIRONMENT_VARIABLE: "secret-token"}

        self.assertEqual(settings.take_session_token(env), "secret-token")
        self.assertNotIn(settings.TOKEN_ENVIRONMENT_VARIABLE, env)

    def test_missing_empty_or_huge_tokens_are_rejected(self) -> None:
        for env in ({}, {settings.TOKEN_ENVIRONMENT_VARIABLE: ""}, {settings.TOKEN_ENVIRONMENT_VARIABLE: "x" * 1000}):
            with self.subTest(env=env), self.assertRaises(settings.SettingsError):
                settings.take_session_token(env)


class ConnectTests(unittest.TestCase):
    def test_retries_while_the_pipe_is_missing_or_busy_then_connects(self) -> None:
        sentinel = object()
        outcomes = [
            FileNotFoundError(errno.ENOENT, "missing"),
            OSError(errno.EINVAL, "busy (as the C runtime reports it)"),
            sentinel,
        ]

        def fake_open(*_args: object, **_kwargs: object) -> object:
            outcome = outcomes.pop(0)
            if isinstance(outcome, Exception):
                raise outcome
            return outcome

        with mock.patch("builtins.open", fake_open):
            self.assertIs(pipe.connect(VALID_PIPE, timeout=5, retry_interval=0), sentinel)

        self.assertEqual(outcomes, [])

    def test_opens_the_full_pipe_path_read_write_unbuffered(self) -> None:
        with mock.patch("builtins.open", return_value=object()) as opener:
            pipe.connect(VALID_PIPE)

        opener.assert_called_once_with("\\\\.\\pipe\\" + VALID_PIPE, "r+b", buffering=0)

    def test_access_denied_fails_immediately_without_retrying(self) -> None:
        with mock.patch("builtins.open", side_effect=PermissionError(errno.EACCES, "denied")) as opener:
            with self.assertRaisesRegex(pipe.PipeConnectError, "refused"):
                pipe.connect(VALID_PIPE, timeout=5, retry_interval=0)

        self.assertEqual(opener.call_count, 1)

    def test_an_unexpected_os_error_fails_immediately(self) -> None:
        with mock.patch("builtins.open", side_effect=OSError(errno.EIO, "io")):
            with self.assertRaisesRegex(pipe.PipeConnectError, "could not open"):
                pipe.connect(VALID_PIPE, timeout=5, retry_interval=0)

    def test_gives_up_after_the_timeout(self) -> None:
        with mock.patch("builtins.open", side_effect=FileNotFoundError(errno.ENOENT, "missing")):
            with self.assertRaisesRegex(pipe.PipeConnectError, "timed out"):
                pipe.connect(VALID_PIPE, timeout=0.05, retry_interval=0.01)


class RunPipeSessionTests(unittest.TestCase):
    def test_invalid_pipe_name_is_a_usage_error(self) -> None:
        self.assertEqual(worker_main.run_pipe_session("not-a-session-pipe"), server.EXIT_USAGE)

    def test_missing_token_has_its_own_exit_code(self) -> None:
        with mock.patch.dict("os.environ", {}, clear=False) as env:
            env.pop(settings.TOKEN_ENVIRONMENT_VARIABLE, None)
            self.assertEqual(worker_main.run_pipe_session(VALID_PIPE), server.EXIT_NO_TOKEN)

    def test_connection_failure_has_its_own_exit_code(self) -> None:
        with mock.patch.dict("os.environ", {settings.TOKEN_ENVIRONMENT_VARIABLE: "t"}), mock.patch.object(
            worker_main, "connect", side_effect=pipe.PipeConnectError("nope")
        ):
            self.assertEqual(worker_main.run_pipe_session(VALID_PIPE), server.EXIT_CONNECT_FAILED)

    def test_main_dispatches_pipe_mode_and_returns_its_exit_code(self) -> None:
        self.addCleanup(network_guard.uninstall)

        with mock.patch.object(worker_main, "run_pipe_session", return_value=5) as run:
            self.assertEqual(worker_main.main(["--pipe", VALID_PIPE]), 5)

        run.assert_called_once_with(VALID_PIPE)

    def test_the_network_guard_is_installed_before_the_session_starts(self) -> None:
        network_guard.uninstall()
        self.addCleanup(network_guard.uninstall)
        seen: list[bool] = []

        def session(_pipe_name: str) -> int:
            seen.append(network_guard.is_installed())
            return 0

        with mock.patch.object(worker_main, "run_pipe_session", session):
            worker_main.main(["--pipe", VALID_PIPE])

        self.assertEqual(seen, [True])

    def test_version_and_health_modes_do_not_install_the_guard(self) -> None:
        network_guard.uninstall()

        with contextlib.redirect_stdout(io.StringIO()):
            worker_main.main(["--version"])
            worker_main.main(["--health"])

        self.assertFalse(network_guard.is_installed())

    def test_blocked_attempts_are_logged_by_name_only(self) -> None:
        network_guard.uninstall()
        self.addCleanup(network_guard.uninstall)
        logged: list[str] = []

        with mock.patch.object(worker_main, "run_pipe_session", return_value=0), mock.patch.object(
            worker_main, "stderr_log", logged.append
        ):
            worker_main.main(["--pipe", VALID_PIPE])
            with self.assertRaises(network_guard.NetworkBlockedError):
                socket.getaddrinfo("secret-host.example.invalid", 443)

        self.assertEqual(logged, ["blocked a network attempt (socket.getaddrinfo)"])

    def test_exit_codes_are_distinct(self) -> None:
        codes = [server.EXIT_OK, server.EXIT_USAGE, server.EXIT_CONNECT_FAILED, server.EXIT_PROTOCOL_ERROR, server.EXIT_PIPE_LOST, server.EXIT_NO_TOKEN]
        self.assertEqual(len(set(codes)), len(codes))


if __name__ == "__main__":
    unittest.main()
