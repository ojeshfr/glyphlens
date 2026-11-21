"""Tests for the language.detect service: validation, result shaping and backend selection, with fake backends.

The real libraries are tested at the end and skipped when they are not installed.
"""

from __future__ import annotations

import importlib.util
import json
import sys
import unittest
from typing import Any

from glyphlens_worker.engines.language_detection.backends import BackendSpec, DEFAULT_BACKENDS
from glyphlens_worker.engines.language_detection.service import LanguageDetectionService
from glyphlens_worker.errors import ServiceError

from tests.support import FakeDetectorBackend, fake_backend_spec


def service_with(backend: FakeDetectorBackend | None = None, **kwargs: Any) -> LanguageDetectionService:
    return LanguageDetectionService([fake_backend_spec(backend, **kwargs)])


def detect(service: LanguageDetectionService, texts: list[str], **extra: Any) -> dict[str, Any]:
    return service.detect({"texts": texts, **extra})


class ShapingTests(unittest.TestCase):
    def test_a_detected_language_has_the_protocol_shape_with_the_libraries_own_probabilities(self) -> None:
        result = detect(service_with(), ["Hallo Welt, wie geht es dir?"])

        self.assertEqual(
            result["results"],
            [
                {
                    "language": "de",
                    "confidence": 0.9,
                    "alternatives": [
                        {"language": "nl", "confidence": 0.07},
                        {"language": "sv", "confidence": 0.02},
                        {"language": "da", "confidence": 0.01},
                    ],
                    "issue": "none",
                }
            ],
        )
        self.assertEqual(json.loads(json.dumps(result)), result)

    def test_the_detector_is_named_with_its_version(self) -> None:
        result = detect(service_with(name="fake"), ["Hallo Welt"])

        self.assertRegex(result["detector"], r"^script\+fake \S+$")

    def test_results_keep_the_order_and_count_of_the_texts(self) -> None:
        backend = FakeDetectorBackend()
        texts = ["Hallo Welt", "12345", "ab", "", "   ", "!!!", "Guten Tag", "你好世界"]

        results = detect(service_with(backend), texts)["results"]

        self.assertEqual(len(results), len(texts))
        self.assertEqual([r["issue"] for r in results], ["none", "no_text", "too_short", "no_text", "no_text", "no_text", "none", "none"])
        self.assertEqual([call[0] for call in backend.calls], ["Hallo Welt", "Guten Tag"])  # Chinese is decided by rule

    def test_texts_without_letters_or_with_fewer_than_three_are_not_sent_to_the_detector(self) -> None:
        backend = FakeDetectorBackend()

        results = detect(service_with(backend), ["", "  ", "123 456", "...", "a", "ab", "a1", "a b", "abc"])["results"]

        self.assertEqual([r["issue"] for r in results], ["no_text"] * 4 + ["too_short"] * 4 + ["none"])
        self.assertEqual([call[0] for call in backend.calls], ["abc"])
        for undetermined in results[:-1]:
            self.assertEqual(undetermined, {"language": None, "confidence": None, "alternatives": [], "issue": undetermined["issue"]})

    def test_letters_are_unicode_letters(self) -> None:
        backend = FakeDetectorBackend()

        results = detect(service_with(backend), ["éèê", "今日", "अआइ", "กขฃ"])["results"]

        self.assertEqual([r["issue"] for r in results], ["none", "none", "none", "none"])  # two CJK characters are a whole word

    def test_the_number_of_alternatives_is_limited(self) -> None:
        backend = FakeDetectorBackend()
        service = service_with(backend)

        zero = detect(service, ["Hallo Welt"], maxAlternatives=0)["results"][0]
        one = detect(service, ["Hallo Welt"], maxAlternatives=1)["results"][0]
        five = detect(service, ["Hallo Welt"], maxAlternatives=5)["results"][0]
        default = detect(service, ["Hallo Welt"])["results"][0]

        self.assertEqual([len(r["alternatives"]) for r in (zero, one, five, default)], [0, 1, 3, 3])  # the fake only knows 3 more
        self.assertEqual([call[1] for call in backend.calls], [1, 2, 6, 4])  # one more than the alternatives wanted

    def test_the_confidence_is_null_when_the_library_reports_none_and_is_never_invented(self) -> None:
        backend = FakeDetectorBackend([("de", None), ("nl", None)])

        result = detect(service_with(backend), ["Hallo Welt"])["results"][0]

        self.assertEqual(result["language"], "de")
        self.assertIsNone(result["confidence"])
        self.assertEqual(result["alternatives"], [])  # an alternative without a probability cannot be reported

    def test_probabilities_are_kept_within_zero_and_one_and_nan_becomes_null(self) -> None:
        backend = FakeDetectorBackend([("de", 1.0000000002), ("nl", 0.5), ("sv", float("nan")), ("da", -1e-12)])

        result = detect(service_with(backend), ["Hallo Welt"])["results"][0]

        self.assertEqual(result["confidence"], 1.0)
        self.assertEqual(result["alternatives"], [{"language": "nl", "confidence": 0.5}])

    def test_alternatives_with_zero_probability_are_dropped(self) -> None:
        backend = FakeDetectorBackend([("de", 1.0), ("nl", 0.0)])

        self.assertEqual(detect(service_with(backend), ["Hallo Welt"])["results"][0]["alternatives"], [])

    def test_a_detector_with_no_answer_is_issue_unknown(self) -> None:
        result = detect(service_with(FakeDetectorBackend([])), ["Hallo Welt"])["results"][0]

        self.assertEqual(result, {"language": None, "confidence": None, "alternatives": [], "issue": "unknown"})

    def test_no_uncertainty_threshold_is_applied(self) -> None:
        result = detect(service_with(FakeDetectorBackend([("de", 0.02), ("nl", 0.019)])), ["Hallo Welt"])["results"][0]

        self.assertEqual((result["language"], result["confidence"], result["issue"]), ("de", 0.02, "none"))

    def test_codes_the_wire_would_reject_are_not_passed_on(self) -> None:
        bad_top = detect(service_with(FakeDetectorBackend([("DE_de", 0.9)])), ["Hallo Welt"])["results"][0]
        bad_alternative = detect(service_with(FakeDetectorBackend([("de", 0.9), ("x", 0.05), ("nl", 0.04)])), ["Hallo Welt"])["results"][0]

        self.assertEqual(bad_top["issue"], "unknown")
        self.assertEqual(bad_alternative["alternatives"], [{"language": "nl", "confidence": 0.04}])


