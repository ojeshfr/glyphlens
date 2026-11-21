"""The one real-model test: import a real Argos package and translate French to English offline.

It runs only when ``GLYPHLENS_TEST_FR_EN_MODEL`` names a ``.argosmodel`` file (``translate-fr_en``) and ctranslate2 and
sentencepiece are installed; nothing is downloaded, and the model is never part of the repository. The package is
imported into a temporary model directory, with the network guard installed, and the test fails if the worker made a single
network attempt.

    $env:GLYPHLENS_TEST_FR_EN_MODEL = 'D:\\...\\translate-fr_en-1_9.argosmodel'
"""

from __future__ import annotations

import hashlib
import importlib.util
import os
import tempfile
import time
import unittest
import zipfile
from pathlib import Path
from typing import Any

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.translation.service import TranslationService
from glyphlens_worker.runtime import network_guard

MODEL_ENVIRONMENT_VARIABLE = "GLYPHLENS_TEST_FR_EN_MODEL"
MODEL_PATH = os.environ.get(MODEL_ENVIRONMENT_VARIABLE, "")
HAVE_RUNTIME = all(importlib.util.find_spec(name) is not None for name in ("ctranslate2", "sentencepiece"))


def item(item_id: str, text: str) -> dict[str, str]:
    return {"id": item_id, "text": text, "sourceLanguage": "fr"}


@unittest.skipUnless(MODEL_PATH, f"set {MODEL_ENVIRONMENT_VARIABLE} to a translate-fr_en .argosmodel file to run this test")
@unittest.skipUnless(HAVE_RUNTIME, "needs ctranslate2 and sentencepiece")
class RealFrenchModelTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        network_guard.uninstall()
        network_guard.install()
        cls.addClassCleanup(network_guard.uninstall)
        cls._temp = tempfile.TemporaryDirectory()
        cls.addClassCleanup(cls._temp.cleanup)
        cls.models = Path(cls._temp.name) / "models"
        cls.service = TranslationService(ModelsLocation(cls.models))  # the real CTranslate2 runtime
        cls.installed = cls.service.install({"path": MODEL_PATH})

    def translate(self, *texts: str) -> list[str]:
        response = self.service.translate({"targetLanguage": "en", "items": [item(str(n), text) for n, text in enumerate(texts)]})
        for result in response["results"]:
            self.assertEqual(result["status"], "ok", result)
        return [result["translatedText"] for result in response["results"]]

    def test_the_package_was_imported_with_validated_hashes(self) -> None:
        self.assertEqual(self.installed["package"]["packageName"], "translate-fr_en")
        self.assertFalse(self.installed["replaced"])
        files = sorted(path.relative_to(self.models / "fr_en").as_posix() for path in (self.models / "fr_en").rglob("*") if path.is_file())
        self.assertIn("glyphlens-model.json", files)
        self.assertIn("model/model.bin", files)
        self.assertFalse([name for name in files if name.startswith(("stanza", "minisbd", "spacy"))], "no Stanza data may be extracted")
        with zipfile.ZipFile(MODEL_PATH) as archive:
            expected = hashlib.sha256(archive.read(next(n for n in archive.namelist() if n.endswith("model/model.bin")))).hexdigest()
        self.assertEqual(hashlib.sha256((self.models / "fr_en" / "model" / "model.bin").read_bytes()).hexdigest(), expected)

    def test_the_catalogue_and_route_are_honest(self) -> None:
        listing = self.service.list_models(None)
        status = self.service.route_status({"source": "fr", "target": "en"})
        missing = self.service.route_status({"source": "ja", "target": "en"})

        self.assertEqual((listing["state"], listing["engine"], listing["problems"]), ("installed", "ct2-argos", []))
        self.assertRegex(listing["engineVersion"], r"^\d+\.\d+\.\d+")
        self.assertEqual((status["available"], status["route"]), (True, ["fr", "en"]))
        self.assertRegex(status["modelVersion"], r"^ct2-\d+\.\d+\.\d+[^/]*/seg4\|argos:fr-en:[^:]+:[0-9a-f]{12}$")
        self.assertEqual((missing["available"], missing["issue"], missing["missingPackage"]), (False, "model_missing", "translate-ja_en"))
        self.assertEqual(self.service.health().translation, "ready")

    def test_french_is_translated_to_plausible_english(self) -> None:
        translations = self.translate(
            "Enregistrer sous...",
            "Bonjour, je voudrais r\u00e9server une table pour deux personnes ce soir.",
            "Le service est lent. Mais la nourriture est excellente !",
            "O\u00f9 est la gare la plus proche ? J'ai perdu mon billet.",
            "Param\u00e8tres de l'imprimante",
        )

        lowered = [text.lower() for text in translations]
        self.assertIn("save", lowered[0])
        self.assertTrue("table" in lowered[1] and "two" in lowered[1] and "tonight" in lowered[1], translations[1])
        self.assertTrue("slow" in lowered[2] and "food" in lowered[2] and "excellent" in lowered[2], translations[2])
        self.assertTrue("station" in lowered[3] and "ticket" in lowered[3], translations[3])
        self.assertIn("printer", lowered[4])
        for text in translations:
            self.assertNotIn("\u2581", text)

    def test_line_breaks_are_preserved(self) -> None:
        (text,) = self.translate("Ligne un\nLigne deux\r\n\r\nLigne trois")

        self.assertEqual(text.count("\n"), 3)
        self.assertEqual(text.split("\n")[0].lower(), "line one")

    def test_a_long_unpunctuated_text_is_not_silently_truncated(self) -> None:
        text = ("le chat noir mange la petite souris grise " * 60).strip()  # 480 words: CTranslate2 alone stops at about 157

        (translated,) = self.translate(text)

        self.assertGreater(len(translated.split()), 300)
        self.assertGreaterEqual(translated.lower().count("mouse"), 50)

    def test_a_batch_of_30_lines_is_translated_in_one_request(self) -> None:
        lines = ["Fichier", "\u00c9dition", "Affichage", "Insertion", "Format", "Outils", "Aide", "Enregistrer", "Annuler", "Ouvrir le fichier"] * 3

        started = time.perf_counter()
        translations = self.translate(*lines)
        elapsed = time.perf_counter() - started

        self.assertEqual(len(translations), 30)
        self.assertEqual(translations[:10], translations[10:20])
        self.assertIn("file", translations[0].lower())
        self.assertLess(elapsed, 30.0, "a 30-line batch should take seconds, not minutes")

    def test_text_is_stable_between_runs(self) -> None:
        first = self.translate("Bonjour tout le monde. Comment allez-vous ?")
        second = self.translate("Bonjour tout le monde. Comment allez-vous ?")

        self.assertEqual(first, second)

    def test_zero_network_attempts_were_made(self) -> None:
        self.translate("Bonjour")

        self.assertEqual(network_guard.blocked_attempts(), ())
        self.assertEqual(network_guard.blocked_attempt_count(), 0)

    def test_reinstalling_replaces_the_package_and_keeps_the_model_version_for_identical_files(self) -> None:
        before = self.service.route_status({"source": "fr", "target": "en"})["modelVersion"]

        second: dict[str, Any] = self.service.install({"path": MODEL_PATH})
        after = self.service.route_status({"source": "fr", "target": "en"})["modelVersion"]

        self.assertTrue(second["replaced"])
        self.assertEqual(before, after)  # the same file gives the same hash, so cached translations stay valid
        self.assertEqual(self.translate("Fichier")[0].lower(), "file")
        self.assertEqual(sorted(path.name for path in self.models.iterdir()), ["fr_en"])


if __name__ == "__main__":
    unittest.main()
