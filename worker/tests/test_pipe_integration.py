"""End-to-end test of a real worker process over a real Windows named pipe, as the application launches it.

The test plays the application: it hosts the pipe server, starts ``python -E -X utf8 -u -m glyphlens_worker --pipe <name>``
with the session token in the environment, and exchanges framed JSON messages. It needs only the standard library: engines
whose libraries are not installed answer honestly with ``engine_unavailable``, and that is what is asserted then.
"""

from __future__ import annotations

import os
import secrets
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path
from typing import Any

from tests import pipe_harness
from tests.support import make_package

WORKER_ROOT = Path(__file__).resolve().parents[1]
SECRET = "TOP-SECRET-SCREEN-TEXT"


class RunningWorker:
    """A worker process connected to a pipe server owned by the test."""

    def __init__(self, models_dir: Path) -> None:
        self.token = secrets.token_hex(32)
        self.server = pipe_harness.PipeServer(f"glyphlens-{uuid.uuid4().hex}")
        environment = dict(os.environ, GLYPHLENS_WORKER_TOKEN=self.token, GLYPHLENS_MODELS_DIR=str(models_dir))
        self.started = time.perf_counter()
        self.process = subprocess.Popen(  # noqa: S603 - the interpreter running this test
            [sys.executable, "-E", "-X", "utf8", "-u", "-m", "glyphlens_worker", "--pipe", self.server.name],
            cwd=WORKER_ROOT,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
        )
        self.server.wait_for_client(30)

    def call(self, message_type: str, payload: Any = None, request_id: str = "r1") -> dict[str, Any]:
        return self.server.call(message_type, payload, request_id)

    def initialize(self) -> dict[str, Any]:
        response = self.call("worker.initialize", {"clientProtocolVersion": "1.0"}, "init")
        self.ready_seconds = time.perf_counter() - self.started
        return response

    def stop(self) -> tuple[int | None, str]:
        """Close the pipe, wait for the process and return its exit code and stderr."""
        self.server.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=15)
        stderr = self.process.stderr.read().decode("utf-8", "replace") if self.process.stderr else ""
        if self.process.stderr:
            self.process.stderr.close()
        return self.process.returncode, stderr


@unittest.skipUnless(pipe_harness.AVAILABLE, "needs Windows named pipes")
class WorkerProcessTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)
        self.worker = RunningWorker(self.temp / "models")
        self.stopped = False
        self.addCleanup(self.stop)

    def stop(self) -> tuple[int | None, str]:
        if self.stopped:
            return self.exit_code, self.stderr
        self.stopped = True
        self.exit_code, self.stderr = self.worker.stop()
        return self.exit_code, self.stderr

    def test_the_handshake_and_every_translation_message_work_over_a_real_pipe(self) -> None:
        init = self.worker.initialize()

        self.assertEqual(init["messageType"], "worker.initialize.result")
        self.assertEqual(init["payload"]["sessionToken"], self.worker.token)
        engines = init["payload"]["engines"]
        self.assertIn(engines["ocr"], {"unavailable", "not_installed", "installed"})
        self.assertIn(engines["languageDetection"], {"unavailable", "installed"})
        self.assertIn(engines["translation"], {"unavailable", "not_installed"})

        health = self.worker.call("worker.health", None, "h1")["payload"]
        self.assertEqual(health["engines"], engines)
        self.assertEqual(health["models"], "not_installed")

        archive = make_package(self.temp / "fr.argosmodel")
        install = self.worker.call("models.install", {"path": str(archive)}, "i1")
        self.assertEqual(install["messageType"], "models.install.result")
        self.assertEqual(install["payload"]["package"]["packageName"], "translate-fr_en")
        self.assertEqual(self.worker.call("worker.health", None, "h2")["payload"]["models"], "installed")

        listing = self.worker.call("models.list", None, "l1")["payload"]
        self.assertEqual([package["packageName"] for package in listing["packages"]], ["translate-fr_en"])
        self.assertEqual(listing["storagePath"], str(self.temp / "models"))
        self.assertEqual(listing["state"], "unavailable" if engines["translation"] == "unavailable" else "installed")

        status = self.worker.call("models.status", {"source": "fr", "target": "en"}, "s1")["payload"]
        if engines["translation"] == "unavailable":
            self.assertEqual((status["available"], status["issue"]), (False, "engine_unavailable"))
        else:
            self.assertTrue(status["available"])

        translation = self.worker.call(
            "translation.translate", {"targetLanguage": "en", "items": [{"id": "0", "text": SECRET, "sourceLanguage": "fr"}]}, "t1"
        )
        if engines["translation"] == "unavailable":
            self.assertEqual((translation["messageType"], translation["payload"]["code"]), ("error", "engine_unavailable"))
        else:  # the synthetic weights are not a real model, so the engine reports the item honestly
            self.assertEqual(translation["payload"]["results"][0]["error"]["code"], "translation_failed")

        detection = self.worker.call("language.detect", {"texts": [SECRET]}, "d1")
        if engines["languageDetection"] == "unavailable":
            self.assertEqual((detection["messageType"], detection["payload"]["code"]), ("error", "engine_unavailable"))
        else:
            self.assertEqual(detection["messageType"], "language.detect.result")
            self.assertEqual(len(detection["payload"]["results"]), 1)

        self.assertEqual(self.worker.call("worker.shutdown", None, "bye")["payload"], {"status": "stopping"})
        exit_code, stderr = self.stop()

        self.assertEqual(exit_code, 0, stderr)
        self.assertNotIn(SECRET, stderr)
        self.assertIn("type=translation.translate", stderr)  # request type and outcome are logged, never the content
        self.assertNotIn("blocked a network attempt", stderr)

    def test_a_request_before_initialize_is_an_error_and_the_worker_keeps_serving(self) -> None:
        early = self.worker.call("language.detect", {"texts": [SECRET]}, "early")

        self.assertEqual((early["messageType"], early["payload"]["code"]), ("error", "not_initialized"))
        self.assertEqual(self.worker.initialize()["messageType"], "worker.initialize.result")

    def test_the_worker_exits_when_the_application_closes_the_pipe(self) -> None:
        self.worker.initialize()

        exit_code, _ = self.stop()

        self.assertEqual(exit_code, 5)  # EXIT_PIPE_LOST


if __name__ == "__main__":
    unittest.main()
