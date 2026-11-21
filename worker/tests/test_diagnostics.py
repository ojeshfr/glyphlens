"""Tests for the worker package metadata and command-line diagnostics."""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import tomllib
import unittest
from pathlib import Path
from unittest import mock

import glyphlens_worker
from glyphlens_worker import main as worker_main
from glyphlens_worker.config.settings import MODELS_DIR_ENVIRONMENT_VARIABLE

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

    def test_pyproject_has_no_base_dependencies_and_pins_the_engine_extras(self) -> None:
        # The worker must start with the standard library alone; engines are optional extras (exact versions, except the ONNX Runtime lower bound).
        text = (WORKER_ROOT / "pyproject.toml").read_text(encoding="utf-8")
        self.assertRegex(text, r"(?m)^dependencies = \[\]$")

        project = tomllib.loads(text)["project"]
        self.assertEqual(
            project["optional-dependencies"],
            {
                "translation": ["ctranslate2==4.9.0", "sentencepiece==0.2.3"],
                "ocr": ["paddleocr==3.7.0", "onnxruntime>=1.20"],
                "lingua": ["lingua-language-detector==2.2.0; python_version >= '3.12'"],
                "langid": ["py3langid==0.4.0"],
            },
        )
        requirements = [requirement for group in project["optional-dependencies"].values() for requirement in group]
        self.assertFalse([requirement for requirement in requirements if "argos" in requirement.lower()])  # never a dependency


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
        self.assertEqual(report["workerVersion"], glyphlens_worker.__version__)
        self.assertEqual(report["protocolVersion"], glyphlens_worker.PROTOCOL_VERSION)

    def test_standalone_health_reports_a_running_but_unconnected_worker_without_any_ready_engine(self) -> None:
        with tempfile.TemporaryDirectory() as temp, mock.patch.dict(
            os.environ, {MODELS_DIR_ENVIRONMENT_VARIABLE: str(Path(temp) / "no-models-here")}
        ):
            report = worker_main.health_report()

        runtime_present = all(importlib.util.find_spec(name) is not None for name in ("ctranslate2", "sentencepiece"))
        self.assertEqual(report["process"], "ready")
        self.assertEqual(report["ipc"], "disconnected")
        self.assertEqual(report["protocol"], "not_initialized")
        self.assertIn(report["engines"]["ocr"], {"unavailable", "not_installed", "installed"})
        self.assertIn(report["engines"]["languageDetection"], {"unavailable", "installed"})
        self.assertEqual(report["engines"]["translation"], "not_installed" if runtime_present else "unavailable")
        self.assertNotIn("ready", report["engines"].values())
        self.assertEqual(report["models"], "not_installed")

    def test_standalone_health_imports_no_engine_library(self) -> None:
        code = (
            "import sys; from glyphlens_worker.main import health_report; health_report(); "
            "heavy = [m for m in ('ctranslate2', 'sentencepiece', 'lingua', 'py3langid', 'numpy', 'argostranslate') if m in sys.modules]; "
            "print(heavy)"
        )

        completed = subprocess.run(
            [sys.executable, "-c", code], cwd=WORKER_ROOT, capture_output=True, text=True, timeout=60, check=False
        )

        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertEqual(completed.stdout.strip(), "[]")

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
