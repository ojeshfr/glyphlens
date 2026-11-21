"""The contraction cleanup applied to decoded English (Moses-style tokens from the Chinese models)."""

from __future__ import annotations

import unittest

from glyphlens_worker.engines.translation.backend import _tidy_contractions


class TidyContractionTests(unittest.TestCase):
    def test_contractions_split_into_tokens_are_rejoined(self) -> None:
        cases = {
            "Tomorrow ' s meeting has been moved": "Tomorrow's meeting has been moved",
            "I don ' t know": "I don't know",
            "They ' re here": "They're here",
            "We ' ll see, I ' d say so, I ' m sure, you ' ve won": "We'll see, I'd say so, I'm sure, you've won",
        }
        for text, expected in cases.items():
            with self.subTest(text=text):
                self.assertEqual(_tidy_contractions(text), expected)

    def test_text_without_the_artifact_is_untouched(self) -> None:
        for text in ("It's fine", "'quoted' text", "rock 'n' roll", "He said ' hello '", "The students' books", "Hello world"):
            with self.subTest(text=text):
                self.assertEqual(_tidy_contractions(text), text)


if __name__ == "__main__":
    unittest.main()
