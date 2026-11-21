"""Tests for script-based identification (Chinese first): Simplified versus Traditional, mixed text, Japanese, Korean, English."""

from __future__ import annotations

import unittest

from glyphlens_worker.engines.language_detection.script import analyze, has_cjk, identify_script


class ChineseScriptTests(unittest.TestCase):
    def test_simplified_only_characters_make_the_text_simplified(self) -> None:
        for text in ("我们今天去吃饭吧", "这个软件的设置在哪里？", "谢谢你", "发送", "@user_123 你在吗"):
            with self.subTest(text=text):
                self.assertEqual(identify_script(text), "zh")

    def test_traditional_only_characters_make_the_text_traditional(self) -> None:
        for text in ("我們今天去吃飯吧", "這個軟體的設定在哪裡？", "謝謝你", "發送"):
            with self.subTest(text=text):
                self.assertEqual(identify_script(text), "zt")

    def test_text_made_only_of_shared_characters_is_reported_as_simplified(self) -> None:
        # 你好 exists in both national character sets: the primary target (Simplified) is the answer.
        self.assertEqual(identify_script("你好"), "zh")
        self.assertEqual(identify_script("好的"), "zh")
        found = analyze("你好")
        self.assertEqual((found.han, found.simplified_only, found.traditional_only), (2, 0, 0))

    def test_the_variant_with_more_evidence_wins_in_mixed_script_text(self) -> None:
        self.assertEqual(identify_script("我們今天去吃饭了，谢谢谢谢"), "zh")
        self.assertEqual(identify_script("我們今天去吃飯了，謝謝謝謝 谢"), "zt")

    def test_chinese_mixed_with_latin_text_digits_and_urls_is_chinese(self) -> None:
        for text in (
            "我今天用了 iPhone 15 Pro，很不错",
            "Hello 你好",
            "https://example.com 点击这里",
            "会议在 10:30 开始，请带上 laptop",
        ):
            with self.subTest(text=text):
                self.assertEqual(identify_script(text), "zh")

    def test_an_english_sentence_with_one_stray_ideograph_is_not_chinese(self) -> None:
        text = "Please remember to bring the documents and the laptop to the meeting tomorrow morning 好"
        self.assertNotEqual(identify_script(text), "zh")

    def test_kana_means_japanese_and_hangul_means_korean(self) -> None:
        self.assertEqual(identify_script("こんにちは世界"), "ja")
        self.assertEqual(identify_script("今日は天気がいいですね"), "ja")
        self.assertEqual(identify_script("안녕하세요"), "ko")

    def test_chinese_punctuation_and_full_width_forms_do_not_change_the_answer(self) -> None:
        self.assertEqual(identify_script("你好！你吃饭了吗？"), "zh")
        self.assertEqual(identify_script("「這是引用」"), "zt")

    def test_a_single_cjk_character_is_a_complete_word(self) -> None:
        self.assertTrue(has_cjk("好"))
        self.assertEqual(identify_script("好"), "zh")
        self.assertEqual(identify_script("們"), "zt")
        self.assertFalse(has_cjk("a"))


class OtherScriptsTests(unittest.TestCase):
    def test_numbers_symbols_and_unknown_latin_text_are_left_to_a_statistical_detector(self) -> None:
        for text in ("12:30", "###", "Bonjour le monde", "Settings", "OK", "@user_123"):
            with self.subTest(text=text):
                self.assertIsNone(identify_script(text))

    def test_plain_english_is_recognised_without_a_library_and_only_then(self) -> None:
        text = "The weather is good and it is a nice day"

        self.assertEqual(identify_script(text), "en")
        self.assertIsNone(identify_script(text, allow_english=False))  # with a statistical library, it decides English too

    def test_english_needs_several_function_words_and_pure_ascii_letters(self) -> None:
        self.assertIsNone(identify_script("File Edit View"))
        self.assertIsNone(identify_script("the café"))
        self.assertIsNone(identify_script("Le chat est sur la table"))

    def test_analysis_counts_scripts(self) -> None:
        found = analyze("我们 hello こんにちは 안녕")

        self.assertEqual((found.han, found.simplified_only, found.kana, found.hangul), (2, 1, 5, 2))


if __name__ == "__main__":
    unittest.main()
