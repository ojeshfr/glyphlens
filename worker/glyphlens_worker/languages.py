"""The language codes the engines work with.

Translation runs Argos model packages, so the worker uses the codes Argos uses: two letters (``de``, ``zh``), plus
``pb`` (Brazilian Portuguese) and ``zt`` (Traditional Chinese). The application may send a BCP 47 form such as
``pt-BR`` or ``zh-Hant``; :func:`canonical_language` maps it to the Argos code.
"""

from __future__ import annotations

import re
from collections.abc import Mapping

#: Syntax of a language code on the wire (the same rule as ``GlyphLens.Core.Models.LanguageCode``).
LANGUAGE_CODE_PATTERN = re.compile(r"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$")

#: Every language Argos publishes a package for (as a source, and English), with its English name.
#: Derived from the official package index (49 into-English packages); see docs/research/TRANSLATION-COMPATIBILITY.md.
ARGOS_LANGUAGES: Mapping[str, str] = {
    "ar": "Arabic",
    "az": "Azerbaijani",
    "bg": "Bulgarian",
    "bn": "Bengali",
    "ca": "Catalan",
    "cs": "Czech",
    "da": "Danish",
    "de": "German",
    "el": "Greek",
    "en": "English",
    "eo": "Esperanto",
    "es": "Spanish",
    "et": "Estonian",
    "eu": "Basque",
    "fa": "Persian",
    "fi": "Finnish",
    "fr": "French",
    "ga": "Irish",
    "gl": "Galician",
    "he": "Hebrew",
    "hi": "Hindi",
    "hu": "Hungarian",
    "id": "Indonesian",
    "it": "Italian",
    "ja": "Japanese",
    "ko": "Korean",
    "ky": "Kyrgyz",
    "lt": "Lithuanian",
    "lv": "Latvian",
    "ms": "Malay",
    "nb": "Norwegian",
    "nl": "Dutch",
    "pb": "Portuguese (Brazil)",
    "pl": "Polish",
    "pt": "Portuguese",
    "ro": "Romanian",
    "ru": "Russian",
    "sk": "Slovak",
    "sl": "Slovenian",
    "sq": "Albanian",
    "sv": "Swedish",
    "sw": "Swahili",
    "th": "Thai",
    "tl": "Tagalog",
    "tr": "Turkish",
    "uk": "Ukrainian",
    "ur": "Urdu",
    "vi": "Vietnamese",
    "zh": "Chinese",
    "zt": "Chinese (traditional)",
}

#: Argos' variant codes and the language detectors' code for the same language (detectors know ``pt`` and ``zh`` only).
DETECTION_CODES: Mapping[str, str] = {"pb": "pt", "zt": "zh"}

#: Variants a model package may stand in for: a Portuguese request can use a Brazilian package and the reverse, and
#: likewise Simplified and Traditional Chinese. An exact package is always preferred.
VARIANT_ALIASES: Mapping[str, str] = {"pt": "pb", "pb": "pt", "zh": "zt", "zt": "zh"}

_TRADITIONAL_CHINESE_SUBTAGS = frozenset({"hant", "tw", "hk", "mo"})


def canonical_language(code: str) -> str:
    """Map a syntactically valid wire code to the Argos code: ``pt-BR`` -> ``pb``, ``zh-Hant`` -> ``zt``, ``de-AT`` -> ``de``."""
    primary, *subtags = code.lower().split("-")
    if primary == "pt" and "br" in subtags:
        return "pb"
    if primary == "zh" and _TRADITIONAL_CHINESE_SUBTAGS.intersection(subtags):
        return "zt"
    return primary


def is_known_language(code: str) -> bool:
    return code in ARGOS_LANGUAGES


def detection_languages() -> tuple[str, ...]:
    """The codes a language detector is built over: every Argos language, with the variants folded into their base language."""
    return tuple(sorted({DETECTION_CODES.get(code, code) for code in ARGOS_LANGUAGES}))


def package_name(source: str, target: str) -> str:
    """The conventional Argos package name for a pair, for example ``translate-de_en``."""
    return f"translate-{source}_{target}"
