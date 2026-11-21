"""Tests that use the real CTranslate2 and SentencePiece libraries (but no real model).

They are skipped when the libraries are not installed, which is the case in CI (standard library only). The real-model
test, which needs a downloaded package, is in ``test_real_model.py``.
"""

from __future__ import annotations

import importlib.util
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from typing import Any

from glyphlens_worker.config.settings import ModelsLocation
from glyphlens_worker.engines.translation.backend import Ct2RuntimeLoader, Ct2Translator
from glyphlens_worker.engines.translation.service import TranslationService

from tests.support import make_package

WORKER_ROOT = Path(__file__).resolve().parents[1]
HAVE_RUNTIME = all(importlib.util.find_spec(name) is not None for name in ("ctranslate2", "sentencepiece"))
HAVE_SENTENCEPIECE = importlib.util.find_spec("sentencepiece") is not None


class RecordingCt2Translator:
    """Stands in for ``ctranslate2.Translator``: returns the source tokens (or ``output``) and records how it was called."""

    class _Result:
        def __init__(self, tokens: list[str]) -> None:
            self.hypotheses = [tokens]

    def __init__(self, output: list[str] | None = None) -> None:
        self.output = output
        self.calls: list[dict[str, Any]] = []

    def translate_batch(self, tokens: list[list[str]], **kwargs: Any) -> list[_Result]:
        self.calls.append({"tokens": tokens, **kwargs})
        return [self._Result(list(sequence) if self.output is None else list(self.output)) for sequence in tokens]


def train_tokenizer() -> Any:
    """A tiny real SentencePiece model, trained on the spot (no file needed)."""
    import sentencepiece

    corpus = [
        "Hello world, this is a small test of the tokenizer.",
        "Where is the nearest station? I have lost my ticket.",
        "Save as the file. Open the printer settings and restart the application.",
        "The meeting tomorrow morning is postponed to three o'clock. Please confirm.",
        "Good morning everyone, thank you for your understanding and your patience.",
    ] * 4
    model = io.BytesIO()
    sentencepiece.SentencePieceTrainer.train(
        sentence_iterator=iter(corpus),
        model_writer=model,
        vocab_size=90,
        model_type="unigram",
        hard_vocab_limit=False,
        character_coverage=1.0,
        minloglevel=2,  # the trainer is chatty on stderr
    )
    return sentencepiece.SentencePieceProcessor(model_proto=model.getvalue())


@unittest.skipUnless(HAVE_SENTENCEPIECE, "needs sentencepiece")
class Ct2TranslatorAdapterTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.processor = train_tokenizer()

    def make(self, prefix: str = "", output: list[str] | None = None) -> tuple[Ct2Translator, RecordingCt2Translator]:
        engine = RecordingCt2Translator(output)
        return Ct2Translator(engine, self.processor, prefix), engine

    def test_tokens_are_counted_with_the_real_tokenizer(self) -> None:
        translator, _ = self.make()

        self.assertGreater(translator.count_tokens("Hello world, this is a small test."), 5)
        self.assertGreater(translator.count_tokens("Hello world, this is a small test of the tokenizer."), translator.count_tokens("Hello world"))
        self.assertEqual(translator.count_tokens(""), 0)

    def test_the_batch_is_translated_in_one_call_with_argos_decoding_parameters(self) -> None:
        translator, engine = self.make()

        translator.translate(["Hello world.", "Save as the file.", "Where is the station?"])

        self.assertEqual(len(engine.calls), 1)
        call = engine.calls[0]
        self.assertEqual(len(call["tokens"]), 3)
        self.assertEqual(
            {key: call[key] for key in ("beam_size", "num_hypotheses", "length_penalty", "max_batch_size", "batch_type", "replace_unknowns", "target_prefix")},
            {"beam_size": 4, "num_hypotheses": 1, "length_penalty": 0.2, "max_batch_size": 32, "batch_type": "tokens", "replace_unknowns": True, "target_prefix": None},
        )

    def test_decoding_restores_the_text_from_pieces(self) -> None:
        translator, _ = self.make()

        decoded = translator.translate(["Hello world, this is a small test.", "Where is the nearest station?"])

        self.assertEqual(decoded, ["Hello world, this is a small test.", "Where is the nearest station?"])

    def test_the_sentencepiece_space_marker_never_leaks_into_the_output(self) -> None:
        translator, _ = self.make(output=["▁Hello", "▁▁", "▁world", "▁"])

        decoded = translator.translate(["x"])[0]

        self.assertNotIn("▁", decoded)
        self.assertEqual(decoded.split(), ["Hello", "world"])

    def test_a_target_prefix_is_sent_and_removed_from_the_output(self) -> None:
        translator, engine = self.make(prefix="__en__", output=["__en__", "▁Hello"])

        decoded = translator.translate(["Bonjour", "Salut"])

        self.assertEqual(engine.calls[0]["target_prefix"], [["__en__"], ["__en__"]])
        self.assertEqual(decoded, ["Hello", "Hello"])

    def test_an_empty_batch_makes_no_call(self) -> None:
        translator, engine = self.make()

        self.assertEqual(translator.translate([]), [])
        self.assertEqual(engine.calls, [])

    def test_long_unpunctuated_text_is_chunked_by_the_real_token_count(self) -> None:
        from glyphlens_worker.engines.translation.segmentation import plan_text

        translator, _ = self.make()
        text = " ".join(["Hello world this is a small test"] * 60)  # about 400 tokens, no punctuation

        plan = plan_text(text, translator.count_tokens)

        self.assertGreater(len(plan.segments), 2)
        self.assertTrue(all(translator.count_tokens(segment) <= 120 for segment in plan.segments))


