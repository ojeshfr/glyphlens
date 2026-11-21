"""Splitting a text into the segments the translation model sees, and putting the translations back together.

The Argos models translate one sentence at a time, and CTranslate2 silently truncates an input of more than about 1024
tokens (and output of more than 256), so the engine owns the splitting. Following Argos' algorithm:

1. a text is split into lines, keeping the line breaks exactly; blank lines and lines without a letter or digit are not
   translated and stay as they are;
2. a line is split into sentences after ``. ! ? ...`` when whitespace follows, and after the ideographic full stop and
   full-width ``!`` ``?`` even without whitespace (Chinese and Japanese put none between sentences);
3. a sentence longer than ``MAX_SEGMENT_TOKENS`` tokens is cut at punctuation (``, ; :``), then at spaces, then, for
   text without spaces, into blocks of characters, so no segment reaches CTranslate2's limits.

The sentence splitter is deliberately simple (an abbreviation such as ``M. Dupont`` splits wrongly); OCR text arrives
line by line anyway. ``SEGMENTER_REVISION`` is part of the model version string: change it whenever the splitting
changes, so cached translations made with the old behaviour are not reused.
"""

from __future__ import annotations

import re
from collections.abc import Callable, Sequence
from dataclasses import dataclass

#: Revision of the splitting rules; appears in the model version string as ``seg<N>``.
SEGMENTER_REVISION = 4

#: Tokens per segment (SentencePiece pieces). CTranslate2 truncates at 1024 input and 256 output tokens.
MAX_SEGMENT_TOKENS = 120

#: Sentence terminators: ASCII, the horizontal ellipsis, and the ideographic full stop and full-width ! and ?.
_SENTENCE_TERMINATORS = ".!?" + "".join(chr(code) for code in (0x2026, 0x3002, 0xFF01, 0xFF1F))
#: CJK clause separators: the ideographic comma and the full-width comma, semicolon and colon (no space follows them).
_CJK_CLAUSE_SEPARATORS = "".join(chr(code) for code in (0x3001, 0xFF0C, 0xFF1B, 0xFF1A))

_LINE_BREAK = re.compile(r"(\r\n|\n|\r)")
_CJK_TERMINATORS = "".join(chr(code) for code in (0x3002, 0xFF01, 0xFF1F))
#: Closing quotes and brackets that stay with the sentence they end.
_CLOSERS = "".join(chr(code) for code in (0x201D, 0x2019, 0x300D, 0x300F, 0xFF09, 0x3011))
_SENTENCE_BOUNDARY = re.compile(
    rf"(?<=[{_SENTENCE_TERMINATORS}])\s+(?=\S)"
    rf"|(?<=[{_CJK_TERMINATORS}])\s*(?![{_CLOSERS}{_CJK_TERMINATORS}\s])(?=\S)"
    rf"|(?<=[{_CJK_TERMINATORS}][{_CLOSERS}])\s*(?=\S)"
)
_CLAUSE_BOUNDARY = re.compile(rf"(?<=[,;:])\s+|(?<=[{_CJK_CLAUSE_SEPARATORS}])")
_WORD_BOUNDARY = re.compile(r"(?<=\s)(?=\S)")

TokenCounter = Callable[[str], int]


@dataclass(frozen=True)
class TextPlan:
    """How one text breaks into segments and how the translated segments reassemble.

    ``layout`` has one entry per piece of the text: a ``str`` that is copied unchanged (a line break, a blank line, a
    line without letters), or the indices into ``segments`` of the segments of one translated line.
    """

    layout: tuple[str | tuple[int, ...], ...]
    segments: tuple[str, ...]


def plan_text(text: str, count_tokens: TokenCounter, max_tokens: int = MAX_SEGMENT_TOKENS) -> TextPlan:
    """Split ``text`` into segments of at most about ``max_tokens`` tokens (as ``count_tokens`` counts them)."""
    layout: list[str | tuple[int, ...]] = []
    segments: list[str] = []
    for position, piece in enumerate(_LINE_BREAK.split(text)):
        is_line_break = position % 2 == 1
        if is_line_break or not any(ch.isalnum() for ch in piece):
            layout.append(piece)
            continue
        first = len(segments)
        for sentence in _SENTENCE_BOUNDARY.split(piece.strip()):
            segments.extend(_fit(sentence, count_tokens, max_tokens, level=0))
        layout.append(tuple(range(first, len(segments))))
    return TextPlan(tuple(layout), tuple(segments))


def assemble(plan: TextPlan, translations: Sequence[str]) -> str:
    """Rebuild the translated text: segments of a line are joined by a space, line breaks are kept."""
    parts: list[str] = []
    for part in plan.layout:
        if isinstance(part, str):
            parts.append(part)
        else:
            parts.append(" ".join(text for index in part if (text := translations[index])))
    return "".join(parts)


_BOUNDARIES = (_CLAUSE_BOUNDARY, _WORD_BOUNDARY)


def _fit(text: str, count_tokens: TokenCounter, max_tokens: int, level: int) -> list[str]:
    """Cut ``text`` into chunks of at most ``max_tokens`` tokens, preferring the coarsest boundary that works."""
    if count_tokens(text) <= max_tokens:
        return [text]
    if level >= len(_BOUNDARIES):
        return _fit_characters(text, count_tokens, max_tokens)

    pieces = _pieces(text, _BOUNDARIES[level])
    if len(pieces) == 1:
        return _fit(text, count_tokens, max_tokens, level + 1)

    chunks: list[str] = []
    current: list[str] = []
    current_tokens = 0
    for piece in pieces:
        tokens = count_tokens(piece)
        if tokens > max_tokens:
            _flush(chunks, current)
            current, current_tokens = [], 0
            chunks.extend(_fit(piece, count_tokens, max_tokens, level + 1))
            continue
        if current and current_tokens + tokens > max_tokens:
            _flush(chunks, current)
            current, current_tokens = [], 0
        current.append(piece)
        current_tokens += tokens
    _flush(chunks, current)
    return chunks


def _fit_characters(text: str, count_tokens: TokenCounter, max_tokens: int) -> list[str]:
    """Last resort for text without spaces: blocks sized from the text's own token density."""
    size = max(1, len(text) * max_tokens // max(count_tokens(text), 1))
    chunks: list[str] = []
    for start in range(0, len(text), size):
        block = text[start : start + size]
        if count_tokens(block) > max_tokens and len(block) > 1:
            chunks.extend(_fit_characters(block, count_tokens, max_tokens))
        else:
            chunks.append(block)
    return chunks


def _pieces(text: str, boundary: re.Pattern[str]) -> list[str]:
    """Cut ``text`` after every ``boundary`` match, so that joining the pieces gives the text back."""
    pieces: list[str] = []
    start = 0
    for match in boundary.finditer(text):
        if match.end() > start:
            pieces.append(text[start : match.end()])
            start = match.end()
    if start < len(text):
        pieces.append(text[start:])
    return pieces


def _flush(chunks: list[str], current: list[str]) -> None:
    if current:
        chunks.append("".join(current).strip())
