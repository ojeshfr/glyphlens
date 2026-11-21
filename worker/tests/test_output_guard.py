"""The hallucination guard: a translation containing placeholders that the source never had is rejected, not shown."""

from __future__ import annotations

import unittest

from glyphlens_worker.engines.translation.service import _run_hop, has_unexpected_placeholder


class FakeTranslator:
    """Translates by a fixed table, one output per segment (what a real model returns, minus the model)."""

    def __init__(self, table: dict[str, str]) -> None:
        self._table = table

    def count_tokens(self, text: str) -> int:
        return len(text.split()) or 1

    def translate(self, segments):  # type: ignore[no-untyped-def]
        return [self._table.get(segment, segment) for segment in segments]


class PlaceholderTests(unittest.TestCase):
    def test_placeholders_in_the_output_but_not_in_the_source_are_unexpected(self) -> None:
        for output in ("Could not close temporary folder: %s", "Error %d occurred", "Hello {0}", "Value {name}", "Item %1$s", "50%s"):
            with self.subTest(output=output):
                self.assertTrue(has_unexpected_placeholder("网络连接失败，请检查您的网络设置。", output))

    def test_placeholders_that_the_source_has_are_kept(self) -> None:
        self.assertFalse(has_unexpected_placeholder("无法关闭临时文件夹：%s", "Could not close temporary folder: %s"))
        self.assertFalse(has_unexpected_placeholder("你好 {0}", "Hello {0}"))

    def test_ordinary_text_percent_signs_and_braces_are_not_placeholders(self) -> None:
        for output in ("Budget used: 60%", "100% sure", "Use {braces} sparingly?", "Save and exit", "It costs 5% more. Really?"):
            with self.subTest(output=output):
                self.assertEqual(has_unexpected_placeholder("预算已使用 60%", output), output == "Use {braces} sparingly?")


class RunHopTests(unittest.TestCase):
    def test_a_text_whose_translation_is_a_hallucinated_placeholder_string_has_no_translation(self) -> None:
        translator = FakeTranslator({"网络连接失败": "Could not close temporary folder: %s", "你好": "Hello"})

        outputs = _run_hop(translator, ["你好", "网络连接失败", "你好"])  # type: ignore[arg-type]

        self.assertEqual(outputs, ["Hello", None, "Hello"])

    def test_one_rejected_sentence_rejects_only_its_own_text(self) -> None:
        translator = FakeTranslator({"甲。": "Alpha.", "乙。": "Beta {0}.", "丙。": "Gamma."})

        outputs = _run_hop(translator, ["甲。丙。", "乙。丙。"])  # type: ignore[arg-type]

        self.assertEqual(outputs[0], "Alpha. Gamma.")
        self.assertIsNone(outputs[1])


if __name__ == "__main__":
    unittest.main()