class RequestValidationTests(unittest.TestCase):
    def invalid(self, payload: object) -> ServiceError:
        with self.assertRaises(ServiceError) as raised:
            service_with().detect(payload)
        self.assertEqual(raised.exception.code, "invalid_request")
        return raised.exception

    def test_the_payload_must_be_an_object_with_a_list_of_texts(self) -> None:
        for payload in (None, [], "x", 5, {}, {"texts": "Hallo"}, {"texts": None}, {"texts": {"a": "b"}}):
            with self.subTest(payload=payload):
                self.invalid(payload)

    def test_every_text_must_be_a_string_of_at_most_4096_characters(self) -> None:
        for bad in (None, 5, ["a"], {"a": 1}, "x" * 4097):
            with self.subTest(bad=str(bad)[:10]):
                self.invalid({"texts": ["Hallo Welt", bad]})

    def test_the_limits_are_inclusive(self) -> None:
        service = service_with()

        results = detect(service, ["a" * 4096] * 64)["results"]

        self.assertEqual(len(results), 64)
        self.invalid({"texts": ["abc"] * 65})

    def test_the_limit_counts_code_points(self) -> None:
        self.assertEqual(len(detect(service_with(), ["\U0001F600" * 4096])["results"]), 1)

    def test_text_that_is_not_valid_unicode_is_refused_instead_of_failing_the_whole_request_later(self) -> None:
        self.invalid({"texts": ["Hallo Welt", "broken \ud800 text"]})  # a lone surrogate cannot be encoded

    def test_max_alternatives_must_be_an_integer_from_0_to_5(self) -> None:
        for bad in (-1, 6, 1.5, "3", True, [], {}):
            with self.subTest(bad=bad):
                self.invalid({"texts": ["abc"], "maxAlternatives": bad})
        for good in (0, 5, None):
            with self.subTest(good=good):
                detect(service_with(), ["abc"], maxAlternatives=good)

    def test_no_texts_is_an_empty_result(self) -> None:
        self.assertEqual(detect(service_with(), [])["results"], [])

    def test_messages_never_repeat_the_text(self) -> None:
        secret = "TOP-SECRET-SCREEN-TEXT"
        for payload in ({"texts": [secret * 300]}, {"texts": [secret, 5]}, {"texts": [secret], "maxAlternatives": secret}):
            with self.subTest(payload=str(payload)[:30]):
                self.assertNotIn(secret, self.invalid(payload).message)

    def test_a_malformed_request_is_refused_before_a_library_is_loaded(self) -> None:
        loads: list[int] = []
        spec = BackendSpec("fake", "none", "json", (3, 0), "pip install fake", lambda: loads.append(1) or FakeDetectorBackend())  # type: ignore[arg-type,return-value]

        with self.assertRaises(ServiceError):
            LanguageDetectionService([spec]).detect({"texts": "x"})

        self.assertEqual(loads, [])


