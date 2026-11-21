"""A small least-recently-used cache of loaded translators.

A loaded package costs about 130 MB, so only a couple are kept: enough for a two-hop route. The key includes the
SHA-256 of ``model.bin``, so a re-imported package is never served by a stale translator.
"""

from __future__ import annotations

from collections import OrderedDict

from glyphlens_worker.engines.translation.backend import SegmentTranslator

CacheKey = tuple[str, str, str]  # (source, target, sha256 of model.bin)


class TranslatorCache:
    def __init__(self, capacity: int) -> None:
        if capacity < 1:
            raise ValueError("the cache must hold at least one translator")
        self._capacity = capacity
        self._items: OrderedDict[CacheKey, SegmentTranslator] = OrderedDict()

    def __len__(self) -> int:
        return len(self._items)

    def keys(self) -> list[CacheKey]:
        """The cached keys, least recently used first."""
        return list(self._items)

    def get(self, key: CacheKey) -> SegmentTranslator | None:
        translator = self._items.get(key)
        if translator is not None:
            self._items.move_to_end(key)
        return translator

    def put(self, key: CacheKey, translator: SegmentTranslator) -> None:
        self._items[key] = translator
        self._items.move_to_end(key)
        while len(self._items) > self._capacity:
            self._items.popitem(last=False)

    def drop_pair(self, source: str, target: str) -> None:
        """Forget every translator of a language pair (before its files are replaced)."""
        for key in [key for key in self._items if key[:2] == (source, target)]:
            del self._items[key]
