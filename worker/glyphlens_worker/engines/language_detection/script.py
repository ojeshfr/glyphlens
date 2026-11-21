"""Script-based identification: Chinese (Simplified or Traditional), Japanese and Korean, and obvious English.

This is the Chinese-first part of language identification. It needs no third-party library and no model, so it works in a
bare worker, and it is deterministic:

* **Han characters** decide Chinese. Simplified versus Traditional is decided from the characters that exist in only one
  of the two national character sets: a character the GB 2312 codec (Simplified) can encode but the Big5 codec
  (Traditional) cannot is *Simplified-only* (``们``); the reverse is *Traditional-only* (``們``). Characters in both sets
  (``你``, ``好``, ``的``) say nothing. Shared-only text is reported as Simplified (``zh``), the primary target.
* **Kana** means Japanese and **Hangul** means Korean, even when Han characters are mixed in.
* Chinese mixed with Latin text (``我用 iPhone 15 Pro``) is Chinese: the Han share of the letters decides, so an English
  sentence with one stray ideograph is not.
* A text with no CJK that is plainly English (several common English function words, nothing else) is ``en``.

The answers are rules, not probabilities, so they carry no confidence (``None``): the protocol's confidence is only ever a
library's own probability, never an invented one.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

#: Chinese is assumed when at least this share of the letters are Han characters.
MIN_HAN_SHARE = 0.2


def _ranges(*spans: tuple[int, int]) -> str:
    """A character class for code point ranges (written as numbers: escapes in source are easy to corrupt)."""
    return "[" + "".join(f"{chr(first)}-{chr(last)}" for first, last in spans) + "]"


_HAN = re.compile(_ranges((0x3400, 0x4DBF), (0x4E00, 0x9FFF), (0xF900, 0xFAFF), (0x20000, 0x2EBEF)))
_KANA = re.compile(_ranges((0x3040, 0x30FF), (0x31F0, 0x31FF), (0xFF66, 0xFF9F)))
_HANGUL = re.compile(_ranges((0x1100, 0x11FF), (0x3130, 0x318F), (0xAC00, 0xD7AF)))

_ENGLISH_WORDS = frozenset(
    "the be to of and a in that have i it for not on with he as you do at this but his by from they we say her she or an will my one "
    "all would there their what so up out if about who get which go me when make can like time no just him know take people into year "
    "your good some could them see other than then now look only come its over think also back after use two how our work first well "
    "way even new want because any these give day most us is are was were been has had did does am please thanks hello yes ok".split()
)
_WORD = re.compile(r"[A-Za-z']+")


@dataclass(frozen=True)
class ScriptAnalysis:
    """What the characters of a text say about its script."""

    han: int
    simplified_only: int
    traditional_only: int
    kana: int
    hangul: int
    letters: int


def analyze(text: str) -> ScriptAnalysis:
    han = _HAN.findall(text)
    simplified = traditional = 0
    for character in han:
        in_simplified = _encodable(character, "gb2312")
        in_traditional = _encodable(character, "big5")
        if in_simplified and not in_traditional:
            simplified += 1
        elif in_traditional and not in_simplified:
            traditional += 1
    return ScriptAnalysis(
        han=len(han),
        simplified_only=simplified,
        traditional_only=traditional,
        kana=len(_KANA.findall(text)),
        hangul=len(_HANGUL.findall(text)),
        letters=sum(1 for character in text if character.isalpha()),
    )


def identify_script(text: str, *, allow_english: bool = True) -> str | None:
    """
    ``zh``, ``zt``, ``ja``, ``ko`` (and ``en`` when ``allow_english``) when the characters settle it, else ``None`` (let a
    statistical detector decide). English is only claimed by rule when no statistical detector is installed.
    """
    found = analyze(text)
    if found.kana and found.kana + found.han >= max(1, found.letters * MIN_HAN_SHARE):
        return "ja"
    if found.hangul and found.hangul >= max(1, found.letters * MIN_HAN_SHARE):
        return "ko"
    if found.han and found.han >= found.letters * MIN_HAN_SHARE:
        return "zt" if found.traditional_only > found.simplified_only else "zh"
    if allow_english and found.han == found.kana == found.hangul == 0 and _looks_english(text):
        return "en"
    return None


def has_cjk(text: str) -> bool:
    """Whether the text contains Han, kana or Hangul (a single such character is a complete word, unlike a Latin letter)."""
    return bool(_HAN.search(text) or _KANA.search(text) or _HANGUL.search(text))


def _looks_english(text: str) -> bool:
    words = [word.lower() for word in _WORD.findall(text)]
    if len(words) < 2 or any(ord(character) > 0x7F and character.isalpha() for character in text):
        return False
    hits = sum(1 for word in words if word in _ENGLISH_WORDS)
    return hits >= 2 and hits / len(words) >= 0.4


def _encodable(character: str, codec: str) -> bool:
    try:
        character.encode(codec)
    except UnicodeEncodeError:
        return False
    return True