class LoadingAndStateTests(unittest.TestCase):
    def test_the_library_is_loaded_once_on_the_first_request_and_reused(self) -> None:
        loads: list[int] = []
        backend = FakeDetectorBackend()
        spec = BackendSpec("fake", "none", "json", (3, 0), "pip install fake", lambda: loads.append(1) or backend)  # type: ignore[arg-type,return-value]
        service = LanguageDetectionService([spec])
        self.assertEqual(loads, [])  # not at construction

        detect(service, ["Hallo Welt"])
        detect(service, ["Guten Tag"])

        self.assertEqual(loads, [1])

    def test_state_is_installed_until_a_request_was_answered_then_ready(self) -> None:
        service = service_with()

        self.assertEqual(service.state(), "installed")
        detect(service, ["Hallo Welt"])
        self.assertEqual(service.state(), "ready")

    def test_state_never_imports_the_library(self) -> None:
        loads: list[int] = []
        spec = BackendSpec("fake", "none", "json", (3, 0), "pip install fake", lambda: loads.append(1) or FakeDetectorBackend())  # type: ignore[arg-type,return-value]

        LanguageDetectionService([spec]).state()

        self.assertEqual(loads, [])

    def test_without_a_library_the_script_rules_still_answer_and_everything_else_is_unknown(self) -> None:
        service = LanguageDetectionService([fake_backend_spec(module="glyphlens_no_such_module")])

        self.assertEqual(service.state(), "installed")
        result = detect(service, ["我们今天去吃饭吧", "我們今天去吃飯吧", "Hallo Welt", "The weather is good and it is a nice day"])

        self.assertEqual(result["detector"], "script")
        self.assertEqual([r["language"] for r in result["results"]], ["zh", "zt", None, "en"])
        self.assertEqual([r["issue"] for r in result["results"]], ["none", "none", "unknown", "none"])
        self.assertEqual(result["results"][0]["confidence"], None)  # a rule has no probability, and none is invented
        self.assertEqual(service.state(), "ready")

    def test_the_default_backends_are_lingua_on_python_3_12_and_py3langid_as_the_fallback(self) -> None:
        self.assertEqual([spec.name for spec in DEFAULT_BACKENDS], ["lingua", "py3langid"])
        self.assertIn("py3langid==0.4.0", DEFAULT_BACKENDS[1].requirement)
        self.assertEqual(DEFAULT_BACKENDS[0].usable_here(), sys.version_info >= (3, 12))

    def test_a_library_that_needs_a_newer_python_is_not_used(self) -> None:
        future = BackendSpec("future", "none", "json", (99, 0), "pip install future", lambda: FakeDetectorBackend())
        service = LanguageDetectionService([future])

        self.assertEqual(detect(service, ["Hallo Welt"])["results"][0]["issue"], "unknown")
        self.assertEqual(service.state(), "ready")

    def test_the_first_usable_library_wins_and_a_library_that_fails_to_load_falls_back_to_the_next(self) -> None:
        def broken() -> FakeDetectorBackend:
            raise MemoryError("simulated failure with a secret path")

        fallback = FakeDetectorBackend([("fr", 0.8)])
        service = LanguageDetectionService([
            BackendSpec("broken", "none", "json", (3, 0), "pip install broken", broken),
            BackendSpec("fallback", "none", "json", (3, 0), "pip install fallback", lambda: fallback),
        ])

        result = detect(service, ["Bonjour le monde"])

        self.assertTrue(result["detector"].startswith("script+fallback "))
        self.assertEqual(result["results"][0]["language"], "fr")

    def test_when_every_library_fails_to_load_the_script_rules_still_work_and_nothing_leaks(self) -> None:
        def broken() -> FakeDetectorBackend:
            raise OSError("simulated failure with a secret path")

        service = LanguageDetectionService([BackendSpec("broken", "none", "json", (3, 0), "pip install broken", broken)])

        result = detect(service, ["你好", "Hallo Welt"])

        self.assertEqual(result["detector"], "script")
        self.assertEqual([r["language"] for r in result["results"]], ["zh", None])
        self.assertNotIn("secret", str(result))


