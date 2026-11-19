"""Tests for the worker package metadata and command-line diagnostics."""

from __future__ import annotations

import contextlib
import io
import json
import re
import subprocess
import sys
import unittest
from pathlib import Path

import glyphlens_worker
from glyphlens_worker import main as worker_main

WORKER_ROOT = Path(__file__).resolve().parents[1]


class PackageTests(unittest.TestCase):
    def test_version_is_semantic(self) -> None:
        self.assertRegex(glyphlens_worker.__version__, r"^\d+\.\d+\.\d+$")

    def test_protocol_version_is_major_minor(self) -> None:
        self.assertRegex(glyphlens_worker.PROTOCOL_VERSION, r"^[1-9]\d*\.\d+$")

    def test_engine_packages_import(self) -> None:
        import glyphlens_worker.engines.language_detection  # noqa: F401
        import glyphlens_worker.engines.ocr  # noqa: F401
        import glyphlens_worker.engines.translation  # noqa: F401

    def test_pyproject_has_no_runtime_dependencies_yet(self) -> None:
        # Phase 0 must not pull in OCR or translation frameworks.
        text = (WORKER_ROOT / "pyproject.toml").read_text(encoding="utf-8")
        self.assertRegex(text, r"(?m)^dependencies = \[\]$")


class CliTests(unittest.TestCase):
    def run_main(self, *argv: str) -> tuple[int, str]:
        buffer = io.StringIO()
        with contextlib.redirect_stdout(buffer):
            code = worker_main.main(list(argv))
        return code, buffer.getvalue()

    def test_version_prints_worker_and_protocol_version(self) -> None:
        code, output = self.run_main("--version")
        self.assertEqual(code, 0)
        self.assertEqual(
            output.strip(),
            f"glyphlens-worker {glyphlens_worker.__version__} "
            f"(protocol {glyphlens_worker.PROTOCOL_VERSION})",
        )

    def test_health_prints_json_report(self) -> None:
        code, output = self.run_main("--health")
        self.assertEqual(code, 0)
        report = json.loads(output)
        self.assertEqual(report["version"], glyphlens_worker.__version__)
        self.assertEqual(report["protocolVersion"], glyphlens_worker.PROTOCOL_VERSION)

    def test_health_reports_engines_as_not_implemented(self) -> None:
        report = worker_main.health_report()
        self.assertEqual(report["status"], "unavailable")
        self.assertEqual(report["transport"], "not_implemented")
        self.assertEqual(
            report["engines"],
            {"ocr": "not_implemented", "language_detection": "not_implemented",
             "translation": "not_implemented"},
        )

    def test_requires_exactly_one_option(self) -> None:
        for argv in ([], ["--version", "--health"]):
            with self.subTest(argv=argv), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    worker_main.main(argv)
                self.assertEqual(raised.exception.code, 2)

    def test_module_entry_point_runs_as_subprocess(self) -> None:
        completed = subprocess.run(
            [sys.executable, "-m", "glyphlens_worker", "--version"],
            cwd=WORKER_ROOT,
            capture_output=True,
            text=True,
            timeout=60,
            check=False,
        )
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertTrue(re.match(r"^glyphlens-worker \d+\.\d+\.\d+ ", completed.stdout))


if __name__ == "__main__":
    unittest.main()