@unittest.skipUnless(HAVE_RUNTIME, "needs ctranslate2 and sentencepiece")
class RealRuntimeTests(unittest.TestCase):
    def setUp(self) -> None:
        self._temp = tempfile.TemporaryDirectory()
        self.addCleanup(self._temp.cleanup)
        self.temp = Path(self._temp.name)

    def test_the_runtime_loads_and_reports_the_ctranslate2_version(self) -> None:
        import ctranslate2

        loader = Ct2RuntimeLoader()

        self.assertTrue(loader.is_present())
        self.assertEqual(loader.load().version, ctranslate2.__version__)

    def test_a_package_the_engine_cannot_load_fails_its_items_and_is_reported(self) -> None:
        service = TranslationService(ModelsLocation(self.temp / "models"))  # the real runtime loader
        service.install({"path": str(make_package(self.temp / "fr.argosmodel"))})  # fake weights: not a real model

        response = service.translate({"targetLanguage": "en", "items": [{"id": "a", "text": "Bonjour", "sourceLanguage": "fr"}]})
        listing = service.list_models(None)

        self.assertEqual(response["results"][0]["error"]["code"], "translation_failed")
        self.assertIn("could not be loaded", response["results"][0]["error"]["message"])
        self.assertEqual(listing["state"], "installed")
        self.assertTrue(listing["engineVersion"])
        self.assertTrue(any("fr_en" in problem for problem in listing["problems"]))
        self.assertEqual(service.health().translation, "ready")  # the runtime itself works

    def test_importing_the_runtime_in_a_guarded_worker_makes_no_network_attempt_and_never_imports_argos(self) -> None:
        code = (
            "import sys, json\n"
            "from glyphlens_worker.runtime import network_guard\n"
            "network_guard.install()\n"
            "from glyphlens_worker.engines.translation.backend import Ct2RuntimeLoader\n"
            "runtime = Ct2RuntimeLoader().load()\n"
            "print(json.dumps({'attempts': network_guard.blocked_attempts(), 'argos': 'argostranslate' in sys.modules, "
            "'heavy': [m for m in ('torch', 'spacy', 'stanza') if m in sys.modules], 'version': runtime.version}))\n"
        )

        completed = subprocess.run([sys.executable, "-c", code], cwd=WORKER_ROOT, capture_output=True, text=True, timeout=120, check=False)

        self.assertEqual(completed.returncode, 0, completed.stderr)
        report = json.loads(completed.stdout)
        self.assertEqual(report["attempts"], [])
        self.assertFalse(report["argos"])
        self.assertEqual(report["heavy"], [])
        self.assertTrue(report["version"])


if __name__ == "__main__":
    unittest.main()
