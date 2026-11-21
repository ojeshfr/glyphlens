"""Tests for splitting texts into model segments and reassembling the translations."""

from __future__ import annotations

import random
import unittest

from glyphlens_worker.engines.translation.segmentation import MAX_SEGMENT_TOKENS, assemble, plan_text


def words(text: str) -> int:
    """A stand-in for the tokenizer: one token per whitespace-separated word."""
    return len(text.split())


def squeeze(text: str) -> str:
    return "".join(text.split())


class LineAndSentenceTests(unittest.TestCase):
    def test_a_single_short_line_is_one_segment(self) -> None:
        plan = plan_text("Enregistrer sous...", words)

        self.assertEqual(plan.segments, ("Enregistrer sous...",))
        self.assertEqual(assemble(plan, plan.segments), "Enregistrer sous...")

    def test_sentences_are_split_after_terminators_followed_by_whitespace(self) -> None:
        plan = plan_text("Bonjour tout le monde. Comment allez-vous ? Très bien ! Et vous… Moi aussi", words)

        self.assertEqual(
            plan.segments,
            ("Bonjour tout le monde.", "Comment allez-vous ?", "Très bien !", "Et vous…", "Moi aussi"),
        )

    def test_cjk_terminators_split_when_whitespace_follows(self) -> None:
        plan = plan_text("今日は晴れです。 明日は雨です！ 本当？ はい", words)

        self.assertEqual(len(plan.segments), 4)

    def test_chinese_sentences_split_without_any_whitespace_between_them(self) -> None:
        plan = plan_text("随着技术的发展，企业开始使用它。这提高了效率！你觉得呢？因此我们需要制度。", words)

        self.assertEqual(
            plan.segments,
            ("随着技术的发展，企业开始使用它。", "这提高了效率！", "你觉得呢？", "因此我们需要制度。"),
        )

    def test_a_closing_quote_stays_with_its_sentence(self) -> None:
        plan = plan_text("他说：「我不去了。」然后就走了。", words)

        self.assertEqual(plan.segments, ("他说：「我不去了。」", "然后就走了。"))

    def test_no_split_without_following_whitespace(self) -> None:
        for text in ("file.txt", "3.14 is pi", "www.example.com/path", "a.b.c"):
            with self.subTest(text=text):
                self.assertEqual(len(plan_text(text, words).segments), 1)

    def test_line_breaks_are_preserved_exactly(self) -> None:
        text = "Ligne un\r\nLigne deux\nLigne trois\rFin"
        plan = plan_text(text, words)

        self.assertEqual(plan.segments, ("Ligne un", "Ligne deux", "Ligne trois", "Fin"))
        self.assertEqual(assemble(plan, plan.segments), text)

    def test_blank_lines_and_whitespace_only_lines_are_kept_and_not_translated(self) -> None:
        text = "Un\n\n   \nDeux\n"
        plan = plan_text(text, words)

        self.assertEqual(plan.segments, ("Un", "Deux"))
        self.assertEqual(assemble(plan, ["A", "B"]), "A\n\n   \nB\n")

    def test_lines_without_a_letter_or_digit_are_not_translated(self) -> None:
        text = "---\nTitre\n* * *\n...\n12"
        plan = plan_text(text, words)

        self.assertEqual(plan.segments, ("Titre", "12"))
        self.assertEqual(assemble(plan, ["Title", "12"]), "---\nTitle\n* * *\n...\n12")

    def test_leading_and_trailing_spaces_of_a_line_are_not_sent(self) -> None:
        self.assertEqual(plan_text("   Bonjour   \n\tSalut", words).segments, ("Bonjour", "Salut"))

    def test_the_segments_of_a_line_are_joined_by_one_space(self) -> None:
        plan = plan_text("Un. Deux. Trois.\nQuatre.", words)

        self.assertEqual(assemble(plan, ["One.", "Two.", "Three.", "Four."]), "One. Two. Three.\nFour.")

    def test_an_empty_translation_is_skipped_without_a_double_space(self) -> None:
        plan = plan_text("Un. Deux. Trois.", words)

        self.assertEqual(assemble(plan, ["One.", "", "Three."]), "One. Three.")

    def test_an_empty_text_has_no_segments(self) -> None:
        plan = plan_text("", words)

        self.assertEqual(plan.segments, ())
        self.assertEqual(assemble(plan, []), "")


