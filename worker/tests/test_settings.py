"""Tests for the model directory settings."""

from __future__ import annotations

import os
import tempfile
import unittest
from pathlib import Path

from glyphlens_worker.config import settings
from glyphlens_worker.config.settings import ModelsLocation, resolve_models_location

APP_DATA = str(Path(tempfile.gettempdir()) / "AppData-Local")
OVERRIDE = str(Path(tempfile.gettempdir()) / "glyphlens-models-override")


class ModelsLocationTests(unittest.TestCase):
    def test_default_is_below_local_app_data(self) -> None:
        location = resolve_models_location({"LOCALAPPDATA": APP_DATA})

        self.assertEqual(location, ModelsLocation(Path(APP_DATA) / "GlyphLens" / "models"))

    def test_an_absolute_override_wins(self) -> None:
        location = resolve_models_location({settings.MODELS_DIR_ENVIRONMENT_VARIABLE: OVERRIDE, "LOCALAPPDATA": APP_DATA})

        self.assertEqual(location, ModelsLocation(Path(OVERRIDE)))

    def test_a_relative_override_is_ignored_with_a_problem_and_the_default_is_used(self) -> None:
        location = resolve_models_location({settings.MODELS_DIR_ENVIRONMENT_VARIABLE: "relative\\models", "LOCALAPPDATA": APP_DATA})

        self.assertEqual(location.path, Path(APP_DATA) / "GlyphLens" / "models")
        self.assertEqual(len(location.problems), 1)
        self.assertIn(settings.MODELS_DIR_ENVIRONMENT_VARIABLE, location.problems[0])
        self.assertNotIn("relative", location.problems[0])

    def test_unsafe_overrides_are_ignored(self) -> None:
        for value in ("\\\\server\\share\\models", "//server/share/models", "\\\\.\\pipe\\x", OVERRIDE + "\x00evil", "~", "."):
            with self.subTest(value=value):
                location = resolve_models_location({settings.MODELS_DIR_ENVIRONMENT_VARIABLE: value, "LOCALAPPDATA": APP_DATA})

                self.assertEqual(location.path, Path(APP_DATA) / "GlyphLens" / "models")
                self.assertTrue(location.problems)

    def test_an_empty_override_is_the_same_as_none(self) -> None:
        location = resolve_models_location({settings.MODELS_DIR_ENVIRONMENT_VARIABLE: "", "LOCALAPPDATA": APP_DATA})

        self.assertEqual(location, ModelsLocation(Path(APP_DATA) / "GlyphLens" / "models"))

    def test_without_any_usable_location_the_path_is_none_with_an_explanation(self) -> None:
        location = resolve_models_location({})

        self.assertIsNone(location.path)
        self.assertEqual(len(location.problems), 1)
        self.assertIn("LOCALAPPDATA", location.problems[0])

    def test_a_relative_local_app_data_is_not_trusted(self) -> None:
        self.assertIsNone(resolve_models_location({"LOCALAPPDATA": "AppData\\Local"}).path)

    def test_nothing_is_created_when_resolving(self) -> None:
        with tempfile.TemporaryDirectory() as temp:
            target = Path(temp) / "never-created"

            resolve_models_location({settings.MODELS_DIR_ENVIRONMENT_VARIABLE: str(target)})

            self.assertFalse(target.exists())

    def test_the_process_environment_is_used_by_default(self) -> None:
        previous = os.environ.get(settings.MODELS_DIR_ENVIRONMENT_VARIABLE)
        os.environ[settings.MODELS_DIR_ENVIRONMENT_VARIABLE] = OVERRIDE
        try:
            self.assertEqual(resolve_models_location().path, Path(OVERRIDE))
        finally:
            if previous is None:
                del os.environ[settings.MODELS_DIR_ENVIRONMENT_VARIABLE]
            else:
                os.environ[settings.MODELS_DIR_ENVIRONMENT_VARIABLE] = previous


if __name__ == "__main__":
    unittest.main()
