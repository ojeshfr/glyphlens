"""Tests for the fail-closed network guard. They use the standard library only and never touch a real network."""

from __future__ import annotations

import socket
import unittest
import urllib.request
import uuid

from glyphlens_worker.runtime import network_guard
from glyphlens_worker.runtime.framing import read_frame, write_frame
from glyphlens_worker.runtime.network_guard import NetworkBlockedError
from glyphlens_worker.runtime.pipe import connect

from tests import pipe_harness


class GuardTestCase(unittest.TestCase):
    def setUp(self) -> None:
        network_guard.uninstall()
        self.addCleanup(network_guard.uninstall)


class BlockingTests(GuardTestCase):
    def test_the_guard_is_off_until_installed(self) -> None:
        self.assertFalse(network_guard.is_installed())
        network_guard.install()
        self.assertTrue(network_guard.is_installed())

    def test_connecting_a_socket_is_refused_and_recorded(self) -> None:
        network_guard.install()

        with socket.socket() as sock, self.assertRaises(NetworkBlockedError):
            sock.connect(("127.0.0.1", 9))
        with socket.socket() as sock, self.assertRaises(NetworkBlockedError):
            sock.connect_ex(("127.0.0.1", 9))

        self.assertEqual(network_guard.blocked_attempts(), ("socket.connect", "socket.connect_ex"))

    def test_name_lookups_and_helpers_are_refused(self) -> None:
        network_guard.install()
        calls = {
            "socket.create_connection": lambda: socket.create_connection(("example.invalid", 80)),
            "socket.getaddrinfo": lambda: socket.getaddrinfo("example.invalid", 80),
            "socket.gethostbyname": lambda: socket.gethostbyname("example.invalid"),
            "socket.gethostbyname_ex": lambda: socket.gethostbyname_ex("example.invalid"),
            "socket.gethostbyaddr": lambda: socket.gethostbyaddr("192.0.2.1"),
            "urllib.request.urlopen": lambda: urllib.request.urlopen("http://example.invalid/"),
        }

        for label, call in calls.items():
            with self.subTest(label=label), self.assertRaises(NetworkBlockedError):
                call()

        self.assertEqual(set(network_guard.blocked_attempts()), set(calls))

    def test_datagrams_are_refused(self) -> None:
        network_guard.install()

        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock, self.assertRaises(NetworkBlockedError):
            sock.sendto(b"x", ("192.0.2.1", 9))

    def test_a_library_that_swallows_the_error_is_still_recorded(self) -> None:
        network_guard.install()

        try:
            socket.getaddrinfo("example.invalid", 80)
        except OSError:
            pass  # what a careless library does

        self.assertEqual(network_guard.blocked_attempt_count(), 1)
        self.assertEqual(network_guard.blocked_attempts(), ("socket.getaddrinfo",))

    def test_the_error_is_an_os_error_so_libraries_treat_it_as_being_offline(self) -> None:
        self.assertTrue(issubclass(NetworkBlockedError, ConnectionError))
        self.assertTrue(issubclass(NetworkBlockedError, OSError))

    def test_only_the_function_name_is_recorded_never_an_address(self) -> None:
        seen: list[str] = []
        network_guard.install(on_attempt=seen.append)

        with self.assertRaises(NetworkBlockedError) as raised:
            socket.getaddrinfo("secret-host.example.invalid", 8443)

        self.assertEqual(seen, ["socket.getaddrinfo"])
        self.assertNotIn("secret-host", str(raised.exception))
        self.assertNotIn("secret-host", " ".join(network_guard.blocked_attempts()))

    def test_the_list_of_attempts_is_bounded_but_counting_continues(self) -> None:
        network_guard.install()

        for _ in range(network_guard.MAX_RECORDED_ATTEMPTS + 25):
            with self.assertRaises(NetworkBlockedError):
                socket.gethostbyname("example.invalid")

        self.assertEqual(len(network_guard.blocked_attempts()), network_guard.MAX_RECORDED_ATTEMPTS)
        self.assertEqual(network_guard.blocked_attempt_count(), network_guard.MAX_RECORDED_ATTEMPTS + 25)


class InstallationTests(GuardTestCase):
    def test_installing_twice_does_not_stack_wrappers(self) -> None:
        network_guard.install()
        first = socket.getaddrinfo
        network_guard.install()

        self.assertIs(socket.getaddrinfo, first)

    def test_uninstall_restores_the_original_functions_and_forgets_attempts(self) -> None:
        originals = (socket.getaddrinfo, socket.create_connection, socket.socket.connect, urllib.request.urlopen)
        network_guard.install()
        with self.assertRaises(NetworkBlockedError):
            socket.getaddrinfo("example.invalid", 80)

        network_guard.uninstall()

        self.assertEqual((socket.getaddrinfo, socket.create_connection, socket.socket.connect, urllib.request.urlopen), originals)
        self.assertEqual(network_guard.blocked_attempts(), ())
        self.assertEqual(network_guard.blocked_attempt_count(), 0)
        self.assertFalse(network_guard.is_installed())


@unittest.skipUnless(pipe_harness.AVAILABLE, "needs Windows named pipes")
class NamedPipeTests(GuardTestCase):
    def test_the_worker_pipe_client_still_works_with_the_guard_installed(self) -> None:
        # The worker talks to the application over a named pipe opened as a file, not over a socket.
        network_guard.install()
        name = f"glyphlens-guardtest-{uuid.uuid4().hex}"

        with pipe_harness.PipeServer(name) as server:
            stream = connect(name, timeout=10)
            with stream:
                server.wait_for_client()
                server.send_frame(b'{"hello":"worker"}')
                self.assertEqual(read_frame(stream), b'{"hello":"worker"}')

                write_frame(stream, b'{"hello":"app"}')
                self.assertEqual(server.receive_frame(10), b'{"hello":"app"}')

        self.assertEqual(network_guard.blocked_attempts(), ())


if __name__ == "__main__":
    unittest.main()