class ChunkingTests(unittest.TestCase):
    def assert_bounded_and_lossless(self, text: str, limit: int, count=words) -> list[str]:  # type: ignore[no-untyped-def]
        segments = list(plan_text(text, count, limit).segments)
        self.assertTrue(all(count(segment) <= limit for segment in segments), [count(s) for s in segments])
        self.assertEqual(squeeze("".join(segments)), squeeze(text), "no text may be lost or duplicated")
        return segments

    def test_a_segment_within_the_limit_is_not_cut(self) -> None:
        self.assertEqual(plan_text("one two three four five", words, 5).segments, ("one two three four five",))

    def test_a_long_sentence_is_cut_at_punctuation_first(self) -> None:
        text = "alpha beta gamma, delta epsilon zeta; eta theta iota: kappa lambda mu"

        segments = self.assert_bounded_and_lossless(text, 5)  # three-word clauses: two never fit together

        self.assertEqual(segments, ["alpha beta gamma,", "delta epsilon zeta;", "eta theta iota:", "kappa lambda mu"])

    def test_small_clauses_are_packed_together_up_to_the_limit(self) -> None:
        segments = self.assert_bounded_and_lossless("a b, c d, e f, g h, i j", 5)

        self.assertEqual(segments, ["a b, c d,", "e f, g h,", "i j"])

    def test_without_punctuation_it_cuts_at_spaces(self) -> None:
        text = " ".join(f"w{n}" for n in range(23))

        segments = self.assert_bounded_and_lossless(text, 5)

        self.assertEqual([words(segment) for segment in segments], [5, 5, 5, 5, 3])

    def test_a_clause_that_is_itself_too_long_is_cut_at_spaces(self) -> None:
        text = "short one, " + " ".join(f"w{n}" for n in range(12)) + ", tail"

        segments = self.assert_bounded_and_lossless(text, 5)

        self.assertEqual(segments[0], "short one,")
        self.assertEqual(segments[-1], "tail")

    def test_text_without_spaces_is_cut_into_blocks_of_characters(self) -> None:
        text = "今日は晴れです" * 40  # 280 characters, no spaces

        def one_token_per_two_characters(value: str) -> int:
            return (len(value) + 1) // 2

        segments = self.assert_bounded_and_lossless(text, 20, one_token_per_two_characters)

        self.assertGreater(len(segments), 5)

    def test_the_default_limit_keeps_every_segment_far_below_the_ctranslate2_limits(self) -> None:
        text = ("word " * 900).strip()  # 4499 characters, no punctuation: the worst case for sentence splitting

        segments = list(plan_text(text, words).segments)

        self.assertGreater(len(segments), 1)
        self.assertTrue(all(words(segment) <= MAX_SEGMENT_TOKENS for segment in segments))
        self.assertLess(MAX_SEGMENT_TOKENS, 1024)

    def test_a_single_unsplittable_token_over_the_limit_terminates(self) -> None:
        segments = list(plan_text("x" * 1000, lambda value: len(value), 7).segments)

        self.assertTrue(all(len(segment) <= 7 for segment in segments))
        self.assertEqual("".join(segments), "x" * 1000)

    def test_random_texts_are_always_bounded_and_lossless(self) -> None:
        generator = random.Random(20260101)
        vocabulary = ["alpha", "b", "gamma", "d3", "\u00e9t\u00e9", "\u4eca\u65e5", "x" * 30, "end.", "why?", "stop!", "pause,", "colon:", "semi;"]
        for _ in range(300):
            lines = [" ".join(generator.choice(vocabulary) for _ in range(generator.randint(0, 60))) for _ in range(generator.randint(1, 4))]
            separator = generator.choice(["\n", "\r\n"])
            text = separator.join(lines)

            plan = plan_text(text, words, 8)

            self.assertTrue(all(words(segment) <= 8 for segment in plan.segments), text)
            self.assertEqual(squeeze("".join(plan.segments)), squeeze("".join(line for line in lines if any(ch.isalnum() for ch in line))))
            self.assertEqual(assemble(plan, plan.segments).count("\n"), text.count("\n"))


if __name__ == "__main__":
    unittest.main()