def _installed(module: str) -> bool:
    return importlib.util.find_spec(module) is not None


GERMAN = "Das ist ein ganz normaler deutscher Satz über das Wetter heute."
FRENCH = "Ceci est une phrase française tout à fait normale sur la météo."
SPANISH = "Esta es una frase española completamente normal sobre el tiempo de hoy."


class RealBackendMixin:
    spec_name = ""

    def setUp(self) -> None:  # type: ignore[override]
        spec = next(spec for spec in DEFAULT_BACKENDS if spec.name == self.spec_name)
        self.service = LanguageDetectionService([spec])

    def test_identifies_sentences_with_probabilities_and_alternatives(self) -> None:
        result = detect(self.service, [GERMAN, FRENCH, SPANISH], maxAlternatives=2)

        self.assertTrue(result["detector"].startswith(f"script+{self.spec_name} "))
        self.assertEqual([r["language"] for r in result["results"]], ["de", "fr", "es"])
        for entry in result["results"]:
            self.assertGreater(entry["confidence"], 0.5)
            self.assertLessEqual(entry["confidence"], 1.0)
            self.assertLessEqual(len(entry["alternatives"]), 2)
            self.assertNotIn(entry["language"], [a["language"] for a in entry["alternatives"]])
            confidences = [entry["confidence"], *(a["confidence"] for a in entry["alternatives"])]
            self.assertEqual(confidences, sorted(confidences, reverse=True))
        self.assertEqual(self.service.state(), "ready")

    def test_detection_covers_languages_without_an_installed_model(self) -> None:
        # Detection is built over every Argos language, not only the installed ones.
        texts = {
            "ja": "今日はとてもいい天気ですね。",
            "ru": "Сегодня очень хорошая погода.",
            "ko": "오늘은 날씨가 정말 좋습니다.",
            "it": "Oggi il tempo è davvero molto bello e soleggiato.",
        }

        results = detect(self.service, list(texts.values()))["results"]

        self.assertEqual([r["language"] for r in results], list(texts))

    def test_short_and_empty_texts_are_not_decided_by_the_library(self) -> None:
        results = detect(self.service, ["", "12 34", "ab"])["results"]

        self.assertEqual([r["issue"] for r in results], ["no_text", "no_text", "too_short"])


@unittest.skipUnless(sys.version_info >= (3, 12) and _installed("lingua"), "needs Python 3.12+ and lingua-language-detector")
class RealLinguaTests(RealBackendMixin, unittest.TestCase):
    spec_name = "lingua"


@unittest.skipUnless(_installed("py3langid"), "needs py3langid")
class RealPy3langidTests(RealBackendMixin, unittest.TestCase):
    spec_name = "py3langid"


if __name__ == "__main__":
    unittest.main()
